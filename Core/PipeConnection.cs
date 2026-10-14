using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Models;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    public class PipeConnection : IPipeConnection
    {
        private readonly object _stateLock = new object();
        // Serializes writes: concurrent WriteAsync calls on one stream can interleave partial
        // writes and corrupt the newline framing.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        protected NamedPipeClientStream? _client;
        protected Stream? _stream;

        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _isConnected;
        private bool _disconnectRaised;
        private string _pipeName = string.Empty;

        // Requests awaiting a matching "response"/"errorResponse" envelope, keyed by the "cid"
        // this connection generated for the outgoing request. See SendAndWaitAsync.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pendingRequests =
            new ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>>();

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionEventArgs>? Connected;
        public event EventHandler<ConnectionEventArgs>? Disconnected;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                {
                    return _isConnected;
                }
            }
        }

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. Messages that
        /// exceed this before a newline terminator is found cause <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and the connection to be dropped. Takes
        /// effect on the next <see cref="ConnectAsync"/> call.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Raises <see cref="ErrorOccurred"/>, guarding each subscriber individually (see
        /// <see cref="SafeInvoke"/>) so a throwing subscriber can't affect this connection any
        /// more than a throwing <see cref="MessageReceived"/> subscriber can. A subscriber that
        /// throws here is itself swallowed rather than re-raised: there is no further event to
        /// escalate to without risking unbounded recursion if that subscriber throws on every
        /// call. The swallowed exception is written to <see cref="System.Diagnostics.Trace"/> (F-2)
        /// so a buggy ErrorOccurred handler doesn't produce total silence — see PROTOCOL.md.
        /// </summary>
        private void RaiseError(string message, Exception? ex)
        {
            SafeInvoke.EachHandler(ErrorOccurred, this, new ErrorEventArgs(message, ex), subscriberEx =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] ErrorOccurred subscriber threw and was swallowed: {subscriberEx}");
            });
        }

        public async Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("Pipe name cannot be null/empty.", nameof(pipeName));
            }

            if (IsConnected)
            {
                throw new InvalidOperationException("Connection is already established.");
            }

            try
            {
                _pipeName = pipeName;
                _client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _client.ConnectAsync(cancellationToken);
                _stream = _client;

                lock (_stateLock)
                {
                    _isConnected = true;
                    _disconnectRaised = false;
                    _receiveCts?.Dispose();
                    _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }

                Connected?.Invoke(this, new ConnectionEventArgs(pipeName, 0));
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _isConnected = false;
                }

                RaiseError(ex.Message, ex);
                throw;
            }
        }

        Task ITcpConnection.ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("PipeConnection does not support host/port ConnectAsync. Use ConnectAsync(pipeName, cancellationToken).");
        }

        public Task SendAsync(IEventMessage message)
        {
            return WriteEnvelopeAsync(new MessageEnvelope(message.EventName, message.PayloadValue));
        }

        private async Task WriteEnvelopeAsync(MessageEnvelope envelope)
        {
            if (!IsConnected || _stream == null)
            {
                throw new InvalidOperationException("Not connected.");
            }

            var bytes = Encoding.UTF8.GetBytes(envelope.Serialize() + "\n");

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
#if NETFRAMEWORK
                await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
#else
                await _stream.WriteAsync(bytes).ConfigureAwait(false);
#endif
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Sends <paramref name="message"/> as a request (wire "type":"request", stamped with a
        /// freshly generated "cid") and asynchronously waits for a matching "response" (or
        /// "errorResponse") envelope from the peer, correlated by that cid. See
        /// <see cref="TcpConnection.SendAndWaitAsync"/> for the full contract this mirrors.
        /// </summary>
        public async Task<IEventMessage> SendAndWaitAsync(IEventMessage message, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var cid = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[cid] = tcs;

            try
            {
                var envelope = new MessageEnvelope(message.EventName, message.PayloadValue)
                {
                    Cid = cid,
                    Type = MessageEnvelope.MessageTypeRequest,
                };
                await WriteEnvelopeAsync(envelope).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                using var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

                MessageEnvelope response;
                try
                {
                    response = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No response for request '{message.EventName}' (cid {cid}) within {timeout}.");
                }

                if (response.Type == MessageEnvelope.MessageTypeErrorResponse)
                {
                    throw new RequestFailedException(response.Error?.Code, response.Error?.Message ?? "Request failed.");
                }

                return new EventMessage(response.EventName, response.PayloadValue);
            }
            finally
            {
                _pendingRequests.TryRemove(cid, out _);
            }
        }

        private void FailPendingRequests()
        {
            foreach (var cid in _pendingRequests.Keys)
            {
                if (_pendingRequests.TryRemove(cid, out var tcs))
                {
                    tcs.TrySetException(new InvalidOperationException("Connection closed while awaiting a response."));
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            if (_stream == null)
            {
                return;
            }

            var reader = new BoundedLineReader(_stream, MaxMessageSizeBytes);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    MessageEnvelope msg;
                    try
                    {
                        msg = MessageEnvelope.Deserialize(line);
                    }
                    catch (Exception ex) when (ReceiveLoopErrors.IsRecoverableParseFailure(ex))
                    {
                        // Recoverable: the line boundary is intact, so resynchronization is free.
                        // Skip this message and keep reading rather than killing the connection.
                        // Anything not a genuine parse failure (UnsupportedProtocolVersionException,
                        // OperationCanceledException, or a non-parse bug) falls through this filter
                        // and is handled by the outer catch below instead.
                        RaiseError(ex.Message, ex);
                        continue;
                    }

                    if ((msg.Type == MessageEnvelope.MessageTypeResponse || msg.Type == MessageEnvelope.MessageTypeErrorResponse)
                        && msg.Cid != null && _pendingRequests.TryRemove(msg.Cid, out var pendingTcs))
                    {
                        pendingTcs.TrySetResult(msg);
                        continue;
                    }

                    SafeInvoke.EachHandler(MessageReceived, this,
                        new MessageReceivedEventArgs(new EventMessage(msg.EventName, msg.PayloadValue)),
                        ex => RaiseError("Unhandled exception in MessageReceived subscriber", ex));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the connection.
            }
            catch (Exception ex)
            {
                RaiseError(ex.Message, ex);
            }
            finally
            {
                FailPendingRequests();
                RaiseDisconnected();
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                CancellationTokenSource? cts;
                Task? receiveTask;

                lock (_stateLock)
                {
                    cts = _receiveCts;
                    receiveTask = _receiveTask;
                    _isConnected = false;
                }

                cts?.Cancel();

                if (_stream != null)
                {
#if NETFRAMEWORK
                    _stream.Dispose();
#else
                    await _stream.DisposeAsync();
#endif
                    _stream = null;
                }

                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                if (receiveTask != null)
                {
                    await receiveTask;
                }

                RaiseDisconnected();
            }
            catch (Exception ex)
            {
                RaiseError(ex.Message, ex);
                throw;
            }
            finally
            {
                lock (_stateLock)
                {
                    _isConnected = false;
                    _receiveTask = null;
                }
            }
        }

        public void Dispose()
        {
            _receiveCts?.Dispose();
            _client?.Dispose();
        }

        private void RaiseDisconnected()
        {
            ConnectionEventArgs? args = null;

            lock (_stateLock)
            {
                if (_disconnectRaised)
                {
                    return;
                }

                _disconnectRaised = true;
                _isConnected = false;

                args = new ConnectionEventArgs(_pipeName, 0);
            }

            // Guarded like TcpConnection.RaiseDisconnected: this fires from ReceiveLoopAsync's
            // finally, so a throwing subscriber must not be allowed to escape and abort
            // CloseAsync's own cleanup.
            SafeInvoke.EachHandler(Disconnected, this, args, ex =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] Disconnected subscriber threw and was swallowed: {ex}");
            });
        }
    }
}

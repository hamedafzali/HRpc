using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    public class TcpConnection : ITcpConnection
    {
        protected TcpClient _client = new TcpClient();
        protected NetworkStream? _stream;

        private readonly object _stateLock = new object();
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _isConnected;
        private bool _disconnectRaised;

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

        public virtual async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("Connection is already established.");
            }

            try
            {
                _client = new TcpClient();
#if NETFRAMEWORK
                cancellationToken.ThrowIfCancellationRequested();
                await _client.ConnectAsync(host, port);
#else
                await _client.ConnectAsync(host, port, cancellationToken);
#endif
                _stream = _client.GetStream();

                lock (_stateLock)
                {
                    _isConnected = true;
                    _disconnectRaised = false;
                    _receiveCts?.Dispose();
                    _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }

                Connected?.Invoke(this, new ConnectionEventArgs(host, port));
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

        public async Task SendAsync(IEventMessage message)
        {
            if (!IsConnected || _stream == null)
            {
                throw new InvalidOperationException("Not connected.");
            }

            var envelope = new MessageEnvelope(message.EventName, message.PayloadValue);

            var bytes = Encoding.UTF8.GetBytes(envelope.Serialize() + "\n");

#if NETFRAMEWORK
            await _stream.WriteAsync(bytes, 0, bytes.Length);
#else
            await _stream.WriteAsync(bytes);
#endif
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

                _client.Close();

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
            _client.Dispose();
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

                var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
                args = new ConnectionEventArgs(
                    endpoint?.Address.ToString() ?? string.Empty,
                    endpoint?.Port ?? 0
                );
            }

            Disconnected?.Invoke(this, args);
        }
    }
}

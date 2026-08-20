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
    public class PipeServer : ITcpServer, IDisposable
    {
        private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clientTasks = new ConcurrentDictionary<NamedPipeServerStream, Task>();
        private CancellationTokenSource? _serverCts;
        private volatile bool _running;
        private string _pipeName = string.Empty;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        // Optional message to send upon client connection
        public IEventMessage? InitialMessage { get; set; }

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. A client that
        /// exceeds this before sending a newline terminator causes <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and that client's connection to be
        /// closed. Applies to connections accepted after this is set.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Raises <see cref="ErrorOccurred"/>, guarding each subscriber individually (see
        /// <see cref="SafeInvoke"/>) so a throwing subscriber can't affect the server any more
        /// than a throwing <see cref="MessageReceived"/> subscriber can. A subscriber that
        /// throws here is itself swallowed rather than re-raised: there is no further event to
        /// escalate to without risking unbounded recursion if that subscriber throws on every
        /// call. The swallowed exception is written to <see cref="System.Diagnostics.Trace"/> (F-2)
        /// so a buggy ErrorOccurred handler doesn't produce total silence — see PROTOCOL.md.
        /// </summary>
        protected void OnErrorOccurred(string message, Exception? ex = null)
        {
            SafeInvoke.EachHandler(ErrorOccurred, this, new ErrorEventArgs(message, ex), subscriberEx =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] ErrorOccurred subscriber threw and was swallowed: {subscriberEx}");
            });
        }

        public Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            return Task.FromException(new NotSupportedException(
                "PipeServer does not support port-based StartAsync. Use StartAsync(pipeName, cancellationToken)."));
        }

        public async Task StartAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("Pipe name cannot be null/empty.", nameof(pipeName));
            }

            if (_running)
            {
                throw new InvalidOperationException("Server is already running.");
            }

            _pipeName = pipeName;
            _running = true;
            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var serverToken = _serverCts.Token;

            try
            {
                while (!serverToken.IsCancellationRequested)
                {
                    var serverStream = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );

                    var task = AcceptAndHandleClientAsync(serverStream, serverToken);
                    _clientTasks[serverStream] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        _clientTasks.TryRemove(serverStream, out var _);
                    }, TaskScheduler.Default);

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in StartAsync", ex);
                throw;
            }
            finally
            {
                _running = false;
            }
        }

        private async Task AcceptAndHandleClientAsync(NamedPipeServerStream serverStream, CancellationToken cancellationToken)
        {
            try
            {
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                ClientConnected?.Invoke(this, new ConnectionEventArgs(_pipeName, 0));

                await HandleClientAsync(serverStream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
                try { serverStream.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                try { serverStream.Dispose(); } catch { }
                OnErrorOccurred("Error accepting pipe client", ex);
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            var reader = new BoundedLineReader(stream, MaxMessageSizeBytes);

            try
            {
                // Send initial message if configured
                if (InitialMessage != null)
                {
                    var envelope = new MessageEnvelope(InitialMessage.EventName, InitialMessage.PayloadValue);
                    var bytes = Encoding.UTF8.GetBytes(envelope.Serialize() + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    MessageEnvelope message;
                    try
                    {
                        message = MessageEnvelope.Deserialize(line);
                    }
                    catch (Exception ex) when (ReceiveLoopErrors.IsRecoverableParseFailure(ex))
                    {
                        // Recoverable: the line boundary is intact, so resynchronization is free.
                        // Skip this message and keep reading rather than killing the connection.
                        // Anything not a genuine parse failure (UnsupportedProtocolVersionException,
                        // OperationCanceledException, or a non-parse bug) falls through this filter
                        // and is handled by the outer catch below instead.
                        OnErrorOccurred(ex.Message, ex);
                        continue;
                    }

                    SafeInvoke.EachHandler(MessageReceived, this,
                        new MessageReceivedEventArgs(new EventMessage(message.EventName, message.PayloadValue)),
                        ex => OnErrorOccurred("Unhandled exception in MessageReceived subscriber", ex));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in HandleClientAsync", ex);
            }
            finally
            {
                ClientDisconnected?.Invoke(this, new ConnectionEventArgs(_pipeName, 0));

                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        public async Task StopAsync()
        {
            _running = false;

            var cts = _serverCts;
            cts?.Cancel();

            foreach (var kvp in _clientTasks)
            {
                try
                {
                    kvp.Key.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            var runningTasks = _clientTasks.Values;
            await Task.WhenAll(runningTasks).ConfigureAwait(false);

            _clientTasks.Clear();
            cts?.Dispose();
            _serverCts = null;
        }

        public void Dispose()
        {
            try
            {
                _running = false;

            }
            catch (Exception)
            {
                // ignore
            }
        }
    }
}

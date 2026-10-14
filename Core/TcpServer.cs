using System;
using System.Collections.Concurrent;
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
    public class TcpServer : ITcpServer
    {
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new ConcurrentDictionary<TcpClient, Task>();
        private CancellationTokenSource? _serverCts;
        private volatile bool _running;
        private Task? _acceptLoopTask;

        // Registered by OnRequest, consulted by HandleClientAsync when an incoming envelope is
        // "type":"request". See OnRequest's doc comment.
        private readonly ConcurrentDictionary<string, Func<IEventMessage, Task<object?>>> _requestHandlers =
            new ConcurrentDictionary<string, Func<IEventMessage, Task<object?>>>();

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. A client that
        /// exceeds this before sending a newline terminator causes <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and that client's connection to be
        /// closed. Applies to connections accepted after this is set.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Registers <paramref name="handler"/> to answer incoming "type":"request" envelopes
        /// (sent by a peer's <see cref="TcpConnection.SendAndWaitAsync"/>/
        /// <see cref="PipeConnection.SendAndWaitAsync"/>) whose "eventName" matches
        /// <paramref name="eventName"/>. The handler's return value becomes the "response"
        /// envelope's payload; a thrown exception is translated into an "errorResponse" envelope
        /// instead of propagating to <see cref="ErrorOccurred"/> or killing the connection.
        /// Registering again for the same <paramref name="eventName"/> replaces the previous
        /// handler. A "request" envelope for an eventName with no registered handler falls
        /// through to <see cref="MessageReceived"/> like any other message, so this is purely
        /// additive to the existing pub/sub surface.
        /// </summary>
        public void OnRequest(string eventName, Func<IEventMessage, Task<object?>> handler)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentException("Event name cannot be null/empty.", nameof(eventName));
            }

            _requestHandlers[eventName] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Raises <see cref="ErrorOccurred"/>, guarding each subscriber individually (see
        /// <see cref="SafeInvoke"/>) so a throwing subscriber can't affect the server any more
        /// than a throwing <see cref="MessageReceived"/> subscriber can. A subscriber that
        /// throws here is itself swallowed rather than re-raised: there is no further event to
        /// escalate to without risking unbounded recursion if that subscriber throws on every
        /// call. The swallowed exception is written to <see cref="System.Diagnostics.Trace"/> (F-2)
        /// so a buggy ErrorOccurred handler doesn't produce total silence — see docs/PROTOCOL.md.
        /// </summary>
        protected void OnErrorOccurred(string message, Exception? ex = null)
        {
            SafeInvoke.EachHandler(ErrorOccurred, this, new ErrorEventArgs(message, ex), subscriberEx =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] ErrorOccurred subscriber threw and was swallowed: {subscriberEx}");
            });
        }

        /// <summary>
        /// Binds and starts listening synchronously, then returns -- it does NOT wait for the
        /// server to stop. The accept loop that used to run inline here (so the returned Task
        /// only completed on shutdown, meaning `await StartAsync(...)` never returned while the
        /// server was up) now runs as a detached background loop tracked by
        /// <see cref="_acceptLoopTask"/>, which <see cref="StopAsync"/> awaits during teardown.
        /// Synchronous setup failures (bad port, port in use) still throw directly from this
        /// call, as before; failures during the background accept loop itself are reported via
        /// <see cref="ErrorOccurred"/>, matching how <see cref="HandleClientAsync"/> already
        /// reports its own background failures.
        /// </summary>
        public Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            if (_running)
            {
                throw new InvalidOperationException("Server is already running.");
            }

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _acceptLoopTask = RunAcceptLoopAsync(_listener, _serverCts.Token);

            return Task.CompletedTask;
        }

        private async Task RunAcceptLoopAsync(TcpListener listener, CancellationToken serverToken)
        {
            // On net48, AcceptTcpClientAsync() has no CancellationToken-aware overload, so a
            // pending accept never observes serverToken becoming cancelled on its own. Stop()
            // disposes the listener socket, unblocking the pending accept as an
            // ObjectDisposedException, which the catch below treats as clean shutdown since
            // serverToken.IsCancellationRequested is true. On net8.0/net9.0 this is redundant
            // with AcceptTcpClientAsync's native cancellation support but harmless.
            using var cancellationRegistration = serverToken.Register(() =>
            {
                try { listener.Stop(); } catch (ObjectDisposedException) { }
            });

            try
            {
                while (!serverToken.IsCancellationRequested)
                {
                    TcpClient client;
#if NETFRAMEWORK
                    serverToken.ThrowIfCancellationRequested();
                    client = await listener.AcceptTcpClientAsync();
#else
                    client = await listener.AcceptTcpClientAsync(serverToken);
#endif
                    // Captured once, at accept time, rather than read again from client.Client
                    // in HandleClientAsync's finally: by teardown time the socket may already be
                    // disposed, and RemoteEndPoint throws ObjectDisposedException on a disposed
                    // socket on every target framework (the same hazard as TcpConnection's
                    // RaiseDisconnected — see CHANGELOG).
                    var endPoint = client.Client.RemoteEndPoint as IPEndPoint;

                    ClientConnected?.Invoke(this, new ConnectionEventArgs(
                        endPoint?.Address.ToString() ?? string.Empty,
                        endPoint?.Port ?? 0
                    ));

                    var task = HandleClientAsync(client, endPoint, serverToken);
                    _clientTasks[client] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        _clientTasks.TryRemove(client, out var _);
                    }, TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (ObjectDisposedException) when (serverToken.IsCancellationRequested)
            {
                // TcpListener.Stop() (called from StopAsync) disposes the underlying socket while
                // an AcceptTcpClientAsync is pending. On net48 that has no overload accepting a
                // CancellationToken, so the pending accept observes the disposal as
                // ObjectDisposedException rather than cancellation. Only swallow it when we know
                // shutdown was requested by us; otherwise fall through and report it, since it may
                // be a genuine, unrelated disposal.
            }
            catch (Exception ex)
            {
                // Nobody awaits this loop's Task directly anymore (StartAsync already returned),
                // so there is no caller left to propagate to -- report it the same way
                // HandleClientAsync reports its own background failures.
                OnErrorOccurred("Error in StartAsync", ex);
            }
            finally
            {
                _running = false;
            }
        }

        private async Task HandleClientAsync(TcpClient client, IPEndPoint? endPoint, CancellationToken cancellationToken)
        {
#if NETFRAMEWORK
            using var stream = client.GetStream();
#else
            await using var stream = client.GetStream();
#endif
            var reader = new BoundedLineReader(stream, MaxMessageSizeBytes);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
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

                    if (message.Type == MessageEnvelope.MessageTypeRequest && message.Cid != null
                        && _requestHandlers.TryGetValue(message.EventName, out var handler))
                    {
                        await HandleRequestAsync(stream, message, handler, cancellationToken).ConfigureAwait(false);
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
                // Guarded so a throwing ClientDisconnected subscriber can't prevent client.Close()
                // from running below.
                SafeInvoke.EachHandler(ClientDisconnected, this, new ConnectionEventArgs(
                    endPoint?.Address.ToString() ?? string.Empty,
                    endPoint?.Port ?? 0
                ), ex =>
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[HRpc] ClientDisconnected subscriber threw and was swallowed: {ex}");
                });

                client.Close();
            }
        }

        /// <summary>
        /// Invokes a registered request handler and writes back a "response" (or, on a thrown
        /// exception, "errorResponse") envelope carrying the same "cid" -- directly on this
        /// client's own stream, since a reply only ever needs to reach the client that sent the
        /// request it answers, not any other connected client.
        /// </summary>
        private async Task HandleRequestAsync(Stream stream, MessageEnvelope request, Func<IEventMessage, Task<object?>> handler, CancellationToken cancellationToken)
        {
            MessageEnvelope response;
            try
            {
                var result = await handler(new EventMessage(request.EventName, request.PayloadValue)).ConfigureAwait(false);
                response = new MessageEnvelope(request.EventName, result)
                {
                    Cid = request.Cid,
                    Type = MessageEnvelope.MessageTypeResponse,
                };
            }
            catch (Exception ex)
            {
                response = new MessageEnvelope(request.EventName, (object?)null)
                {
                    Cid = request.Cid,
                    Type = MessageEnvelope.MessageTypeErrorResponse,
                    Error = new MessageError { Code = ex.GetType().Name, Message = ex.Message },
                };
            }

            var bytes = Encoding.UTF8.GetBytes(response.Serialize() + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            _running = false;

            _serverCts?.Cancel();
            _listener?.Stop();

            if (_acceptLoopTask != null)
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }

            var runningTasks = _clientTasks.Values;
            await Task.WhenAll(runningTasks);

            _clientTasks.Clear();
            _serverCts?.Dispose();
            _serverCts = null;
            _acceptLoopTask = null;
        }
    }
}

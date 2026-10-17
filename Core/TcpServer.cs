using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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

        // Request handlers, connected clients and push. See ServerCore.
        private readonly ServerCore _core;
        private int _maxConcurrentRequests = 1;
        private TimeSpan? _keepAlive;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public TcpServer()
        {
            _core = new ServerCore(this, () => ClientConnected, () => ClientDisconnected, () => MessageReceived,
                (message, ex) => OnErrorOccurred(message, ex));
        }

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. A client that
        /// exceeds this before sending a newline terminator causes <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and that client's connection to be
        /// closed. Applies to connections accepted after this is set.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// How many <see cref="OnRequest(string, Func{IEventMessage, Task{object}})"/> handlers may
        /// run at once for a single client. The default, 1, runs them one at a time in arrival
        /// order, and a message received after a request is delivered only once that request's
        /// handler has finished: exactly the 1.3 behavior. Higher values let a slow handler run
        /// alongside later requests and events from the same client. Applies to connections
        /// accepted after this is set.
        /// </summary>
        public int MaxConcurrentRequests
        {
            get => _maxConcurrentRequests;
            set
            {
                ServerCore.ValidateMaxConcurrentRequests(value);
                _maxConcurrentRequests = value;
            }
        }

        /// <summary>
        /// Turns on TCP keepalive with this idle time on accepted connections, so a silently
        /// dropped client is detected and <see cref="ClientDisconnected"/> fires. Null (the
        /// default) leaves the OS default, usually off. Nothing changes on the wire. Applies to
        /// connections accepted after this is set.
        /// </summary>
        public TimeSpan? KeepAlive
        {
            get => _keepAlive;
            set
            {
                SocketKeepAlive.Validate(value);
                _keepAlive = value;
            }
        }

        /// <summary>
        /// Ids of the clients connected right now, as passed in
        /// <see cref="ConnectionEventArgs.ClientId"/> and <see cref="MessageReceivedEventArgs.ClientId"/>.
        /// </summary>
        public IReadOnlyCollection<string> ConnectedClients => _core.ConnectedClients;

        /// <summary>
        /// Pushes <paramref name="message"/> to one client as an ordinary event, which any client
        /// version receives through MessageReceived. Throws <see cref="InvalidOperationException"/>
        /// if no client with that id is connected.
        /// </summary>
        public Task SendAsync(string clientId, IEventMessage message)
        {
            return _core.SendAsync(clientId, message);
        }

        /// <summary>
        /// Pushes <paramref name="message"/> to every connected client and returns how many it
        /// reached. A client that disconnects during the broadcast is skipped, not reported.
        /// </summary>
        public Task<int> BroadcastAsync(IEventMessage message)
        {
            return _core.BroadcastAsync(message);
        }

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
            _core.OnRequest(eventName, handler);
        }

        /// <summary>
        /// Like <see cref="OnRequest(string, Func{IEventMessage, Task{object}})"/>, but the handler
        /// also gets a <see cref="RequestContext"/> with the calling client's id and a
        /// cancellation token that trips when the client cancels or disconnects, or the server
        /// stops.
        /// </summary>
        public void OnRequest(string eventName, Func<IEventMessage, RequestContext, Task<object?>> handler)
        {
            _core.OnRequest(eventName, handler);
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

                    var keepAlive = _keepAlive;
                    if (keepAlive.HasValue)
                    {
                        try
                        {
                            SocketKeepAlive.Apply(client.Client, keepAlive.Value);
                        }
                        catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                        {
                            OnErrorOccurred("Failed to enable TCP keepalive", ex);
                        }
                    }

                    // HandleClientAsync raises ClientConnected synchronously, before the next accept.
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
            try
            {
#if NETFRAMEWORK
                using var stream = client.GetStream();
#else
                await using var stream = client.GetStream();
#endif
                await _core.RunClientAsync(stream, endPoint?.Address.ToString() ?? string.Empty, endPoint?.Port ?? 0,
                    MaxMessageSizeBytes, _maxConcurrentRequests, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in HandleClientAsync", ex);
            }
            finally
            {
                client.Close();
            }
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

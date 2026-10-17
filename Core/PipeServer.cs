using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
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
        private Task? _acceptLoopTask;

        // The one instance waiting for a client, disposed by StopAsync to unblock the accept loop.
        private volatile NamedPipeServerStream? _waitingStream;

        // Request handlers, connected clients and push. See ServerCore.
        private readonly ServerCore _core;
        private int _maxConcurrentRequests = 1;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public PipeServer()
        {
            _core = new ServerCore(this, () => ClientConnected, () => ClientDisconnected, () => MessageReceived,
                (message, ex) => OnErrorOccurred(message, ex));
        }

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
        /// How many request handlers may run at once for a single client. See
        /// <see cref="TcpServer.MaxConcurrentRequests"/>; the default, 1, is the 1.3 behavior.
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
        /// Only accept clients running as the current user. Off by default, so any local user can
        /// connect, as in 1.3. On net8.0+ this is <see cref="PipeOptions"/>.CurrentUserOnly; on
        /// net48 the pipe gets an ACL granting only the current user. Takes effect on the next
        /// <see cref="StartAsync(string, CancellationToken)"/>.
        /// </summary>
        public bool CurrentUserOnly { get; set; }

        /// <summary>Ids of the clients connected right now. See <see cref="TcpServer.ConnectedClients"/>.</summary>
        public IReadOnlyCollection<string> ConnectedClients => _core.ConnectedClients;

        /// <summary>Pushes an event to one client. See <see cref="TcpServer.SendAsync"/>.</summary>
        public Task SendAsync(string clientId, IEventMessage message)
        {
            return _core.SendAsync(clientId, message);
        }

        /// <summary>Pushes an event to every connected client. See <see cref="TcpServer.BroadcastAsync"/>.</summary>
        public Task<int> BroadcastAsync(IEventMessage message)
        {
            return _core.BroadcastAsync(message);
        }

        /// <summary>
        /// Registers <paramref name="handler"/> to answer incoming "type":"request" envelopes.
        /// See <see cref="TcpServer.OnRequest(string, Func{IEventMessage, Task{object}})"/> for the
        /// full contract this mirrors.
        /// </summary>
        public void OnRequest(string eventName, Func<IEventMessage, Task<object?>> handler)
        {
            _core.OnRequest(eventName, handler);
        }

        /// <summary>
        /// Registers a handler that also gets a <see cref="RequestContext"/>. See
        /// <see cref="TcpServer.OnRequest(string, Func{IEventMessage, RequestContext, Task{object}})"/>.
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

        public Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            return Task.FromException(new NotSupportedException(
                "PipeServer does not support port-based StartAsync. Use StartAsync(pipeName, cancellationToken)."));
        }

        /// <summary>
        /// Sets up server state synchronously, then returns -- it does NOT wait for the server
        /// to stop. The accept loop that used to run inline here (so the returned Task only
        /// completed on shutdown, meaning `await StartAsync(...)` never returned while the
        /// server was up) now runs as a detached background loop tracked by
        /// <see cref="_acceptLoopTask"/>, which <see cref="StopAsync"/> awaits during teardown.
        /// Synchronous validation failures (bad pipe name, already running) still throw directly
        /// from this call, as before; failures during the background accept loop itself are
        /// reported via <see cref="ErrorOccurred"/>, matching how
        /// <see cref="HandleClientAsync"/> already reports its own background failures.
        /// </summary>
        public Task StartAsync(string pipeName, CancellationToken cancellationToken = default)
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

            _acceptLoopTask = RunAcceptLoopAsync(pipeName, _serverCts.Token);

            return Task.CompletedTask;
        }

        private async Task RunAcceptLoopAsync(string pipeName, CancellationToken serverToken)
        {
            var currentUserOnly = CurrentUserOnly;
            NamedPipeServerStream? spare = null;

            try
            {
                // Two instances exist at a time: one waiting for a client, plus an idle spare
                // that becomes the next waiting instance. Before 1.4 a new instance was created
                // on every loop iteration without waiting, which leaked instances and spun a CPU
                // core while idle. One instance alone is not enough:
                // - On Unix, instances of one name share a listening socket that closes with the
                //   last instance. If a short-lived client's instance is disposed before the next
                //   one exists, clients already queued on that socket are silently dropped:
                //   their connect succeeded, but no server ever sees them.
                // - On Windows, a client that arrives while no instance exists has to retry.
                spare = PipeAccess.CreateServer(pipeName, currentUserOnly);

                while (!serverToken.IsCancellationRequested)
                {
                    var serverStream = spare;
                    spare = PipeAccess.CreateServer(pipeName, currentUserOnly);
                    _waitingStream = serverStream;

                    try
                    {
                        await serverStream.WaitForConnectionAsync(serverToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try { serverStream.Dispose(); } catch { }

                        if (serverToken.IsCancellationRequested)
                        {
                            break;
                        }

                        // A client that connects and vanishes at once can fail the wait; keep
                        // serving the others. The pause stops a persistent failure from spinning.
                        OnErrorOccurred("Error accepting pipe client", ex);
                        await Task.Delay(50, serverToken).ConfigureAwait(false);
                        continue;
                    }
                    finally
                    {
                        _waitingStream = null;
                    }

                    var task = HandleClientAsync(serverStream, serverToken);
                    _clientTasks[serverStream] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        _clientTasks.TryRemove(serverStream, out var _);
                    }, TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                // Nobody awaits this loop's Task directly anymore (StartAsync already returned),
                // so there is no caller left to propagate to.
                OnErrorOccurred("Error in StartAsync", ex);
            }
            finally
            {
                try { spare?.Dispose(); } catch { }
                _running = false;
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            try
            {
                await _core.RunClientAsync(stream, _pipeName, 0, MaxMessageSizeBytes, _maxConcurrentRequests,
                    InitialMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in HandleClientAsync", ex);
            }
            finally
            {
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

            try
            {
                _waitingStream?.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }

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

            if (_acceptLoopTask != null)
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }

            var runningTasks = _clientTasks.Values;
            await Task.WhenAll(runningTasks).ConfigureAwait(false);

            _clientTasks.Clear();
            cts?.Dispose();
            _serverCts = null;
            _acceptLoopTask = null;
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

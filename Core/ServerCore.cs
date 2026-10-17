using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Models;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    /// <summary>
    /// Transport-independent server logic shared by <see cref="TcpServer"/> and
    /// <see cref="PipeServer"/>: request handlers, the connected-client registry, server push, and
    /// raising the owner's events with the owner as sender. The transport accepts the stream.
    /// </summary>
    internal sealed class ServerCore
    {
        private readonly object _owner;
        private readonly Func<EventHandler<ConnectionEventArgs>?> _clientConnected;
        private readonly Func<EventHandler<ConnectionEventArgs>?> _clientDisconnected;
        private readonly Func<EventHandler<MessageReceivedEventArgs>?> _messageReceived;
        private readonly Action<string, Exception?> _raiseError;

        private readonly ConcurrentDictionary<string, Func<IEventMessage, RequestContext, Task<object?>>> _requestHandlers =
            new ConcurrentDictionary<string, Func<IEventMessage, RequestContext, Task<object?>>>();

        private readonly ConcurrentDictionary<string, MessageSession> _clients =
            new ConcurrentDictionary<string, MessageSession>();

        public ServerCore(
            object owner,
            Func<EventHandler<ConnectionEventArgs>?> clientConnected,
            Func<EventHandler<ConnectionEventArgs>?> clientDisconnected,
            Func<EventHandler<MessageReceivedEventArgs>?> messageReceived,
            Action<string, Exception?> raiseError)
        {
            _owner = owner;
            _clientConnected = clientConnected;
            _clientDisconnected = clientDisconnected;
            _messageReceived = messageReceived;
            _raiseError = raiseError;
        }

        public IReadOnlyCollection<string> ConnectedClients => _clients.Keys.ToArray();

        public static void ValidateMaxConcurrentRequests(int value)
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentRequests must be at least 1.");
            }
        }

        public void OnRequest(string eventName, Func<IEventMessage, Task<object?>> handler)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentException("Event name cannot be null/empty.", nameof(eventName));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            _requestHandlers[eventName] = (message, _) => handler(message);
        }

        public void OnRequest(string eventName, Func<IEventMessage, RequestContext, Task<object?>> handler)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentException("Event name cannot be null/empty.", nameof(eventName));
            }

            _requestHandlers[eventName] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Runs one accepted client until it disconnects or the server stops: raises
        /// ClientConnected, writes <paramref name="initialMessage"/> if any, runs the session, then
        /// raises ClientDisconnected. Never throws; the caller disposes the stream afterwards.
        /// </summary>
        public async Task RunClientAsync(Stream stream, string remoteAddress, int remotePort, int maxMessageSizeBytes,
            int maxConcurrentRequests, IEventMessage? initialMessage, CancellationToken cancellationToken)
        {
            var clientId = Guid.NewGuid().ToString("N");
            var session = MessageSession.ForServer(stream, maxMessageSizeBytes, clientId,
                envelope => RaiseMessage(envelope, clientId), _raiseError, ResolveHandler, maxConcurrentRequests);

            // Registered before ClientConnected so a subscriber can push to the new client.
            _clients[clientId] = session;

            try
            {
                SafeInvoke.EachHandler(_clientConnected(), _owner,
                    new ConnectionEventArgs(remoteAddress, remotePort, clientId),
                    ex => _raiseError("Unhandled exception in ClientConnected subscriber", ex));

                if (initialMessage != null)
                {
                    await session.WriteAsync(new MessageEnvelope(initialMessage.EventName, initialMessage.PayloadValue),
                        cancellationToken).ConfigureAwait(false);
                }

                await session.RunAsync(cancellationToken, "Error in HandleClientAsync").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                _raiseError("Error in HandleClientAsync", ex);
            }
            finally
            {
                _clients.TryRemove(clientId, out _);

                // Guarded so a throwing ClientDisconnected subscriber can't prevent the caller's
                // cleanup from running.
                SafeInvoke.EachHandler(_clientDisconnected(), _owner,
                    new ConnectionEventArgs(remoteAddress, remotePort, clientId), ex =>
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"[HRpc] ClientDisconnected subscriber threw and was swallowed: {ex}");
                    });
            }
        }

        public async Task SendAsync(string clientId, IEventMessage message)
        {
            if (clientId == null)
            {
                throw new ArgumentNullException(nameof(clientId));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (!_clients.TryGetValue(clientId, out var session))
            {
                throw new InvalidOperationException($"No connected client with id '{clientId}'.");
            }

            try
            {
                await session.WriteAsync(new MessageEnvelope(message.EventName, message.PayloadValue)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                throw new InvalidOperationException($"Client '{clientId}' disconnected.", ex);
            }
        }

        public async Task<int> BroadcastAsync(IEventMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var sessions = _clients.Values.ToArray();
            var sends = sessions.Select(async session =>
            {
                try
                {
                    // One envelope per client: Serialize() is cheap next to the write itself.
                    await session.WriteAsync(new MessageEnvelope(message.EventName, message.PayloadValue)).ConfigureAwait(false);
                    return 1;
                }
                catch (Exception)
                {
                    // A client that disconnects mid-broadcast is normal, not an error.
                    return 0;
                }
            });

            var results = await Task.WhenAll(sends).ConfigureAwait(false);
            return results.Sum();
        }

        private Func<IEventMessage, RequestContext, Task<object?>>? ResolveHandler(string eventName)
        {
            return _requestHandlers.TryGetValue(eventName, out var handler) ? handler : null;
        }

        private void RaiseMessage(MessageEnvelope envelope, string clientId)
        {
            SafeInvoke.EachHandler(_messageReceived(), _owner,
                new MessageReceivedEventArgs(new EventMessage(envelope.EventName, envelope.PayloadValue), clientId),
                ex => _raiseError("Unhandled exception in MessageReceived subscriber", ex));
        }
    }
}

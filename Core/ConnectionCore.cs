using System;
using System.IO;
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
    /// Client-side connection lifecycle shared by <see cref="TcpConnection"/> and
    /// <see cref="PipeConnection"/>: connected state, the receive task, close, and raising the
    /// owner's events with the owner as sender. The transport opens and disposes the stream.
    /// </summary>
    internal sealed class ConnectionCore
    {
        private readonly object _owner;
        private readonly Func<EventHandler<MessageReceivedEventArgs>?> _messageReceived;
        private readonly Func<EventHandler<ConnectionEventArgs>?> _connected;
        private readonly Func<EventHandler<ConnectionEventArgs>?> _disconnected;
        private readonly Func<EventHandler<ErrorEventArgs>?> _errorOccurred;
        private readonly Func<ConnectionEventArgs> _disconnectArgs;

        private readonly object _stateLock = new object();
        private MessageSession? _session;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _isConnected;
        private bool _disconnectRaised;

        public ConnectionCore(
            object owner,
            Func<EventHandler<MessageReceivedEventArgs>?> messageReceived,
            Func<EventHandler<ConnectionEventArgs>?> connected,
            Func<EventHandler<ConnectionEventArgs>?> disconnected,
            Func<EventHandler<ErrorEventArgs>?> errorOccurred,
            Func<ConnectionEventArgs> disconnectArgs)
        {
            _owner = owner;
            _messageReceived = messageReceived;
            _connected = connected;
            _disconnected = disconnected;
            _errorOccurred = errorOccurred;
            _disconnectArgs = disconnectArgs;
        }

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

        public void ThrowIfConnected()
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("Connection is already established.");
            }
        }

        /// <summary>Starts the receive loop on a freshly opened stream and raises Connected.</summary>
        public void Start(Stream stream, int maxMessageSizeBytes, ConnectionEventArgs connectedArgs, CancellationToken cancellationToken)
        {
            var session = MessageSession.ForClient(stream, maxMessageSizeBytes, RaiseMessage, RaiseError);
            CancellationTokenSource receiveCts;

            lock (_stateLock)
            {
                _session = session;
                _isConnected = true;
                _disconnectRaised = false;
                _receiveCts?.Dispose();
                _receiveCts = receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            _connected()?.Invoke(_owner, connectedArgs);
            _receiveTask = ReceiveLoopAsync(session, receiveCts.Token);
        }

        public void MarkConnectFailed(Exception ex)
        {
            lock (_stateLock)
            {
                _isConnected = false;
            }

            RaiseError(ex.Message, ex);
        }

        public async Task SendAsync(IEventMessage message)
        {
            var session = GetSession();
            await session.WriteAsync(new MessageEnvelope(message.EventName, message.PayloadValue)).ConfigureAwait(false);
        }

        public Task<IEventMessage> SendAndWaitAsync(IEventMessage message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            MessageSession session;
            try
            {
                session = GetSession();
            }
            catch (Exception ex)
            {
                return Task.FromException<IEventMessage>(ex);
            }

            return session.SendAndWaitAsync(message, timeout, cancellationToken);
        }

        private MessageSession GetSession()
        {
            lock (_stateLock)
            {
                if (!_isConnected || _session == null)
                {
                    throw new InvalidOperationException("Not connected.");
                }

                return _session;
            }
        }

        private async Task ReceiveLoopAsync(MessageSession session, CancellationToken cancellationToken)
        {
            try
            {
                await session.RunAsync(cancellationToken, string.Empty).ConfigureAwait(false);
            }
            finally
            {
                session.FailPendingRequests();
                RaiseDisconnected();
            }
        }

        /// <summary>
        /// Stops the receive loop and raises Disconnected (once). <paramref name="closeTransport"/>
        /// disposes the stream and the underlying client, which unblocks a pending read.
        /// </summary>
        public async Task CloseAsync(Func<Task> closeTransport)
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

                await closeTransport().ConfigureAwait(false);

                if (receiveTask != null)
                {
                    await receiveTask.ConfigureAwait(false);
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
        }

        private void RaiseMessage(MessageEnvelope envelope)
        {
            SafeInvoke.EachHandler(_messageReceived(), _owner,
                new MessageReceivedEventArgs(new EventMessage(envelope.EventName, envelope.PayloadValue)),
                ex => RaiseError("Unhandled exception in MessageReceived subscriber", ex));
        }

        /// <summary>
        /// Raises ErrorOccurred, guarding each subscriber individually. A subscriber that throws
        /// here is swallowed rather than re-raised (there is no further event to escalate to) and
        /// written to <see cref="System.Diagnostics.Trace"/> -- see docs/PROTOCOL.md.
        /// </summary>
        public void RaiseError(string message, Exception? ex)
        {
            SafeInvoke.EachHandler(_errorOccurred(), _owner, new ErrorEventArgs(message, ex), subscriberEx =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] ErrorOccurred subscriber threw and was swallowed: {subscriberEx}");
            });
        }

        private void RaiseDisconnected()
        {
            ConnectionEventArgs args;

            lock (_stateLock)
            {
                if (_disconnectRaised)
                {
                    return;
                }

                _disconnectRaised = true;
                _isConnected = false;
                args = _disconnectArgs();
            }

            // Guarded: this fires from the receive loop's teardown, so a throwing subscriber must
            // not escape and abort CloseAsync's own cleanup.
            SafeInvoke.EachHandler(_disconnected(), _owner, args, ex =>
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[HRpc] Disconnected subscriber threw and was swallowed: {ex}");
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Core;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    public class Server : IDisposable
    {
        private TcpServer? _tcpServer;
        private PipeServer? _pipeServer;
        private TransportType _transportType;

        // Registered via OnRequest before a transport exists yet (the common case: subscribe,
        // then StartAsync). Replayed onto the concrete transport once it's constructed, and also
        // applied immediately below when a transport is already running.
        private readonly Dictionary<string, Func<IEventMessage, Task<object?>>> _pendingRequestHandlers =
            new Dictionary<string, Func<IEventMessage, Task<object?>>>();

        public TransportType TransportType
        {
            get => _transportType;
            set
            {
                if (_transportType != value)
                {
                    DisposeCurrentServer();
                    _transportType = value;
                }
            }
        }

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. Propagated to the
        /// underlying transport server when <see cref="StartAsync"/> is called.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Registers <paramref name="handler"/> to answer incoming requests (see
        /// <see cref="TcpServer.OnRequest"/> for the full contract) named
        /// <paramref name="eventName"/>. May be called before or after <see cref="StartAsync"/> —
        /// a handler registered before the transport exists yet is applied to it as soon as it's
        /// created.
        /// </summary>
        public void OnRequest(string eventName, Func<IEventMessage, Task<object?>> handler)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentException("Event name cannot be null/empty.", nameof(eventName));
            }

            _pendingRequestHandlers[eventName] = handler ?? throw new ArgumentNullException(nameof(handler));
            _tcpServer?.OnRequest(eventName, handler);
            _pipeServer?.OnRequest(eventName, handler);
        }

        public async Task StartAsync(string target, CancellationToken cancellationToken = default)
        {
            DisposeCurrentServer();

            if (TransportType == TransportType.Tcp)
            {
                if (!int.TryParse(target, out var port))
                {
                    throw new ArgumentException("For TCP, target must be a valid port number", nameof(target));
                }

                _tcpServer = new TcpServer { MaxMessageSizeBytes = MaxMessageSizeBytes };
                _tcpServer.ClientConnected += OnClientConnected;
                _tcpServer.ClientDisconnected += OnClientDisconnected;
                _tcpServer.MessageReceived += OnMessageReceived;
                _tcpServer.ErrorOccurred += OnErrorOccurred;
                foreach (var kvp in _pendingRequestHandlers)
                {
                    _tcpServer.OnRequest(kvp.Key, kvp.Value);
                }

                await _tcpServer.StartAsync(port, cancellationToken);
            }
            else if (TransportType == TransportType.Pipe)
            {
                _pipeServer = new PipeServer { MaxMessageSizeBytes = MaxMessageSizeBytes };
                _pipeServer.ClientConnected += OnClientConnected;
                _pipeServer.ClientDisconnected += OnClientDisconnected;
                _pipeServer.MessageReceived += OnMessageReceived;
                _pipeServer.ErrorOccurred += OnErrorOccurred;
                foreach (var kvp in _pendingRequestHandlers)
                {
                    _pipeServer.OnRequest(kvp.Key, kvp.Value);
                }

                await _pipeServer.StartAsync(target, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Unsupported TransportType");
            }
        }

        public async Task StopAsync()
        {
            if (_tcpServer != null)
            {
                await _tcpServer.StopAsync();
            }
            else if (_pipeServer != null)
            {
                await _pipeServer.StopAsync();
            }
        }

        public void Dispose()
        {
            DisposeCurrentServer();
        }

        private void DisposeCurrentServer()
        {
            if (_tcpServer != null)
            {
                _tcpServer.ClientConnected -= OnClientConnected;
                _tcpServer.ClientDisconnected -= OnClientDisconnected;
                _tcpServer.MessageReceived -= OnMessageReceived;
                _tcpServer.ErrorOccurred -= OnErrorOccurred;
                // TcpServer doesn't implement IDisposable, so no dispose
                _tcpServer = null;
            }

            if (_pipeServer != null)
            {
                _pipeServer.ClientConnected -= OnClientConnected;
                _pipeServer.ClientDisconnected -= OnClientDisconnected;
                _pipeServer.MessageReceived -= OnMessageReceived;
                _pipeServer.ErrorOccurred -= OnErrorOccurred;
                _pipeServer.Dispose();
                _pipeServer = null;
            }
        }

        private void OnClientConnected(object? sender, ConnectionEventArgs e)
        {
            ClientConnected?.Invoke(this, e);
        }

        private void OnClientDisconnected(object? sender, ConnectionEventArgs e)
        {
            ClientDisconnected?.Invoke(this, e);
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }

        private void OnErrorOccurred(object? sender, ErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
        }
    }
}

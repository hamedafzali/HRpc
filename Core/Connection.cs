using System;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Core;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Models;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    public class Connection : IDisposable
    {
        private ITcpConnection? _tcpConnection;
        private IPipeConnection? _pipeConnection;
        private TransportType _transportType;

        public TransportType TransportType
        {
            get => _transportType;
            set
            {
                if (_transportType != value)
                {
                    DisposeCurrentConnection();
                    _transportType = value;
                }
            }
        }

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionEventArgs>? Connected;
        public event EventHandler<ConnectionEventArgs>? Disconnected;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public bool IsConnected => (_tcpConnection?.IsConnected ?? false) || (_pipeConnection?.IsConnected ?? false);

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. Propagated to the
        /// underlying transport connection when <see cref="ConnectAsync"/> is called.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        public async Task ConnectAsync(string target, CancellationToken cancellationToken = default)
        {
            DisposeCurrentConnection();

            if (TransportType == TransportType.Tcp)
            {
                var parts = target.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
                {
                    throw new ArgumentException("For TCP, target must be 'host:port'", nameof(target));
                }
                var host = parts[0];

                _tcpConnection = new TcpConnection { MaxMessageSizeBytes = MaxMessageSizeBytes };
                _tcpConnection.MessageReceived += OnMessageReceived;
                _tcpConnection.Connected += OnConnected;
                _tcpConnection.Disconnected += OnDisconnected;
                _tcpConnection.ErrorOccurred += OnErrorOccurred;

                await _tcpConnection.ConnectAsync(host, port, cancellationToken);
            }
            else if (TransportType == TransportType.Pipe)
            {
                _pipeConnection = new PipeConnection { MaxMessageSizeBytes = MaxMessageSizeBytes };
                _pipeConnection.MessageReceived += OnMessageReceived;
                _pipeConnection.Connected += OnConnected;
                _pipeConnection.Disconnected += OnDisconnected;
                _pipeConnection.ErrorOccurred += OnErrorOccurred;

                await _pipeConnection.ConnectAsync(target, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("Unsupported TransportType");
            }
        }

        public async Task SendAsync(IEventMessage message)
        {
            if (_tcpConnection != null)
            {
                await _tcpConnection.SendAsync(message);
            }
            else if (_pipeConnection != null)
            {
                await _pipeConnection.SendAsync(message);
            }
            else
            {
                throw new InvalidOperationException("Not connected");
            }
        }

        public async Task CloseAsync()
        {
            if (_tcpConnection != null)
            {
                await _tcpConnection.CloseAsync();
            }
            else if (_pipeConnection != null)
            {
                await _pipeConnection.CloseAsync();
            }
        }

        public void Dispose()
        {
            DisposeCurrentConnection();
        }

        private void DisposeCurrentConnection()
        {
            if (_tcpConnection != null)
            {
                _tcpConnection.MessageReceived -= OnMessageReceived;
                _tcpConnection.Connected -= OnConnected;
                _tcpConnection.Disconnected -= OnDisconnected;
                _tcpConnection.ErrorOccurred -= OnErrorOccurred;
                _tcpConnection.Dispose();
                _tcpConnection = null;
            }

            if (_pipeConnection != null)
            {
                _pipeConnection.MessageReceived -= OnMessageReceived;
                _pipeConnection.Connected -= OnConnected;
                _pipeConnection.Disconnected -= OnDisconnected;
                _pipeConnection.ErrorOccurred -= OnErrorOccurred;
                _pipeConnection.Dispose();
                _pipeConnection = null;
            }
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            MessageReceived?.Invoke(this, e);
        }

        private void OnConnected(object? sender, ConnectionEventArgs e)
        {
            Connected?.Invoke(this, e);
        }

        private void OnDisconnected(object? sender, ConnectionEventArgs e)
        {
            Disconnected?.Invoke(this, e);
        }

        private void OnErrorOccurred(object? sender, ErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Core;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
{
    public class Server : IDisposable
    {
        private ITcpServer? _tcpServer;
        private PipeServer? _pipeServer;
        private TransportType _transportType;

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

        public async Task StartAsync(string target, CancellationToken cancellationToken = default)
        {
            DisposeCurrentServer();

            if (TransportType == TransportType.Tcp)
            {
                if (!int.TryParse(target, out var port))
                {
                    throw new ArgumentException("For TCP, target must be a valid port number", nameof(target));
                }

                _tcpServer = new TcpServer();
                _tcpServer.ClientConnected += OnClientConnected;
                _tcpServer.ClientDisconnected += OnClientDisconnected;
                _tcpServer.MessageReceived += OnMessageReceived;
                _tcpServer.ErrorOccurred += OnErrorOccurred;

                await _tcpServer.StartAsync(port, cancellationToken);
            }
            else if (TransportType == TransportType.Pipe)
            {
                _pipeServer = new PipeServer();
                _pipeServer.ClientConnected += OnClientConnected;
                _pipeServer.ClientDisconnected += OnClientDisconnected;
                _pipeServer.MessageReceived += OnMessageReceived;
                _pipeServer.ErrorOccurred += OnErrorOccurred;

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

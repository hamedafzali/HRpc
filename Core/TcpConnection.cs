using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
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

        public virtual async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("Connection is already established.");
            }

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(host, port, cancellationToken);
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

                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message, ex));
                throw;
            }
        }

        public async Task SendAsync(IEventMessage message)
        {
            if (!IsConnected || _stream == null)
            {
                throw new InvalidOperationException("Not connected.");
            }

            var envelope = new MessageEnvelope
            {
                EventName = message.EventName,
                Payload = message.Payload
            };

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

            var reader = new StreamReader(_stream, Encoding.UTF8);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    var msg = MessageEnvelope.Deserialize(line);
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                        new EventMessage(msg.EventName, msg.Payload)
                    ));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the connection.
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message, ex));
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
                    await _stream.DisposeAsync();
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
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message, ex));
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

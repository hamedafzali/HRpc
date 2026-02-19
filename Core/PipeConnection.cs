using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using TcpEventFramework.Utils;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
{
    public class PipeConnection : IPipeConnection
    {
        private readonly object _stateLock = new object();

        protected NamedPipeClientStream? _client;
        protected Stream? _stream;

        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _isConnected;
        private bool _disconnectRaised;
        private string _pipeName = string.Empty;

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

        public async Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("Pipe name cannot be null/empty.", nameof(pipeName));
            }

            if (IsConnected)
            {
                throw new InvalidOperationException("Connection is already established.");
            }

            try
            {
                _pipeName = pipeName;
                _client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _client.ConnectAsync(cancellationToken);
                _stream = _client;

                lock (_stateLock)
                {
                    _isConnected = true;
                    _disconnectRaised = false;
                    _receiveCts?.Dispose();
                    _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }

                Connected?.Invoke(this, new ConnectionEventArgs(pipeName, 0));
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

        Task ITcpConnection.ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("PipeConnection does not support host/port ConnectAsync. Use ConnectAsync(pipeName, cancellationToken).");
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
                    var line = await reader.ReadLineAsync().WithCancellation(cancellationToken);
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
#if NETFRAMEWORK
                    _stream.Dispose();
#else
                    await _stream.DisposeAsync();
#endif
                    _stream = null;
                }

                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

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
            _client?.Dispose();
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

                args = new ConnectionEventArgs(_pipeName, 0);
            }

            Disconnected?.Invoke(this, args);
        }
    }
}

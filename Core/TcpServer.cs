using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
{
    public class TcpServer : ITcpServer
    {
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new ConcurrentDictionary<TcpClient, Task>();
        private CancellationTokenSource? _serverCts;
        private volatile bool _running;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        protected void OnErrorOccurred(string message, Exception? ex = null)
        {
            ErrorOccurred?.Invoke(this, new ErrorEventArgs(message, ex));
        }

        public async Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            if (_running)
            {
                throw new InvalidOperationException("Server is already running.");
            }

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                while (!_serverCts.Token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_serverCts.Token);
                    var endPoint = client.Client.RemoteEndPoint as IPEndPoint;

                    ClientConnected?.Invoke(this, new ConnectionEventArgs(
                        endPoint?.Address.ToString() ?? string.Empty,
                        endPoint?.Port ?? 0
                    ));

                    var task = HandleClientAsync(client, _serverCts.Token);
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
            catch (Exception ex)
            {
                OnErrorOccurred("Error in StartAsync", ex);
                throw;
            }
            finally
            {
                _running = false;
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    var message = MessageEnvelope.Deserialize(line);
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                        new EventMessage(message.EventName, message.Payload)
                    ));
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
                var endPoint = client.Client.RemoteEndPoint as IPEndPoint;
                ClientDisconnected?.Invoke(this, new ConnectionEventArgs(
                    endPoint?.Address.ToString() ?? string.Empty,
                    endPoint?.Port ?? 0
                ));

                client.Close();
            }
        }

        public async Task StopAsync()
        {
            _running = false;

            _serverCts?.Cancel();
            _listener?.Stop();

            var runningTasks = _clientTasks.Values;
            await Task.WhenAll(runningTasks);

            _clientTasks.Clear();
            _serverCts?.Dispose();
            _serverCts = null;
        }
    }
}

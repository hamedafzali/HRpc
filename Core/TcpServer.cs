using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Events;

namespace TcpEventFramework.Core
{
    public class TcpServer : ITcpServer
    {
        private TcpListener? _listener;
        private bool _running = false;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<TcpEventFramework.Events.ErrorEventArgs>? ErrorOccurred;


        protected void OnErrorOccurred(string message, Exception? ex = null)
        {
            ErrorOccurred?.Invoke(this, new TcpEventFramework.Events.ErrorEventArgs(message, ex));
        }

        public async Task StartAsync(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;

                while (_running)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    var endPoint = client.Client.RemoteEndPoint as IPEndPoint;

                    ClientConnected?.Invoke(this, new ConnectionEventArgs(
                        endPoint?.Address.ToString() ?? "", endPoint?.Port ?? 0
                    ));

                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in StartAsync", ex);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            try
            {
                while (client.Connected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in HandleClientAsync", ex);
            }
            finally
            {
                var endPoint = client.Client.RemoteEndPoint as IPEndPoint;
                ClientDisconnected?.Invoke(this, new ConnectionEventArgs(
                    endPoint?.Address.ToString() ?? "", endPoint?.Port ?? 0
                ));

                client.Close();
            }
        }

        public async Task StopAsync()
        {
            _running = false;
            _listener?.Stop();
            await Task.CompletedTask;
        }
    }
}

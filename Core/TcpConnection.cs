using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using TcpEventFramework.Events;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
{
    public class TcpConnection : ITcpConnection
    {
        protected TcpClient _client = new TcpClient();
        protected NetworkStream? _stream;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionEventArgs>? Connected;
        public event EventHandler<ConnectionEventArgs>? Disconnected;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public bool IsConnected => _client.Connected;

        public virtual async Task ConnectAsync(string host, int port)
        {
            try
            {
                await _client.ConnectAsync(host, port);
                _stream = _client.GetStream();
                Connected?.Invoke(this, new ConnectionEventArgs(host, port));
                _ = ReceiveLoopAsync();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message,ex));
            }
        }

        public async Task SendAsync(IEventMessage message)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected.");

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


        private async Task ReceiveLoopAsync()
        {
            if (_stream == null) return;
            var reader = new System.IO.StreamReader(_stream, Encoding.UTF8);

            try
            {
                while (_client.Connected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    var msg = MessageEnvelope.Deserialize(line);
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                        new Models.EventMessage(msg.EventName, msg.Payload)
                    ));
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message,ex));
            }
            finally
            {
                Disconnected?.Invoke(this, new ConnectionEventArgs(
                    (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "",
                    (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Port ?? 0
                ));
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                _stream?.Close();
                _client.Close();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex.Message,ex));
            }
        }

        public void Dispose() => _client.Dispose();
    }
}

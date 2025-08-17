using System;
using System.Threading.Tasks;

namespace TcpEventFramework.Interfaces
{
    public interface ITcpConnection : IDisposable
    {
        event EventHandler<Events.MessageReceivedEventArgs> MessageReceived;
        event EventHandler<Events.ConnectionEventArgs> Connected;
        event EventHandler<Events.ConnectionEventArgs> Disconnected;
        event EventHandler<Events.ErrorEventArgs> ErrorOccurred;

        Task ConnectAsync(string host, int port);
        Task SendAsync(IEventMessage message);
        Task CloseAsync();
        bool IsConnected { get; }
    }
}

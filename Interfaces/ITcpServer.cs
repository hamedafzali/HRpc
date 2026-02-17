using System;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Events;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Interfaces
{
    public interface ITcpServer
    {
        event EventHandler<ConnectionEventArgs> ClientConnected;
        event EventHandler<ConnectionEventArgs> ClientDisconnected;
        event EventHandler<MessageReceivedEventArgs> MessageReceived;
        event EventHandler<ErrorEventArgs> ErrorOccurred;

        Task StartAsync(int port, CancellationToken cancellationToken = default);
        Task StopAsync();
    }
}

using System.Threading;
using System.Threading.Tasks;

namespace TcpEventFramework.Interfaces
{
    public interface IPipeConnection : ITcpConnection
    {
        Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default);
    }
}

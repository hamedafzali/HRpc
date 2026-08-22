using System.Threading;
using System.Threading.Tasks;

namespace HRpc.Interfaces
{
    public interface IPipeConnection : ITcpConnection
    {
        Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default);
    }
}

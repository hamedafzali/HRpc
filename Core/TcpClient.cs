using HRpc.Interfaces;

namespace HRpc.Core
{
    public class TcpClientWrapper : TcpConnection, ITcpClient
    {
        // Inherits everything from TcpConnection
        // Can extend with client-specific features if needed
    }
}

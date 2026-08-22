using HRpc.Interfaces;

namespace HRpc.Core
{
    public class PipeClientWrapper : PipeConnection, IPipeClient
    {
        // Inherits everything from PipeConnection
        // Can extend with client-specific features if needed
    }
}

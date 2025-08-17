using System;

namespace TcpEventFramework.Events
{
    public class ConnectionEventArgs : EventArgs
    {
        public string RemoteAddress { get; }
        public int RemotePort { get; }

        public ConnectionEventArgs(string remoteAddress, int remotePort)
        {
            RemoteAddress = remoteAddress ?? throw new ArgumentNullException(nameof(remoteAddress));
            RemotePort = remotePort;
        }
    }
}

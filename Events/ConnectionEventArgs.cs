using System;

namespace HRpc.Events
{
    public class ConnectionEventArgs : EventArgs
    {
        public string RemoteAddress { get; }
        public int RemotePort { get; }

        /// <summary>
        /// The server-assigned id of the client this event is about. Set on server events, so it
        /// can be passed to <c>SendAsync(clientId, ...)</c>; null on client-side connection events.
        /// </summary>
        public string? ClientId { get; }

        public ConnectionEventArgs(string remoteAddress, int remotePort)
            : this(remoteAddress, remotePort, null)
        {
        }

        public ConnectionEventArgs(string remoteAddress, int remotePort, string? clientId)
        {
            RemoteAddress = remoteAddress ?? throw new ArgumentNullException(nameof(remoteAddress));
            RemotePort = remotePort;
            ClientId = clientId;
        }
    }
}

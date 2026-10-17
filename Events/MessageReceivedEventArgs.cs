using System;
using HRpc.Interfaces;

namespace HRpc.Events
{
    public class MessageReceivedEventArgs : EventArgs
    {
        public IEventMessage Message { get; }

        /// <summary>
        /// The server-assigned id of the client that sent the message. Set on server events, so a
        /// reply can be pushed with <c>SendAsync(clientId, ...)</c>; null on client-side events.
        /// </summary>
        public string? ClientId { get; }

        public MessageReceivedEventArgs(IEventMessage message)
            : this(message, null)
        {
        }

        public MessageReceivedEventArgs(IEventMessage message, string? clientId)
        {
            Message = message;
            ClientId = clientId;
        }
    }
}

using System;
using HRpc.Interfaces;

namespace HRpc.Events
{
    public class MessageReceivedEventArgs : EventArgs
    {
        public IEventMessage Message { get; }

        public MessageReceivedEventArgs(IEventMessage message)
        {
            Message = message;
        }
    }
}

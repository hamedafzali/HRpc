using System;
using TcpEventFramework.Interfaces;

namespace TcpEventFramework.Events
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

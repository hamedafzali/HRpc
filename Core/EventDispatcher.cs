using System;
using System.Threading.Tasks;

using TcpEventFramework.Interfaces;
using TcpEventFramework.Events;

namespace TcpEventFramework.Core
{
    public class EventDispatcher
    {
        public void Subscribe(ITcpConnection connection, string eventName, Action<IEventMessage> handler)
        {
            connection.MessageReceived += (s, e) =>
            {
                if (e.Message.EventName == eventName)
                    handler(e.Message);
            };
        }

        public async Task Emit(ITcpConnection connection, IEventMessage message)
        {
            await connection.SendAsync(message);
        }
    }
}

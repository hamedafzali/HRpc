using TcpEventFramework.Interfaces;

namespace TcpEventFramework.Models
{
    public class EventMessage : IEventMessage
    {
        public string EventName { get;  }
        public string Payload { get; }

        public EventMessage(string eventName, string payload)
        {
            EventName = eventName;
            Payload = payload;
        }
    }
}

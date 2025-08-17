namespace TcpEventFramework.Interfaces
{
    public interface IEventMessage
    {
        string EventName { get; }
        string Payload { get; }
    }
}

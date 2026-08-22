using System;
using System.Threading.Tasks;

using HRpc.Interfaces;
using HRpc.Events;

namespace HRpc.Core
{
    public class EventDispatcher
    {
        public IDisposable Subscribe(ITcpConnection connection, string eventName, Action<IEventMessage> handler)
        {
            EventHandler<MessageReceivedEventArgs> eventHandler = (s, e) =>
            {
                if (e.Message.EventName == eventName)
                {
                    handler(e.Message);
                }
            };

            connection.MessageReceived += eventHandler;
            return new Subscription(() => connection.MessageReceived -= eventHandler);
        }

        public async Task Emit(ITcpConnection connection, IEventMessage message)
        {
            await connection.SendAsync(message);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _unsubscribe;
            private bool _disposed;

            public Subscription(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _unsubscribe();
                _disposed = true;
            }
        }
    }
}

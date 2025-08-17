using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using TcpEventFramework.Core;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using TcpEventFramework.Events;
using Moq;

namespace TcpEventFramework.Tests
{
    [TestClass]
    public class UnitTests
    {
        #region Models

        [TestMethod]
        public void EventMessage_ShouldStoreProperties()
        {
            var msg = new EventMessage("TestEvent", "Hello");
            Assert.AreEqual("TestEvent", msg.EventName);
            Assert.AreEqual("Hello", msg.Payload);
        }

        [TestMethod]
        public void MessageEnvelope_ShouldSerializeAndDeserialize()
        {
            var envelope = new MessageEnvelope
            {
                EventName = "E",
                Payload = "Data"
            };

            var json = envelope.Serialize();
            var deserialized = MessageEnvelope.Deserialize(json);

            Assert.AreEqual(envelope.EventName, deserialized.EventName);
            Assert.AreEqual(envelope.Payload, deserialized.Payload);
        }

        #endregion

        #region EventDispatcher

        [TestMethod]
        public void EventDispatcher_ShouldInvokeHandler_OnMatchingEvent()
        {
            var mockConnection = new Mock<ITcpConnection>();
            var dispatcher = new EventDispatcher();
            bool handlerCalled = false;

            dispatcher.Subscribe(mockConnection.Object, "TestEvent", msg =>
            {
                handlerCalled = true;
                Assert.AreEqual("TestEvent", msg.EventName);
                Assert.AreEqual("Payload", msg.Payload);
            });

            // Raise the MessageReceived event
            mockConnection.Raise(c => c.MessageReceived += null, new MessageReceivedEventArgs(
                new EventMessage("TestEvent", "Payload")
            ));

            Assert.IsTrue(handlerCalled);
        }

        #endregion

        #region TcpConnection / TcpClientWrapper

        [TestMethod]
        public async Task TcpConnection_ShouldCallSendAsync()
        {
            var mockConnection = new Mock<ITcpConnection>();
            var dispatcher = new EventDispatcher();

            bool sendCalled = false;

            mockConnection.Setup(c => c.SendAsync(It.IsAny<IEventMessage>()))
                .Returns(Task.CompletedTask)
                .Callback<IEventMessage>(msg =>
                {
                    sendCalled = true;
                    Assert.AreEqual("TestEvent", msg.EventName);
                    Assert.AreEqual("Payload", msg.Payload);
                });

            await dispatcher.Emit(mockConnection.Object, new EventMessage("TestEvent", "Payload"));
            Assert.IsTrue(sendCalled);
        }

        #endregion
    }
}

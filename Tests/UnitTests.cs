using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Core;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;

namespace TcpEventFramework.Tests
{
    [TestClass]
    public class UnitTests
    {
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

        [TestMethod]
        public void EventDispatcher_ShouldInvokeHandler_OnMatchingEvent()
        {
            var mockConnection = new Mock<ITcpConnection>();
            var dispatcher = new EventDispatcher();
            var handlerCalled = false;

            dispatcher.Subscribe(mockConnection.Object, "TestEvent", msg =>
            {
                handlerCalled = true;
                Assert.AreEqual("TestEvent", msg.EventName);
                Assert.AreEqual("Payload", msg.Payload);
            });

            mockConnection.Raise(c => c.MessageReceived += null, new MessageReceivedEventArgs(
                new EventMessage("TestEvent", "Payload")
            ));

            Assert.IsTrue(handlerCalled);
        }

        [TestMethod]
        public void EventDispatcher_SubscriptionDispose_ShouldUnsubscribeHandler()
        {
            var mockConnection = new Mock<ITcpConnection>();
            var dispatcher = new EventDispatcher();
            var callCount = 0;

            var subscription = dispatcher.Subscribe(mockConnection.Object, "TestEvent", _ => callCount++);

            mockConnection.Raise(c => c.MessageReceived += null, new MessageReceivedEventArgs(
                new EventMessage("TestEvent", "Payload")
            ));

            subscription.Dispose();

            mockConnection.Raise(c => c.MessageReceived += null, new MessageReceivedEventArgs(
                new EventMessage("TestEvent", "Payload")
            ));

            Assert.AreEqual(1, callCount);
        }

        [TestMethod]
        public async Task TcpConnection_ShouldReceiveMessage_FromSocketServer()
        {
            var port = GetFreePort();
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.Payload);

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                await using var serverStream = serverClient.GetStream();
                var payload = new MessageEnvelope { EventName = "Greeting", Payload = "Hello" }.Serialize() + "\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                await serverStream.WriteAsync(bytes, 0, bytes.Length);
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(received.Task, completed);
            Assert.AreEqual("Hello", received.Task.Result);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        public async Task TcpConnection_ShouldRaiseError_OnMalformedEnvelope()
        {
            var port = GetFreePort();
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.ErrorOccurred += (_, _) => errorTcs.TrySetResult(true);

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                await using var serverStream = serverClient.GetStream();
                var bytes = Encoding.UTF8.GetBytes("not-json\n");
                await serverStream.WriteAsync(bytes, 0, bytes.Length);
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsTrue(errorTcs.Task.Result);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        public async Task TcpServer_ShouldRaiseMessageReceived_WhenClientSendsEnvelope()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var receivedTcs = new TaskCompletionSource<IEventMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();

            server.MessageReceived += (_, args) => receivedTcs.TrySetResult(args.Message);

            var serverTask = server.StartAsync(port, cts.Token);
            await Task.Delay(150);

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            await using (var stream = client.GetStream())
            {
                var payload = new MessageEnvelope { EventName = "Ping", Payload = "Pong" }.Serialize() + "\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            var completed = await Task.WhenAny(receivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(receivedTcs.Task, completed);
            Assert.AreEqual("Ping", receivedTcs.Task.Result.EventName);
            Assert.AreEqual("Pong", receivedTcs.Task.Result.Payload);

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        public async Task TcpConnection_ConnectAsync_ShouldThrow_OnConnectionFailure()
        {
            var connection = new TcpClientWrapper();
            var unusedPort = GetFreePort();

            await Assert.ThrowsExceptionAsync<SocketException>(
                () => connection.ConnectAsync("127.0.0.1", unusedPort)
            );
        }

        [TestMethod]
        public async Task TcpServer_StopAsync_ShouldNotHang_WithConnectedClient()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var serverTask = server.StartAsync(port);
            await Task.Delay(150);

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);

            var stopTask = server.StopAsync();
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(stopTask, completed);
            await serverTask;
        }

        [TestMethod]
        public async Task TcpConnection_CloseAsync_ShouldBeIdempotent()
        {
            var port = GetFreePort();
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var acceptTask = listener.AcceptTcpClientAsync();
            var connection = new TcpClientWrapper();
            await connection.ConnectAsync("127.0.0.1", port);
            using var serverClient = await acceptTask;

            await connection.CloseAsync();
            await connection.CloseAsync();
        }

        [TestMethod]
        public async Task TcpConnection_Disconnected_ShouldFireOnce()
        {
            var port = GetFreePort();
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var connection = new TcpClientWrapper();
            var disconnectCount = 0;
            connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnectCount);

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                await Task.Delay(100);
                serverClient.Close();
            });

            await connection.ConnectAsync("127.0.0.1", port);

            var timeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (disconnectCount == 0 && !timeout.IsCompleted)
            {
                await Task.Delay(30);
            }

            Assert.AreEqual(1, disconnectCount);

            await connection.CloseAsync();
            await acceptTask;

            Assert.AreEqual(1, disconnectCount);
        }

        [TestMethod]
        public async Task TcpConnection_ConnectAsync_ShouldHonorCancellation()
        {
            var connection = new TcpClientWrapper();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => connection.ConnectAsync("127.0.0.1", 65000, cts.Token)
            );
        }

        [TestMethod]
        public async Task TcpServer_StartAsync_ShouldHonorCancellation()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(200);

            var startTask = server.StartAsync(port, cts.Token);
            await startTask;
        }

        [TestMethod]
        public async Task TcpConnection_ShouldReceiveLargeAndBurstMessages()
        {
            var port = GetFreePort();
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var messageCount = 0;
            var receivedAll = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.MessageReceived += (_, _) =>
            {
                if (Interlocked.Increment(ref messageCount) == 3)
                {
                    receivedAll.TrySetResult(true);
                }
            };

            var largePayload = new string('x', 128 * 1024);
            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                await using var stream = serverClient.GetStream();

                var frames = new[]
                {
                    new MessageEnvelope { EventName = "Large", Payload = largePayload }.Serialize() + "\n",
                    new MessageEnvelope { EventName = "Small1", Payload = "a" }.Serialize() + "\n",
                    new MessageEnvelope { EventName = "Small2", Payload = "b" }.Serialize() + "\n"
                };

                foreach (var frame in frames)
                {
                    var bytes = Encoding.UTF8.GetBytes(frame);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(receivedAll.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(receivedAll.Task, completed);
            Assert.AreEqual(3, messageCount);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        public async Task TcpServer_ShouldHandleConcurrentClientMessages()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var received = 0;
            var expected = 5;
            var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.MessageReceived += (_, _) =>
            {
                if (Interlocked.Increment(ref received) == expected)
                {
                    allReceived.TrySetResult(true);
                }
            };

            var serverTask = server.StartAsync(port);
            await Task.Delay(150);

            var clients = new Task[expected];
            for (var i = 0; i < expected; i++)
            {
                var idx = i;
                clients[i] = Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", port);
                    await using var stream = client.GetStream();
                    var frame = new MessageEnvelope { EventName = "C", Payload = idx.ToString() }.Serialize() + "\n";
                    var bytes = Encoding.UTF8.GetBytes(frame);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                });
            }

            await Task.WhenAll(clients);
            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(allReceived.Task, completed);
            Assert.AreEqual(expected, received);

            await server.StopAsync();
            await serverTask;
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}

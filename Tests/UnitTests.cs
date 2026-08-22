using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Core;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Models;
using HRpc.Utils;
#if NETFRAMEWORK
using JsonExceptionType = Newtonsoft.Json.JsonException;
#else
using JsonExceptionType = System.Text.Json.JsonException;
#endif

namespace HRpc.Tests
{
    [TestClass]
    public class UnitTests
    {
        // net48's TcpListener does not implement IDisposable, so `using var listener = new
        // TcpListener(...)` does not compile there even though it does on net8.0/net9.0 (which
        // added public Dispose()). This subclass adds it uniformly so the same test source
        // compiles -- and disposes deterministically -- across all TFMs the test project targets.
        private sealed class DisposableTcpListener : TcpListener, IDisposable
        {
            public DisposableTcpListener(IPAddress localaddr, int port) : base(localaddr, port) { }
            void IDisposable.Dispose() => Stop();
        }

        private static void ArrayFillX(byte[] buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)'x';
            }
        }
        [TestMethod]
        public void EventMessage_ShouldStoreProperties()
        {
            var msg = new EventMessage("TestEvent", "Hello");
            Assert.AreEqual("TestEvent", msg.EventName);
            Assert.AreEqual("Hello", msg.GetPayloadAsString());
        }

        [TestMethod]
#pragma warning disable CS0618 // B3: deliberately exercises the obsolete string-based Payload property to prove it still compiles and works.
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
#pragma warning restore CS0618

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
                Assert.AreEqual("Payload", msg.GetPayloadAsString());
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
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var payload = new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n";
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
        [Timeout(10000)]
        public async Task TcpConnection_ShouldRaiseError_AndResync_OnMalformedEnvelope()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.ErrorOccurred += (_, _) => errorTcs.TrySetResult(true);
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            // The server side must stay open until the test explicitly closes the client
            // connection below -- disposing it right after writing races the client's receive
            // loop (it would see EOF and disconnect on its own, independent of the malformed
            // envelope this test is actually exercising).
            TcpClient? serverClient = null;
            NetworkStream? serverStream = null;
            var acceptTask = Task.Run(async () =>
            {
                serverClient = await listener.AcceptTcpClientAsync();
                serverStream = serverClient.GetStream();
                var bad = Encoding.UTF8.GetBytes("not-json\n");
                await serverStream.WriteAsync(bad, 0, bad.Length);

                var good = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
                await serverStream.WriteAsync(good, 0, good.Length);
            });

            try
            {
                await connection.ConnectAsync("127.0.0.1", port);
                var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.AreEqual(errorTcs.Task, errorCompleted);
                Assert.IsTrue(errorTcs.Task.Result);

                // Recoverable: the connection must keep reading and deliver the next, valid message
                // on the same connection rather than disconnecting after the malformed one.
                var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.AreEqual(received.Task, receivedCompleted);
                Assert.AreEqual("Hello", received.Task.Result);
                Assert.IsFalse(disconnected);

                await connection.CloseAsync();
                await acceptTask;
            }
            finally
            {
                serverStream?.Dispose();
                serverClient?.Dispose();
            }
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeConnection_ShouldRaiseError_AndResync_OnMalformedEnvelope()
        {
            var pipeName = "hrpcmalpipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var serverStream = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new PipeClientWrapper();
            connection.ErrorOccurred += (_, _) => errorTcs.TrySetResult(true);
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            var acceptTask = Task.Run(async () =>
            {
                await serverStream.WaitForConnectionAsync();
                var bad = Encoding.UTF8.GetBytes("not-json\n");
                await serverStream.WriteAsync(bad, 0, bad.Length);

                var good = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
                await serverStream.WriteAsync(good, 0, good.Length);
            });

            await connection.ConnectAsync(pipeName);
            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsTrue(errorTcs.Task.Result);

            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);
            Assert.IsFalse(disconnected);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpServer_ShouldRaiseError_AndResync_OnMalformedEnvelope()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ErrorOccurred += (_, _) => errorTcs.TrySetResult(true);
            server.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());

            var serverTask = server.StartAsync(port);
            await Task.Delay(150);

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();

            var bad = Encoding.UTF8.GetBytes("not-json\n");
            await stream.WriteAsync(bad, 0, bad.Length);
            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsTrue(errorTcs.Task.Result);

            var good = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
            await stream.WriteAsync(good, 0, good.Length);
            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeServer_ShouldRaiseError_AndResync_OnMalformedEnvelope()
        {
            var pipeName = "hrpcsrvmal-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer();
            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ErrorOccurred += (_, _) => errorTcs.TrySetResult(true);
            server.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());

            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync();

            var badBytes = Encoding.UTF8.GetBytes("not-json\n");
            await client.WriteAsync(badBytes, 0, badBytes.Length);
            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsTrue(errorTcs.Task.Result);

            var goodBytes = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
            await client.WriteAsync(goodBytes, 0, goodBytes.Length);
            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_ThrowingSubscriber_DoesNotDisconnect_AndOtherSubscribersStillReceive()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canClosePeer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            connection.MessageReceived += (_, _) => throw new InvalidOperationException("boom: first subscriber always throws");
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var bytes = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
                await serverStream.WriteAsync(bytes, 0, bytes.Length);

                // Keep the peer's stream open (so the client doesn't see EOF and legitimately
                // disconnect for an unrelated reason) until after the test has checked that a
                // throwing subscriber alone did not disconnect the connection.
                await canClosePeer.Task;
            });

            await connection.ConnectAsync("127.0.0.1", port);

            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(InvalidOperationException));

            // The first subscriber throwing must not stop the second subscriber in the invocation
            // list from receiving the same message.
            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);
            Assert.IsFalse(disconnected);

            canClosePeer.TrySetResult(true);
            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeConnection_ThrowingSubscriber_DoesNotDisconnect_AndOtherSubscribersStillReceive()
        {
            var pipeName = "hrpcthrowpipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer
            {
                InitialMessage = new EventMessage("Greeting", "Hello")
            };
            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new PipeClientWrapper();
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            connection.MessageReceived += (_, _) => throw new InvalidOperationException("boom: first subscriber always throws");
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            await connection.ConnectAsync(pipeName);

            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(InvalidOperationException));

            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);
            Assert.IsFalse(disconnected);

            await connection.CloseAsync();
            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpServer_ThrowingSubscriber_DoesNotDisconnectClient_AndOtherSubscribersStillReceive()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            server.MessageReceived += (_, _) => throw new InvalidOperationException("boom: first subscriber always throws");
            server.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var clientDisconnected = false;
            server.ClientDisconnected += (_, _) => clientDisconnected = true;

            var serverTask = server.StartAsync(port);
            await Task.Delay(150);

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            using var stream = client.GetStream();
            var bytes = Encoding.UTF8.GetBytes(new MessageEnvelope("Greeting", (object?)"Hello").Serialize() + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);

            var errorCompleted = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(errorTcs.Task, errorCompleted);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(InvalidOperationException));

            var receivedCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, receivedCompleted);
            Assert.AreEqual("Hello", received.Task.Result);
            Assert.IsFalse(clientDisconnected);

            await server.StopAsync();
            await serverTask;
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
            using (var stream = client.GetStream())
            {
                var payload = new MessageEnvelope("Ping", (object?)"Pong").Serialize() + "\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            var completed = await Task.WhenAny(receivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(receivedTcs.Task, completed);
            Assert.AreEqual("Ping", receivedTcs.Task.Result.EventName);
            Assert.AreEqual("Pong", receivedTcs.Task.Result.GetPayloadAsString());

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        public async Task TcpServer_Stress_ShouldHandleManyConcurrentClientMessages()
        {
            var port = GetFreePort();
            var server = new TcpServer();

            var received = 0;
            var clientsCount = 20;
            var messagesPerClient = 10;
            var expected = clientsCount * messagesPerClient;
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

            var clients = new Task[clientsCount];
            for (var i = 0; i < clientsCount; i++)
            {
                var idx = i;
                clients[i] = Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", port);
                    using var stream = client.GetStream();

                    for (var m = 0; m < messagesPerClient; m++)
                    {
                        var frame = new MessageEnvelope("Stress", (object?)$"{idx}:{m}").Serialize() + "\n";
                        var bytes = Encoding.UTF8.GetBytes(frame);
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                });
            }

            await Task.WhenAll(clients);
            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.AreEqual(allReceived.Task, completed);
            Assert.AreEqual(expected, received);

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        public async Task PipeServer_Stress_ShouldHandleManyConcurrentClientMessages()
        {
            var pipeName = "hrpcps-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer();

            var received = 0;
            var clientsCount = 50;
            var expected = clientsCount;
            var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.MessageReceived += (_, _) =>
            {
                if (Interlocked.Increment(ref received) == expected)
                {
                    allReceived.TrySetResult(true);
                }
            };

            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var clients = new Task[clientsCount];
            for (var i = 0; i < clientsCount; i++)
            {
                var idx = i;
                clients[i] = Task.Run(async () =>
                {
                    var client = new PipeClientWrapper();
                    await client.ConnectAsync(pipeName);
                    await client.SendAsync(new EventMessage("Stress", idx.ToString()));
                    await client.CloseAsync();
                });
            }

            await Task.WhenAll(clients);
            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.AreEqual(allReceived.Task, completed);
            Assert.AreEqual(expected, received);

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        public async Task PipeServer_ShouldRaiseMessageReceived_WhenClientSendsEnvelope()
        {
            var pipeName = "hrpcsrv-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer();
            var receivedTcs = new TaskCompletionSource<IEventMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();

            server.MessageReceived += (_, args) => receivedTcs.TrySetResult(args.Message);

            var serverTask = server.StartAsync(pipeName, cts.Token);
            await Task.Delay(150);

            var client = new PipeClientWrapper();
            await client.ConnectAsync(pipeName);
            await client.SendAsync(new EventMessage("Ping", "Pong"));
            await client.CloseAsync();

            var completed = await Task.WhenAny(receivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(receivedTcs.Task, completed);
            Assert.AreEqual("Ping", receivedTcs.Task.Result.EventName);
            Assert.AreEqual("Pong", receivedTcs.Task.Result.GetPayloadAsString());

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeServer_ShouldReceiveMultipleMessages_OnSingleConnection()
        {
            var pipeName = "hrpcmulti-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer();
            var receivedMessages = new System.Collections.Concurrent.ConcurrentQueue<IEventMessage>();
            var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var expected = 5;

            server.MessageReceived += (_, args) =>
            {
                receivedMessages.Enqueue(args.Message);
                if (receivedMessages.Count == expected)
                {
                    allReceived.TrySetResult(true);
                }
            };

            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var client = new PipeClientWrapper();
            await client.ConnectAsync(pipeName);

            for (var i = 0; i < expected; i++)
            {
                await client.SendAsync(new EventMessage("Multi", i.ToString()));
            }

            var completed = await Task.WhenAny(allReceived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(allReceived.Task, completed);
            Assert.AreEqual(expected, receivedMessages.Count);

            var ordered = receivedMessages.ToArray();
            for (var i = 0; i < expected; i++)
            {
                Assert.AreEqual("Multi", ordered[i].EventName);
                Assert.AreEqual(i.ToString(), ordered[i].GetPayloadAsString());
            }

            await client.CloseAsync();
            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        public async Task PipeConnection_ShouldReceiveMessage_FromNamedPipeServer()
        {
            var pipeName = "hrpc-test-" + Guid.NewGuid().ToString("N");

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new PipeClientWrapper();
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());

            var server = new PipeServer();
            server.InitialMessage = new EventMessage("Greeting", "Hello");

            var serverTask = server.StartAsync(pipeName);

            await connection.ConnectAsync(pipeName);
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(received.Task, completed);
            Assert.AreEqual("Hello", received.Task.Result);

            await connection.CloseAsync();
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
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
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
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
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
        [Timeout(10000)]
        public async Task PipeConnection_Disconnected_ShouldFireOnce()
        {
            var pipeName = "hrpcdisc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer();
            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var connection = new PipeClientWrapper();
            var disconnectCount = 0;
            connection.Disconnected += (_, _) => Interlocked.Increment(ref disconnectCount);

            await connection.ConnectAsync(pipeName);

            // Force the server end of the pipe closed; the client's receive loop should
            // observe the break and raise Disconnected exactly once (not once on the
            // failed read and again during CloseAsync's own disposal).
            await server.StopAsync();
            await serverTask;

            var timeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (disconnectCount == 0 && !timeout.IsCompleted)
            {
                await Task.Delay(30);
            }

            Assert.AreEqual(1, disconnectCount);

            await connection.CloseAsync();

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
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
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
                using var stream = serverClient.GetStream();

                var frames = new[]
                {
                    new MessageEnvelope("Large", (object?)largePayload).Serialize() + "\n",
                    new MessageEnvelope("Small1", (object?)"a").Serialize() + "\n",
                    new MessageEnvelope("Small2", (object?)"b").Serialize() + "\n"
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
                    using var stream = client.GetStream();
                    var frame = new MessageEnvelope("C", (object?)idx.ToString()).Serialize() + "\n";
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

        // --- A2: max message size guard ---

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_ShouldReceiveMessage_AtExactMaxSize()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var envelope = new MessageEnvelope("Exact", (object?)new string('a', 500));
            var json = envelope.Serialize();
            var exactLimit = Encoding.UTF8.GetByteCount(json);

            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper { MaxMessageSizeBytes = exactLimit };
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message.GetPayloadAsString());
            var errorFired = false;
            connection.ErrorOccurred += (_, _) => errorFired = true;

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await serverStream.WriteAsync(bytes, 0, bytes.Length);
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));

            Assert.AreEqual(received.Task, completed);
            Assert.AreEqual(envelope.GetPayloadAsString(), received.Task.Result);
            Assert.IsFalse(errorFired, "A message exactly at the configured limit must not raise ErrorOccurred.");

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_ShouldRaiseError_AndDisconnect_OnOversizedMessage()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var connection = new TcpClientWrapper { MaxMessageSizeBytes = 64 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var oversized = new MessageEnvelope("Big", (object?)new string('x', 4096)).Serialize() + "\n";
                var bytes = Encoding.UTF8.GetBytes(oversized);
                try
                {
                    await serverStream.WriteAsync(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Expected once the client aborts and closes the connection.
                }
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));

            // ErrorOccurred and Disconnected fire on separate paths (the TaskCompletionSource above
            // resumes asynchronously via RunContinuationsAsynchronously), so poll rather than assert
            // disconnected immediately after the error is observed.
            var disconnectTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (!disconnected && !disconnectTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.IsTrue(disconnected);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeConnection_ShouldRaiseError_AndDisconnect_OnOversizedMessage()
        {
            var pipeName = "hrpcbig-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer
            {
                InitialMessage = new EventMessage("Big", new string('x', 4096))
            };
            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var connection = new PipeClientWrapper { MaxMessageSizeBytes = 64 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            await connection.ConnectAsync(pipeName);

            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));

            // ErrorOccurred and Disconnected fire on separate paths (the TaskCompletionSource above
            // resumes asynchronously via RunContinuationsAsynchronously), so poll rather than assert
            // disconnected immediately after the error is observed.
            var disconnectTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (!disconnected && !disconnectTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.IsTrue(disconnected);

            await connection.CloseAsync();
            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpServer_ShouldRaiseError_AndRemoveClient_OnOversizedMessage()
        {
            var port = GetFreePort();
            var server = new TcpServer { MaxMessageSizeBytes = 64 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);

            var serverTask = server.StartAsync(port);
            await Task.Delay(150);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();
                var oversized = new MessageEnvelope("Big", (object?)new string('x', 4096)).Serialize() + "\n";
                var bytes = Encoding.UTF8.GetBytes(oversized);
                try
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Expected once the server aborts and closes the connection.
                }

                var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(errorTcs.Task, completed);
                Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));
            }

            // A1 regression guard: the disconnected client's entry must not leak in _clientTasks.
            var dict = GetClientTaskDictionary(server);
            var pollTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (dict.Count > 0 && !pollTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.AreEqual(0, dict.Count, "Expected the disconnected client's task entry to be removed from _clientTasks.");

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeServer_ShouldRaiseError_AndRemoveClient_OnOversizedMessage()
        {
            var pipeName = "hrpcsrvbig-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer { MaxMessageSizeBytes = 64 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);

            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            var client = new PipeClientWrapper();
            await client.ConnectAsync(pipeName);
            await client.SendAsync(new EventMessage("Big", new string('x', 4096)));

            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));

            await client.CloseAsync();

            // A1 regression guard: the disconnected client's completed task entry must not leak in
            // _clientTasks. Unlike TcpServer, PipeServer's accept loop continuously creates new
            // pending (not-yet-connected) listener entries, so dict.Count never reaches 0 on its
            // own; instead assert that no *completed* task remains present.
            var dict = GetClientTaskDictionary(server);
            var pollTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (AnyCompletedTaskLeaked(dict) && !pollTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.IsFalse(AnyCompletedTaskLeaked(dict), "Expected the disconnected client's completed task entry to be removed from _clientTasks.");

            await server.StopAsync();
            await serverTask;
        }

        // --- B1: protocol version field ---

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_WithVersion_UsesCurrentProtocolVersion()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var roundTripped = MessageEnvelope.Deserialize(envelope.Serialize());

            Assert.AreEqual(MessageEnvelope.CurrentProtocolVersion, roundTripped.Version);
            Assert.AreEqual("Foo", roundTripped.EventName);
            Assert.AreEqual("Bar", roundTripped.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_DoesNotMutateVersion()
        {
            // Item-1 regression guard: Serialize() must not stamp CurrentProtocolVersion onto the
            // instance. A deserialized envelope being forwarded unchanged (a relay, a future
            // BroadcastAsync) must keep its original Version through Serialize(), not get silently
            // relabeled as whatever this build currently emits.
            var envelope = MessageEnvelope.Deserialize("{\"eventName\":\"Foo\",\"payload\":\"Bar\"}");
            Assert.AreEqual(MessageEnvelope.LegacyProtocolVersion, envelope.Version);

            envelope.Serialize();

            Assert.AreEqual(MessageEnvelope.LegacyProtocolVersion, envelope.Version,
                "Serialize() must not mutate the Version it was called on.");
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_CalledTwice_ProducesIdenticalOutput()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var first = envelope.Serialize();
            var second = envelope.Serialize();

            Assert.AreEqual(first, second);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_LegacyProtocolVersion_MustNeverChange()
        {
            // LegacyProtocolVersion identifies one specific historical wire shape -- every HRpc
            // release before v1.2.0, none of which ever emitted a "v" field -- and must stay 1
            // forever, no matter how high CurrentProtocolVersion climbs. If this fails, someone
            // changed the wrong constant; see the doc comment on LegacyProtocolVersion.
            Assert.AreEqual(1, MessageEnvelope.LegacyProtocolVersion);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_MissingVersionField_DefaultsToLiteralVersion1_NotCurrentProtocolVersion()
        {
            // B1-FIX regression guard: a v-less envelope (simulating a 1.1.x peer) must resolve to
            // the literal historical value 1 -- via LegacyProtocolVersion -- never to whatever
            // CurrentProtocolVersion happens to be at the time this build was compiled. Do NOT
            // "simplify" this assertion to MessageEnvelope.CurrentProtocolVersion after a future
            // version bump; that was the exact bug B1-FIX corrected.
            var raw = "{\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var envelope = MessageEnvelope.Deserialize(raw);

            Assert.AreEqual(1, envelope.Version);
            Assert.AreEqual(MessageEnvelope.LegacyProtocolVersion, envelope.Version);
            Assert.AreEqual("Foo", envelope.EventName);
            Assert.AreEqual("Bar", envelope.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_FutureVersion_ThrowsUnsupportedProtocolVersionException()
        {
            var raw = "{\"v\":999,\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var ex = Assert.ThrowsException<UnsupportedProtocolVersionException>(
                () => MessageEnvelope.Deserialize(raw));
            Assert.AreEqual(999, ex.ReceivedVersion);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_VersionZero_ThrowsUnsupportedProtocolVersionException()
        {
            var raw = "{\"v\":0,\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var ex = Assert.ThrowsException<UnsupportedProtocolVersionException>(
                () => MessageEnvelope.Deserialize(raw));
            Assert.AreEqual(0, ex.ReceivedVersion);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_NegativeVersion_ThrowsUnsupportedProtocolVersionException()
        {
            var raw = "{\"v\":-1,\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var ex = Assert.ThrowsException<UnsupportedProtocolVersionException>(
                () => MessageEnvelope.Deserialize(raw));
            Assert.AreEqual(-1, ex.ReceivedVersion);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_UnknownExtraFields_AreIgnored()
        {
            var raw = "{\"v\":1,\"eventName\":\"Foo\",\"payload\":\"Bar\",\"correlationId\":\"abc-123\",\"type\":\"future-field\"}";

            var envelope = MessageEnvelope.Deserialize(raw);

            Assert.AreEqual(1, envelope.Version);
            Assert.AreEqual("Foo", envelope.EventName);
            Assert.AreEqual("Bar", envelope.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_CrossSerializer_NewtonsoftShapedJson_DeserializesViaMessageEnvelope()
        {
            // As of D1, HRpc.Tests multi-targets net48;net8.0;net9.0, so this same test source is
            // compiled and run once per TFM. On net48, MessageEnvelope.Deserialize is the
            // Newtonsoft-backed path (#if NETFRAMEWORK); on net8.0/net9.0 it's the System.Text.Json
            // path. The hand-written camelCase JSON below (declaration order, no extra whitespace --
            // the exact shape the paired-attribute rule promises both serializers agree on) is now a
            // real cross-serializer assertion: it is fed to whichever serializer this TFM's build
            // actually uses, not a stand-in for one we couldn't run.
            var newtonsoftShapedJson = "{\"v\":1,\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var envelope = MessageEnvelope.Deserialize(newtonsoftShapedJson);

            Assert.AreEqual(1, envelope.Version);
            Assert.AreEqual("Foo", envelope.EventName);
            Assert.AreEqual("Bar", envelope.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_ProducesNewtonsoftShapedJson()
        {
            // The mirror image of the test above: proves MessageEnvelope.Serialize (System.Text.Json
            // on this TFM) emits byte-identical JSON to what the Newtonsoft path is expected to
            // produce for the same values, which is what actually matters for wire compatibility
            // (a net48 peer must be able to read it without knowing which serializer wrote it).
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var json = envelope.Serialize();

            // v:2 as of B3 (see MessageEnvelope.CurrentProtocolVersion doc comment) -- the payload
            // representation change is why this bumped from v:1.
            Assert.AreEqual("{\"v\":2,\"eventName\":\"Foo\",\"payload\":\"Bar\"}", json);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_PlainEvent_ContainsNoCidTypeOrErrorKeys()
        {
            // B2: cid/type/error are optional and absent-by-default, so a 1.2.0 fire-and-forget
            // message must stay exactly as small on the wire as a 1.1.x one.
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var json = envelope.Serialize();

            Assert.AreEqual("{\"v\":2,\"eventName\":\"Foo\",\"payload\":\"Bar\"}", json);
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("\"cid\"|\"type\"|\"error\""));
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_WithCidTypeAndError_Present()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"Bar")
            {
                Cid = "abc123",
                Type = MessageEnvelope.MessageTypeRequest,
                Error = new MessageError { Code = "TIMEOUT", Message = "boom" }
            };

            var json = envelope.Serialize();
            var roundTripped = MessageEnvelope.Deserialize(json);

            Assert.AreEqual("abc123", roundTripped.Cid);
            Assert.AreEqual(MessageEnvelope.MessageTypeRequest, roundTripped.Type);
            Assert.IsNotNull(roundTripped.Error);
            Assert.AreEqual("TIMEOUT", roundTripped.Error!.Code);
            Assert.AreEqual("boom", roundTripped.Error!.Message);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_WithCidTypeAndError_Absent()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var json = envelope.Serialize();
            var roundTripped = MessageEnvelope.Deserialize(json);

            Assert.IsNull(roundTripped.Cid);
            Assert.IsNull(roundTripped.Type);
            Assert.IsNull(roundTripped.Error);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_UnknownTypeDiscriminator_IsNotFatal()
        {
            // Documented fallback: HRpc does not interpret `type` in any way in v1.2.0, so an
            // unrecognized value must round-trip untouched rather than throwing or being reset.
            var json = "{\"v\":1,\"eventName\":\"Foo\",\"payload\":\"Bar\",\"type\":\"something-from-the-future\"}";

            var envelope = MessageEnvelope.Deserialize(json);

            Assert.AreEqual("something-from-the-future", envelope.Type);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_LegacyShapedJson_WithNoCidTypeOrError_StillDeserializes()
        {
            // A 1.1.x-shaped envelope (no v, no cid, no type, no error) must still deserialize
            // correctly under the B2 schema.
            var json = "{\"eventName\":\"Foo\",\"payload\":\"Bar\"}";

            var envelope = MessageEnvelope.Deserialize(json);

            Assert.AreEqual(MessageEnvelope.LegacyProtocolVersion, envelope.Version);
            Assert.AreEqual("Foo", envelope.EventName);
            Assert.AreEqual("Bar", envelope.GetPayloadAsString());
            Assert.IsNull(envelope.Cid);
            Assert.IsNull(envelope.Type);
            Assert.IsNull(envelope.Error);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_CrossSerializer_NewtonsoftShapedJson_WithCidTypeError_DeserializesViaMessageEnvelope()
        {
            // Mirrors MessageEnvelope_CrossSerializer_NewtonsoftShapedJson_DeserializesViaMessageEnvelope
            // above, exercising the B2 fields instead. As of D1 (net48;net8.0;net9.0 test matrix),
            // this hand-written camelCase JSON is fed to whichever serializer the current TFM's
            // build actually uses (Newtonsoft on net48, System.Text.Json on net8.0/net9.0), so it
            // is a real cross-serializer proof for cid/type/error, not a single-TFM stand-in.
            var json = "{\"v\":1,\"eventName\":\"Foo\",\"payload\":\"Bar\",\"cid\":\"abc123\",\"type\":\"response\",\"error\":{\"code\":\"E1\",\"message\":\"m\"}}";

            var envelope = MessageEnvelope.Deserialize(json);

            Assert.AreEqual("abc123", envelope.Cid);
            Assert.AreEqual("response", envelope.Type);
            Assert.IsNotNull(envelope.Error);
            Assert.AreEqual("E1", envelope.Error!.Code);
            Assert.AreEqual("m", envelope.Error!.Message);
        }

        // --- B3: typed payload ---

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_ObjectPayload()
        {
            var envelope = new MessageEnvelope("Foo", (object?)new { a = 1, b = "x" });

            var roundTripped = MessageEnvelope.Deserialize(envelope.Serialize());
            var payload = roundTripped.GetPayload<Dictionary<string, object>>();

            Assert.IsNotNull(payload);
            Assert.AreEqual("1", payload!["a"].ToString());
            Assert.AreEqual("x", payload["b"].ToString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_StringPayload()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"Bar");

            var roundTripped = MessageEnvelope.Deserialize(envelope.Serialize());

            Assert.AreEqual("Bar", roundTripped.GetPayload<string>());
            Assert.AreEqual("Bar", roundTripped.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_NullPayload()
        {
            var envelope = new MessageEnvelope("Foo", (object?)null);

            var roundTripped = MessageEnvelope.Deserialize(envelope.Serialize());

            Assert.IsNull(roundTripped.GetPayload<string>());
            Assert.AreEqual("null", roundTripped.GetPayloadAsString());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_EmptyObjectPayload()
        {
            var envelope = new MessageEnvelope("Foo", (object?)new Dictionary<string, object>());

            var json = envelope.Serialize();
            var roundTripped = MessageEnvelope.Deserialize(json);

            StringAssert.Contains(json, "\"payload\":{}");
            var payload = roundTripped.GetPayload<Dictionary<string, object>>();
            Assert.IsNotNull(payload);
            Assert.AreEqual(0, payload!.Count);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_ArrayPayload()
        {
            var envelope = new MessageEnvelope("Foo", (object?)new[] { 1, 2, 3 });

            var json = envelope.Serialize();
            var roundTripped = MessageEnvelope.Deserialize(json);

            StringAssert.Contains(json, "\"payload\":[1,2,3]");
            var payload = roundTripped.GetPayload<int[]>();
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, payload);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_RoundTrip_DeeplyNestedPayload()
        {
            var nested = new
            {
                level1 = new
                {
                    level2 = new
                    {
                        level3 = new[] { "a", "b", "c" },
                        flag = true
                    }
                }
            };
            var envelope = new MessageEnvelope("Foo", (object?)nested);

            var roundTripped = MessageEnvelope.Deserialize(envelope.Serialize());

            var text = roundTripped.GetPayloadAsString();
            StringAssert.Contains(text, "\"level3\":[\"a\",\"b\",\"c\"]");
            StringAssert.Contains(text, "\"flag\":true");
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_ObjectPayload_IsNotDoubleEscaped()
        {
            // The core B3 proof: an object payload must appear as a nested JSON object on the
            // wire, not as an escaped string. Before B3, this would have serialized as
            // "payload":"{\"a\":1}" (escaped, larger, and not typed-accessible on read).
            var envelope = new MessageEnvelope("Foo", (object?)new { a = 1 });

            var json = envelope.Serialize();

            Assert.AreEqual("{\"v\":2,\"eventName\":\"Foo\",\"payload\":{\"a\":1}}", json);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Serialize_ObjectPayload_IsSmallerThanOldEscapedStringForm()
        {
            // Demonstrates the byte-size win: the old API forced a consumer to pre-serialize an
            // object to a string, which then got re-escaped inside the outer envelope JSON.
#pragma warning disable CS0618 // deliberately constructing the old escaped-string shape for comparison
            var preSerializedPayload = "{\"a\":1,\"b\":\"x\"}";
            var oldForm = new MessageEnvelope("Foo", preSerializedPayload).Serialize();
#pragma warning restore CS0618

            var newForm = new MessageEnvelope("Foo", (object?)new { a = 1, b = "x" }).Serialize();

            Assert.IsTrue(
                Encoding.UTF8.GetByteCount(newForm) < Encoding.UTF8.GetByteCount(oldForm),
                $"Expected typed payload ({newForm}) to be smaller than the escaped-string form ({oldForm}).");
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_GetPayload_TypeMismatch_Throws()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"not a number");

            Assert.ThrowsException<JsonExceptionType>(() => envelope.GetPayload<int>());
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_TryGetPayload_TypeMismatch_ReturnsFalse()
        {
            var envelope = new MessageEnvelope("Foo", (object?)"not a number");

            var ok = envelope.TryGetPayload<int>(out var value);

            Assert.IsFalse(ok);
            Assert.AreEqual(0, value);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_TryGetPayload_MatchingType_ReturnsTrue()
        {
            var envelope = new MessageEnvelope("Foo", (object?)42);

            var ok = envelope.TryGetPayload<int>(out var value);

            Assert.IsTrue(ok);
            Assert.AreEqual(42, value);
        }

        [TestMethod]
        [Timeout(5000)]
        public void MessageEnvelope_Deserialize_LegacyRawJsonFixture_StillReadableIn120()
        {
            // A 1.1.x peer always wrote the payload as an escaped JSON string. Confirms 1.2.0
            // still reads that shape correctly via both the typed and string accessors.
            var raw = "{\"eventName\":\"Foo\",\"payload\":\"{\\\"a\\\":1}\"}";

            var envelope = MessageEnvelope.Deserialize(raw);

            Assert.AreEqual(MessageEnvelope.LegacyProtocolVersion, envelope.Version);
            Assert.AreEqual("{\"a\":1}", envelope.GetPayloadAsString());
            Assert.AreEqual("{\"a\":1}", envelope.GetPayload<string>());
        }

        // --- B3-EXT: typed payload on IEventMessage/EventMessage ---

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_SendAsync_ObjectPayload_WireBytesContainNestedObject_NotEscapedString()
        {
            // The acceptance test for B3-EXT: send an object payload through the PUBLIC API
            // (EventMessage(string, object?) + TcpConnection.SendAsync) and inspect the raw wire
            // bytes a peer actually receives. Before B3-EXT this would have been a re-escaped
            // string ("payload":"{\"a\":1}"); it must now be a nested JSON object.
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var lineTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var reader = new StreamReader(serverStream, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                lineTcs.TrySetResult(line ?? string.Empty);
            });

            var connection = new TcpClientWrapper();
            await connection.ConnectAsync("127.0.0.1", port);
            await connection.SendAsync(new EventMessage("Foo", (object?)new { a = 1 }));

            var completed = await Task.WhenAny(lineTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(lineTcs.Task, completed);
            StringAssert.Contains(lineTcs.Task.Result, "\"payload\":{\"a\":1}");
            StringAssert.DoesNotMatch(lineTcs.Task.Result, new System.Text.RegularExpressions.Regex("\"payload\":\""));

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeConnection_SendAsync_ObjectPayload_WireBytesContainNestedObject_NotEscapedString()
        {
            var pipeName = "hrpcb3extpipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var serverStream = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var lineTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = Task.Run(async () =>
            {
                await serverStream.WaitForConnectionAsync();
                var reader = new StreamReader(serverStream, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                lineTcs.TrySetResult(line ?? string.Empty);
            });

            var connection = new PipeClientWrapper();
            await connection.ConnectAsync(pipeName);
            await connection.SendAsync(new EventMessage("Foo", (object?)new { a = 1 }));

            var completed = await Task.WhenAny(lineTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(lineTcs.Task, completed);
            StringAssert.Contains(lineTcs.Task.Result, "\"payload\":{\"a\":1}");
            StringAssert.DoesNotMatch(lineTcs.Task.Result, new System.Text.RegularExpressions.Regex("\"payload\":\""));

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeServer_InitialMessage_ObjectPayload_WireBytesContainNestedObject_NotEscapedString()
        {
            // Server-side send path: PipeServer.InitialMessage is the only server-initiated send
            // capability in 1.2.0 (arbitrary server push is deferred to 1.3.0).
            var pipeName = "hrpcb3extsrv-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var server = new PipeServer
            {
                InitialMessage = new EventMessage("Foo", (object?)new { a = 1 })
            };
            var serverTask = server.StartAsync(pipeName);
            await Task.Delay(150);

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync();
            var reader = new StreamReader(client, Encoding.UTF8);
            var lineTask = reader.ReadLineAsync();

            var completed = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(lineTask, completed);
            var line = lineTask.Result ?? string.Empty;
            StringAssert.Contains(line, "\"payload\":{\"a\":1}");
            StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex("\"payload\":\""));

            await server.StopAsync();
            await serverTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_SendAsync_BothConstructorPaths_ProduceDistinctWireShapes()
        {
            // EventMessage(string, string) embeds a JSON-looking string literally as a JSON
            // STRING value (never sniffed/parsed); EventMessage(string, object?) embeds an
            // equivalent-content object as a nested JSON value. Deliberately different wire
            // shapes for "the same information" -- this is the resolution of the
            // constructor-ambiguity decision.
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var lines = new List<string>();
            var secondLineTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var reader = new StreamReader(serverStream, Encoding.UTF8);
                lines.Add(await reader.ReadLineAsync() ?? string.Empty);
                lines.Add(await reader.ReadLineAsync() ?? string.Empty);
                secondLineTcs.TrySetResult(true);
            });

            var connection = new TcpClientWrapper();
            await connection.ConnectAsync("127.0.0.1", port);

            var jsonLookingText = "{\"a\":1}";
            await connection.SendAsync(new EventMessage("Foo", jsonLookingText));
            await connection.SendAsync(new EventMessage("Foo", (object?)new { a = 1 }));

            var completed = await Task.WhenAny(secondLineTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(secondLineTcs.Task, completed);

            // string ctor: embedded verbatim as a JSON string value (escaped per the active
            // serializer's own rules) -- never parsed even though the text happens to look like
            // JSON. Assert on the decoded shape rather than the raw escaping, since the exact
            // escape sequence used for quotes is a serializer implementation detail.
            StringAssert.Contains(lines[0], "\"payload\":\"");
            var stringCtorEnvelope = MessageEnvelope.Deserialize(lines[0]);
            Assert.AreEqual(jsonLookingText, stringCtorEnvelope.GetPayload<string>());

            // object ctor: embedded as a nested JSON value, not escaped.
            StringAssert.Contains(lines[1], "\"payload\":{\"a\":1}");

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_SendAsync_FromJson_ProducesStructuredShape_NotEscapedString()
        {
            // A consumer holding pre-serialized JSON text (e.g. from another system) uses
            // FromJson to get the structured shape, not the string constructor.
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var lineTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var reader = new StreamReader(serverStream, Encoding.UTF8);
                var line = await reader.ReadLineAsync();
                lineTcs.TrySetResult(line ?? string.Empty);
            });

            var connection = new TcpClientWrapper();
            await connection.ConnectAsync("127.0.0.1", port);

            var preSerializedJson = "{\"a\":1,\"b\":\"x\"}";
            await connection.SendAsync(EventMessage.FromJson("Foo", preSerializedJson));

            var completed = await Task.WhenAny(lineTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(lineTcs.Task, completed);
            StringAssert.Contains(lineTcs.Task.Result, "\"payload\":{\"a\":1,\"b\":\"x\"}");
            StringAssert.DoesNotMatch(lineTcs.Task.Result, new System.Text.RegularExpressions.Regex("\"payload\":\""));

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(5000)]
        public void EventMessage_FromJson_InvalidJson_Throws()
        {
            // Assert.ThrowsException requires an exact type match, but the concrete type STJ
            // throws here (JsonReaderException) is internal and not nameable from this assembly
            // -- so assert on the public base type via a plain try/catch instead.
            try
            {
                EventMessage.FromJson("Foo", "not-json");
                Assert.Fail("Expected a JsonException to be thrown.");
            }
            catch (JsonExceptionType)
            {
                // expected
            }
        }

        [TestMethod]
        [Timeout(5000)]
        public void EventMessage_TryGetPayload_TypeMismatch_ReturnsFalse_DoesNotThrow()
        {
            // Re-verifies the Item 2 taxonomy now that PayloadValue crosses the receive-loop
            // boundary into user code: a payload-parse failure reached through a lazy accessor
            // on EventMessage must be catchable via TryGetPayload, not just on MessageEnvelope.
            var msg = new EventMessage("Foo", (object?)"not a number");

            var ok = msg.TryGetPayload<int>(out var value);

            Assert.IsFalse(ok);
            Assert.AreEqual(0, value);
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_ReceiveLoop_MalformedPayloadShape_DoesNotCrashLoop_OnlyLazyAccessThrows()
        {
            // Constructing EventMessage inside a receive loop must never throw due to payload
            // shape (the internal pass-through constructor assigns PayloadValue with zero
            // conversion). A shape mismatch can only surface later, when user code calls a lazy
            // accessor like GetPayload<T>() -- and even then it must not escape back into the
            // loop or disconnect the connection.
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var received = new TaskCompletionSource<IEventMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connection = new TcpClientWrapper();
            connection.MessageReceived += (_, e) => received.TrySetResult(e.Message);
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;
            var canClosePeer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var bytes = Encoding.UTF8.GetBytes(new MessageEnvelope("Foo", (object?)"not a number").Serialize() + "\n");
                await serverStream.WriteAsync(bytes, 0, bytes.Length);

                // Keep the peer's stream open until the test has finished checking that the
                // parse-failure-on-lazy-access didn't disconnect the connection -- otherwise the
                // peer closing early after writing would itself cause a legitimate disconnect
                // that has nothing to do with the payload-shape mismatch being tested here.
                await canClosePeer.Task;
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(received.Task, completed);

            // Construction succeeded (MessageReceived fired at all); the mismatch only surfaces
            // when GetPayload<int>() is explicitly called by user code.
            Assert.ThrowsException<JsonExceptionType>(() => received.Task.Result.GetPayload<int>());
            var ok = received.Task.Result.TryGetPayload<int>(out _);
            Assert.IsFalse(ok);
            Assert.IsFalse(disconnected);

            canClosePeer.TrySetResult(true);
            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_ShouldRaiseError_AndDisconnect_OnFutureProtocolVersion()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var connection = new TcpClientWrapper();
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var raw = "{\"v\":999,\"eventName\":\"Foo\",\"payload\":\"Bar\"}\n";
                var bytes = Encoding.UTF8.GetBytes(raw);
                try
                {
                    await serverStream.WriteAsync(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Expected once the client aborts and closes the connection.
                }
            });

            await connection.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(UnsupportedProtocolVersionException));

            var disconnectTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (!disconnected && !disconnectTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.IsTrue(disconnected);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task PipeConnection_ShouldRaiseError_AndDisconnect_OnFutureProtocolVersion()
        {
            // A raw NamedPipeServerStream is used directly (bypassing PipeServer) because
            // PipeServer.InitialMessage always serializes through MessageEnvelope, which always
            // stamps the current version -- it can't be made to emit an arbitrary "v" value.
            var pipeName = "hrpcverpipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var serverStream = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var connection = new PipeClientWrapper();
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);
            var disconnected = false;
            connection.Disconnected += (_, _) => disconnected = true;

            var acceptTask = Task.Run(async () =>
            {
                await serverStream.WaitForConnectionAsync();
                var raw = "{\"v\":999,\"eventName\":\"Foo\",\"payload\":\"Bar\"}\n";
                var bytes = Encoding.UTF8.GetBytes(raw);
                try
                {
                    await serverStream.WriteAsync(bytes, 0, bytes.Length);
                }
                catch
                {
                    // Expected once the client aborts and closes the connection.
                }
            });

            await connection.ConnectAsync(pipeName);

            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(errorTcs.Task, completed);
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(UnsupportedProtocolVersionException));

            var disconnectTimeout = Task.Delay(TimeSpan.FromSeconds(3));
            while (!disconnected && !disconnectTimeout.IsCompleted)
            {
                await Task.Delay(30);
            }
            Assert.IsTrue(disconnected);

            await connection.CloseAsync();
            await acceptTask;
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task TcpConnection_ShouldRaiseError_AndStop_OnUnboundedStreamWithNoNewline()
        {
            var port = GetFreePort();
            using var listener = new DisposableTcpListener(IPAddress.Loopback, port);
            listener.Start();

            var connection = new TcpClientWrapper { MaxMessageSizeBytes = 4096 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);

            var writesCompleted = 0;
            var totalWrites = 2000; // 2000 * 1024 bytes ~= 2MB, far beyond the 4096-byte limit, and never contains '\n'.
            using var writerCts = new CancellationTokenSource();

            var acceptTask = Task.Run(async () =>
            {
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var chunk = new byte[1024];
                ArrayFillX(chunk);

                try
                {
                    for (var i = 0; i < totalWrites && !writerCts.IsCancellationRequested; i++)
                    {
                        await serverStream.WriteAsync(chunk, 0, chunk.Length, writerCts.Token);
                        Interlocked.Increment(ref writesCompleted);
                    }
                }
                catch
                {
                    // Expected once the reader aborts and closes the connection.
                }
            });

            await connection.ConnectAsync("127.0.0.1", port);

            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(errorTcs.Task, completed, "Expected ErrorOccurred to fire for an unbounded, newline-less stream.");
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));

            writerCts.Cancel();
            await connection.CloseAsync();
            await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.IsTrue(writesCompleted < totalWrites,
                $"Expected the guard to abort before all {totalWrites} writes completed; {writesCompleted} completed.");
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task PipeConnection_ShouldRaiseError_AndStop_OnUnboundedStreamWithNoNewline()
        {
            var pipeName = "hrpcnlpipe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var serverStream = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var connection = new PipeClientWrapper { MaxMessageSizeBytes = 4096 };
            var errorTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ErrorOccurred += (_, e) => errorTcs.TrySetResult(e.Ex);

            var writesCompleted = 0;
            var totalWrites = 2000;
            using var writerCts = new CancellationTokenSource();

            var acceptTask = Task.Run(async () =>
            {
                await serverStream.WaitForConnectionAsync();
                var chunk = new byte[1024];
                ArrayFillX(chunk);

                try
                {
                    for (var i = 0; i < totalWrites && !writerCts.IsCancellationRequested; i++)
                    {
                        await serverStream.WriteAsync(chunk, 0, chunk.Length, writerCts.Token);
                        Interlocked.Increment(ref writesCompleted);
                    }
                }
                catch
                {
                    // Expected once the reader aborts and closes the connection.
                }
            });

            await connection.ConnectAsync(pipeName);

            var completed = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(errorTcs.Task, completed, "Expected ErrorOccurred to fire for an unbounded, newline-less stream.");
            Assert.IsInstanceOfType(errorTcs.Task.Result, typeof(LineTooLongException));

            writerCts.Cancel();
            await connection.CloseAsync();
            await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.IsTrue(writesCompleted < totalWrites,
                $"Expected the guard to abort before all {totalWrites} writes completed; {writesCompleted} completed.");
        }

        private static System.Collections.IDictionary GetClientTaskDictionary(object server)
        {
            var field = server.GetType().GetField("_clientTasks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (System.Collections.IDictionary)field!.GetValue(server)!;
        }

        private static bool AnyCompletedTaskLeaked(System.Collections.IDictionary clientTasks)
        {
            foreach (var value in clientTasks.Values)
            {
                if (((Task)value!).IsCompleted)
                {
                    return true;
                }
            }

            return false;
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

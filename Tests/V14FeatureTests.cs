using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Core;
using HRpc.Events;
using HRpc.Models;

namespace HRpc.Tests
{
    /// <summary>
    /// Tests for the 1.4.0 features: server push, client ids, request context and cancellation,
    /// MaxConcurrentRequests, late-response dropping, TCP keepalive, CurrentUserOnly pipes and
    /// the PipeServer accept-loop fix. Interop with the released 1.3.2 package is in
    /// CompatibilityTests.
    /// </summary>
    [TestClass]
    public class V14FeatureTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        // ---- PipeServer accept loop ------------------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public async Task PipeServer_Idle_DoesNotCreatePendingInstances()
        {
            // 1.3.x created a new waiting pipe instance on every accept-loop iteration, with only
            // a Task.Yield between them, so an idle server kept spinning and piling up instances.
            // Every instance was tracked in _clientTasks.
            var pipeName = NewPipeName();
            var server = new PipeServer();
            await server.StartAsync(pipeName);
            await Task.Delay(500);

            Assert.AreEqual(0, ClientTaskCount(server), "An idle PipeServer must not accumulate pipe instances.");

            var clients = new List<PipeClientWrapper>();
            for (var i = 0; i < 3; i++)
            {
                var client = new PipeClientWrapper();
                await client.ConnectAsync(pipeName);
                clients.Add(client);
            }

            await WaitUntil(() => server.ConnectedClients.Count == 3);
            await Task.Delay(300);
            Assert.AreEqual(3, ClientTaskCount(server), "Only connected clients should be tracked.");

            foreach (var client in clients)
            {
                await client.CloseAsync();
            }

            await WaitUntil(() => ClientTaskCount(server) == 0);
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(20000)]
        public async Task PipeServer_ManyClientsConnectingAtOnce_AllServed()
        {
            var pipeName = NewPipeName();
            var server = new PipeServer();
            server.OnRequest("Echo", msg => Task.FromResult<object?>(msg.GetPayloadAsString()));
            await server.StartAsync(pipeName);

            var calls = Enumerable.Range(0, 20).Select(async i =>
            {
                var client = new PipeClientWrapper();
                await client.ConnectAsync(pipeName);
                var reply = await client.SendAndWaitAsync(new EventMessage("Echo", "c" + i), Wait);
                await client.CloseAsync();
                return reply.GetPayloadAsString();
            });

            var replies = await Task.WhenAll(calls);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).Select(i => "c" + i).ToArray(), replies);
            await server.StopAsync();
        }

        // ---- server push and client ids ---------------------------------------------------

        [TestMethod]
        [Timeout(10000)]
        public async Task Tcp_Server_SendAsync_PushesToTheRightClient()
        {
            var port = GetFreePort();
            using var server = new Server { TransportType = TransportType.Tcp };
            await AssertPushToRightClient(server, port.ToString(), () => new Connection { TransportType = TransportType.Tcp }, $"127.0.0.1:{port}");
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Pipe_Server_SendAsync_PushesToTheRightClient()
        {
            var pipeName = NewPipeName();
            using var server = new Server { TransportType = TransportType.Pipe };
            await AssertPushToRightClient(server, pipeName, () => new Connection { TransportType = TransportType.Pipe }, pipeName);
        }

        private static async Task AssertPushToRightClient(Server server, string target, Func<Connection> newClient, string clientTarget)
        {
            var connectedIds = new ConcurrentQueue<string>();
            server.ClientConnected += (_, e) => connectedIds.Enqueue(e.ClientId!);
            await server.StartAsync(target);

            using var a = newClient();
            using var b = newClient();
            var aGot = new ConcurrentQueue<string>();
            var bGot = new ConcurrentQueue<string>();
            a.MessageReceived += (_, e) => aGot.Enqueue(e.Message.GetPayloadAsString()!);
            b.MessageReceived += (_, e) => bGot.Enqueue(e.Message.GetPayloadAsString()!);

            // Learn each client's id from the server side: the client says who it is.
            var idOf = new ConcurrentDictionary<string, string>();
            server.MessageReceived += (_, e) => idOf[e.Message.GetPayloadAsString()!] = e.ClientId!;

            await a.ConnectAsync(clientTarget);
            await b.ConnectAsync(clientTarget);
            await a.SendAsync(new EventMessage("Hi", "a"));
            await b.SendAsync(new EventMessage("Hi", "b"));
            await WaitUntil(() => idOf.Count == 2);

            CollectionAssert.AreEquivalent(connectedIds.ToArray(), server.ConnectedClients.ToArray());
            CollectionAssert.AreEquivalent(connectedIds.ToArray(), idOf.Values.ToArray());

            await server.SendAsync(idOf["a"], new EventMessage("Push", "for-a"));
            await server.SendAsync(idOf["b"], new EventMessage("Push", "for-b"));
            await WaitUntil(() => aGot.Count == 1 && bGot.Count == 1);
            await Task.Delay(100);

            CollectionAssert.AreEqual(new[] { "for-a" }, aGot.ToArray());
            CollectionAssert.AreEqual(new[] { "for-b" }, bGot.ToArray());

            await a.CloseAsync();
            await WaitUntil(() => server.ConnectedClients.Count == 1);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => server.SendAsync(idOf["a"], new EventMessage("Push", "gone")));

            await b.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpServer_BroadcastAsync_ReachesEveryClient()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            await server.StartAsync(port);

            var received = new ConcurrentBag<string>();
            var clients = new List<TcpClientWrapper>();
            for (var i = 0; i < 3; i++)
            {
                var client = new TcpClientWrapper();
                var name = "client" + i;
                client.MessageReceived += (_, e) => received.Add(name + ":" + e.Message.GetPayloadAsString());
                await client.ConnectAsync("127.0.0.1", port);
                clients.Add(client);
            }

            await WaitUntil(() => server.ConnectedClients.Count == 3);
            var delivered = await server.BroadcastAsync(new EventMessage("News", "hello"));

            Assert.AreEqual(3, delivered);
            await WaitUntil(() => received.Count == 3);
            CollectionAssert.AreEquivalent(new[] { "client0:hello", "client1:hello", "client2:hello" }, received.ToArray());

            foreach (var client in clients)
            {
                await client.CloseAsync();
            }

            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Server_PushFromClientConnected_IsDeliveredFirst()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            server.ClientConnected += (_, e) => _ = server.SendAsync(e.ClientId!, new EventMessage("Welcome", e.ClientId!));
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            var welcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.MessageReceived += (_, e) => welcome.TrySetResult(e.Message.EventName);
            await client.ConnectAsync("127.0.0.1", port);

            Assert.AreEqual("Welcome", await welcome.Task.WithTimeout(Wait));
            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(5000)]
        public async Task Server_SendAsync_BeforeStart_Throws()
        {
            using var server = new Server();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => server.SendAsync("x", new EventMessage("E", "p")));
            Assert.AreEqual(0, server.ConnectedClients.Count);
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task ClientDisconnected_CarriesTheSameClientId()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += (_, e) => connected.TrySetResult(e.ClientId!);
            server.ClientDisconnected += (_, e) => disconnected.TrySetResult(e.ClientId!);
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var id = await connected.Task.WithTimeout(Wait);
            Assert.IsFalse(string.IsNullOrEmpty(id));

            await client.CloseAsync();
            Assert.AreEqual(id, await disconnected.Task.WithTimeout(Wait));
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpServer_ThrowingClientConnectedSubscriber_DoesNotStopAccepting()
        {
            // 1.3 raised ClientConnected unguarded inside the accept loop, so one throwing
            // subscriber ended the loop and the server stopped accepting anyone.
            var port = GetFreePort();
            var server = new TcpServer();
            var errors = new ConcurrentQueue<string>();
            server.ErrorOccurred += (_, e) => errors.Enqueue(e.Message);
            server.ClientConnected += (_, __) => throw new InvalidOperationException("subscriber bug");
            server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
            await server.StartAsync(port);

            for (var i = 0; i < 2; i++)
            {
                var client = new TcpClientWrapper();
                await client.ConnectAsync("127.0.0.1", port);
                var reply = await client.SendAndWaitAsync(new EventMessage("Ping", ""), Wait);
                Assert.AreEqual("pong", reply.GetPayloadAsString());
                await client.CloseAsync();
            }

            Assert.IsTrue(errors.Contains("Unhandled exception in ClientConnected subscriber"));
            await server.StopAsync();
        }

        // ---- request context and cancellation --------------------------------------------

        [TestMethod]
        [Timeout(10000)]
        public async Task RequestContext_CarriesTheCallingClientId()
        {
            var port = GetFreePort();
            using var server = new Server { TransportType = TransportType.Tcp };
            var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientConnected += (_, e) => connected.TrySetResult(e.ClientId!);
            server.OnRequest("WhoAmI", (msg, ctx) => Task.FromResult<object?>(ctx.ClientId));
            await server.StartAsync(port.ToString());

            using var client = new Connection { TransportType = TransportType.Tcp };
            await client.ConnectAsync($"127.0.0.1:{port}");
            var reply = await client.SendAndWaitAsync(new EventMessage("WhoAmI", ""), Wait);

            Assert.AreEqual(await connected.Task.WithTimeout(Wait), reply.GetPayloadAsString());
            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Tcp_CallerCancellation_CancelsTheServerHandler()
        {
            var port = GetFreePort();
            using var server = new Server { TransportType = TransportType.Tcp };
            await AssertCallerCancellationReachesHandler(server, port.ToString(), new Connection { TransportType = TransportType.Tcp }, $"127.0.0.1:{port}");
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Pipe_CallerCancellation_CancelsTheServerHandler()
        {
            var pipeName = NewPipeName();
            using var server = new Server { TransportType = TransportType.Pipe };
            await AssertCallerCancellationReachesHandler(server, pipeName, new Connection { TransportType = TransportType.Pipe }, pipeName);
        }

        private static async Task AssertCallerCancellationReachesHandler(Server server, string target, Connection client, string clientTarget)
        {
            var handlerCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnRequest("Slow", async (msg, ctx) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    handlerCancelled.TrySetResult(true);
                    throw;
                }

                return null;
            });
            server.OnRequest("Fast", msg => Task.FromResult<object?>("ok"));
            await server.StartAsync(target);

            using (client)
            {
                var stray = 0;
                client.MessageReceived += (_, __) => Interlocked.Increment(ref stray);
                await client.ConnectAsync(clientTarget);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                    () => client.SendAndWaitAsync(new EventMessage("Slow", ""), TimeSpan.FromSeconds(30), cts.Token));

                Assert.IsTrue(await handlerCancelled.Task.WithTimeout(Wait), "The server handler should see the cancellation.");

                // The connection is still healthy, and the cancelled request produced no reply.
                var reply = await client.SendAndWaitAsync(new EventMessage("Fast", ""), Wait);
                Assert.AreEqual("ok", reply.GetPayloadAsString());
                await Task.Delay(200);
                Assert.AreEqual(0, Volatile.Read(ref stray));

                await client.CloseAsync();
            }

            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Timeout_AlsoCancelsTheServerHandler()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var handlerCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnRequest("Slow", async (msg, ctx) =>
            {
                using var registration = ctx.CancellationToken.Register(() => handlerCancelled.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                return null;
            });
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => client.SendAndWaitAsync(new EventMessage("Slow", ""), TimeSpan.FromMilliseconds(300)));

            Assert.IsTrue(await handlerCancelled.Task.WithTimeout(Wait));
            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task CancelledWhileQueued_HandlerNeverRuns()
        {
            // Default MaxConcurrentRequests = 1: the second request waits behind the first. If
            // the caller gives up on it meanwhile, its handler must not run at all.
            var port = GetFreePort();
            var server = new TcpServer();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRan = false;
            server.OnRequest("First", async msg => { await gate.Task; return "first"; });
            server.OnRequest("Second", msg => { secondRan = true; return Task.FromResult<object?>("second"); });
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var first = client.SendAndWaitAsync(new EventMessage("First", ""), Wait);
            await Task.Delay(100);

            using var cts = new CancellationTokenSource();
            var second = client.SendAndWaitAsync(new EventMessage("Second", ""), Wait, cts.Token);
            await Task.Delay(100);
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => second);
            await Task.Delay(200); // let the cancel envelope reach the server

            gate.TrySetResult(true);
            Assert.AreEqual("first", (await first).GetPayloadAsString());
            await Task.Delay(200);
            Assert.IsFalse(secondRan);

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task ClientDisconnect_CancelsTheHandlerToken()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnRequest("Slow", async (msg, ctx) =>
            {
                started.TrySetResult(true);
                using var registration = ctx.CancellationToken.Register(() => handlerCancelled.TrySetResult(true));
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                return null;
            });
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var pending = client.SendAndWaitAsync(new EventMessage("Slow", ""), TimeSpan.FromSeconds(30));
            await started.Task.WithTimeout(Wait);
            await client.CloseAsync();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => pending);

            Assert.IsTrue(await handlerCancelled.Task.WithTimeout(Wait));
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task HandlerOwnOperationCanceled_StillBecomesErrorResponse()
        {
            // Only a cancellation of the request's own token suppresses the reply. A handler that
            // throws OperationCanceledException for its own reasons fails the request as in 1.3.
            var port = GetFreePort();
            var server = new TcpServer();
            server.OnRequest("Own", (msg, ctx) => throw new OperationCanceledException("handler gave up"));
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var ex = await Assert.ThrowsExceptionAsync<RequestFailedException>(
                () => client.SendAndWaitAsync(new EventMessage("Own", ""), Wait));
            Assert.AreEqual(nameof(OperationCanceledException), ex.Code);

            await client.CloseAsync();
            await server.StopAsync();
        }

        // ---- ordering and MaxConcurrentRequests ------------------------------------------

        [TestMethod]
        [Timeout(10000)]
        public async Task Default_EventAfterRequest_WaitsForTheHandler()
        {
            // The 1.3 contract, kept by default: a client's messages are handled in order, so an
            // event sent after a request is delivered only once that request's handler is done.
            var port = GetFreePort();
            var server = new TcpServer();
            Assert.AreEqual(1, server.MaxConcurrentRequests);

            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new ConcurrentQueue<string>();
            server.OnRequest("Slow", async msg =>
            {
                await gate.Task;
                order.Enqueue("handler-done");
                return null;
            });
            server.MessageReceived += (_, e) => order.Enqueue("event:" + e.Message.EventName);
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var request = client.SendAndWaitAsync(new EventMessage("Slow", ""), Wait);
            await client.SendAsync(new EventMessage("After", ""));
            await Task.Delay(300);

            Assert.AreEqual(0, order.Count, "The event must not overtake the running request.");
            gate.TrySetResult(true);
            await request;
            await WaitUntil(() => order.Count == 2);
            CollectionAssert.AreEqual(new[] { "handler-done", "event:After" }, order.ToArray());

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task MaxConcurrentRequests_LetsHandlersOverlap()
        {
            var port = GetFreePort();
            var server = new TcpServer { MaxConcurrentRequests = 4 };
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = 0;
            var peak = 0;
            server.OnRequest("Work", async msg =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await gate.Task;
                Interlocked.Decrement(ref running);
                return msg.GetPayloadAsString();
            });
            var eventSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.MessageReceived += (_, __) => eventSeen.TrySetResult(true);
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            var calls = Enumerable.Range(0, 3).Select(i => client.SendAndWaitAsync(new EventMessage("Work", "w" + i), Wait)).ToArray();
            await client.SendAsync(new EventMessage("Meanwhile", ""));

            await WaitUntil(() => Volatile.Read(ref running) == 3);
            Assert.IsTrue(await eventSeen.Task.WithTimeout(Wait), "An event should not wait for running handlers.");

            gate.TrySetResult(true);
            var replies = await Task.WhenAll(calls);
            CollectionAssert.AreEquivalent(new[] { "w0", "w1", "w2" }, replies.Select(r => r.GetPayloadAsString()).ToArray());
            Assert.AreEqual(3, peak);

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task MaxConcurrentRequests_IsALimit()
        {
            var port = GetFreePort();
            var server = new TcpServer { MaxConcurrentRequests = 2 };
            var running = 0;
            var peak = 0;
            server.OnRequest("Work", async msg =>
            {
                InterlockedMax(ref peak, Interlocked.Increment(ref running));
                await Task.Delay(100);
                Interlocked.Decrement(ref running);
                return null;
            });
            await server.StartAsync(port);

            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", port);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAndWaitAsync(new EventMessage("Work", ""), Wait)));

            Assert.AreEqual(2, peak);
            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        public void MaxConcurrentRequests_BelowOne_Throws()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TcpServer { MaxConcurrentRequests = 0 });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new PipeServer { MaxConcurrentRequests = 0 });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Server { MaxConcurrentRequests = -1 });
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task QueuedMessages_AreDeliveredBeforeClientDisconnected()
        {
            // A client that sends and immediately closes: everything it sent is still handled, in
            // order, before ClientDisconnected, as in 1.3.
            var port = GetFreePort();
            var server = new TcpServer();
            var log = new ConcurrentQueue<string>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.MessageReceived += (_, e) =>
            {
                Thread.Sleep(5);
                log.Enqueue(e.Message.GetPayloadAsString()!);
            };
            server.ClientDisconnected += (_, __) => { log.Enqueue("disconnected"); done.TrySetResult(true); };
            await server.StartAsync(port);

            using (var raw = new TcpClient())
            {
                await raw.ConnectAsync("127.0.0.1", port);
                var stream = raw.GetStream();
                var lines = string.Concat(Enumerable.Range(0, 100).Select(i => new MessageEnvelope("E", (object?)("m" + i)).Serialize() + "\n"));
                var bytes = Encoding.UTF8.GetBytes(lines);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            await done.Task.WithTimeout(Wait);
            var expected = Enumerable.Range(0, 100).Select(i => "m" + i).Concat(new[] { "disconnected" }).ToArray();
            CollectionAssert.AreEqual(expected, log.ToArray());
            await server.StopAsync();
        }

        // ---- wire behavior -----------------------------------------------------------------

        [TestMethod]
        [Timeout(10000)]
        public async Task Client_AdvertisesCapsOnlyOnItsFirstRequest_NeverOnEvents()
        {
            using var peer = new RawTcpPeer();
            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", peer.Port);
            await peer.AcceptAsync();

            await client.SendAsync(new EventMessage("Evt", "x"));
            var eventLine = await peer.ReadLineAsync();
            Assert.IsFalse(eventLine.Contains("caps"), eventLine);

            _ = client.SendAndWaitAsync(new EventMessage("Req", "1"), TimeSpan.FromMilliseconds(200)).ContinueWith(t => t.Exception);
            var firstRequest = await peer.ReadLineAsync();
            StringAssert.Contains(firstRequest, "\"caps\":[\"cancel\"]");

            _ = client.SendAndWaitAsync(new EventMessage("Req", "2"), TimeSpan.FromMilliseconds(200)).ContinueWith(t => t.Exception);
            var secondRequest = await peer.ReadLineAsync();
            Assert.IsFalse(secondRequest.Contains("caps"), secondRequest);

            await client.CloseAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Client_NeverSendsCancel_ToAServerThatDidNotSayHello()
        {
            // A 1.3 server never sends hello. Sending it "cancel" would surface as a stray
            // MessageReceived event there, so the client must stay silent.
            using var peer = new RawTcpPeer();
            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", peer.Port);
            await peer.AcceptAsync();

            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => client.SendAndWaitAsync(new EventMessage("Req", ""), TimeSpan.FromMilliseconds(200)));
            await peer.ReadLineAsync(); // the request itself

            Assert.IsNull(await peer.TryReadLineAsync(TimeSpan.FromMilliseconds(700)), "No cancel may follow.");
            await client.CloseAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Client_SendsCancel_AfterHello()
        {
            using var peer = new RawTcpPeer();
            var client = new TcpClientWrapper();
            await client.ConnectAsync("127.0.0.1", peer.Port);
            await peer.AcceptAsync();

            using var cts = new CancellationTokenSource();
            var call = client.SendAndWaitAsync(new EventMessage("Req", ""), Wait, cts.Token);
            var request = MessageEnvelope.Deserialize(await peer.ReadLineAsync());
            await peer.WriteLineAsync(new MessageEnvelope(string.Empty, (object?)null)
            {
                Type = MessageEnvelope.MessageTypeHello,
                Caps = new[] { MessageEnvelope.CapabilityCancel },
            }.Serialize());
            await Task.Delay(100);
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => call);

            var cancel = MessageEnvelope.Deserialize(await peer.ReadLineAsync());
            Assert.AreEqual(MessageEnvelope.MessageTypeCancel, cancel.Type);
            Assert.AreEqual(request.Cid, cancel.Cid);
            await client.CloseAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Client_DropsLateResponse_ButStillSurfacesUnknownCids()
        {
            using var peer = new RawTcpPeer();
            var client = new TcpClientWrapper();
            var received = new ConcurrentQueue<string>();
            client.MessageReceived += (_, e) => received.Enqueue(e.Message.EventName);
            await client.ConnectAsync("127.0.0.1", peer.Port);
            await peer.AcceptAsync();

            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => client.SendAndWaitAsync(new EventMessage("Late", ""), TimeSpan.FromMilliseconds(150)));
            var request = MessageEnvelope.Deserialize(await peer.ReadLineAsync());

            await peer.WriteLineAsync(new MessageEnvelope("Late", (object?)("too late")) { Cid = request.Cid, Type = MessageEnvelope.MessageTypeResponse }.Serialize());
            await peer.WriteLineAsync(new MessageEnvelope("Stranger", (object?)("x")) { Cid = "not-ours", Type = MessageEnvelope.MessageTypeResponse }.Serialize());
            await peer.WriteLineAsync(new MessageEnvelope("Marker", (object?)("x")).Serialize());

            await WaitUntil(() => received.Contains("Marker"));
            CollectionAssert.AreEqual(new[] { "Stranger", "Marker" }, received.ToArray());
            await client.CloseAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Server_SaysHello_OnlyToClientsThatAdvertiseCaps()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            server.OnRequest("Echo", msg => Task.FromResult<object?>(msg.GetPayloadAsString()));
            await server.StartAsync(port);

            // 1.3-shaped request: no caps, so the reply is the very first line.
            using (var raw = new RawTcpClient(port))
            {
                await raw.WriteLineAsync(new MessageEnvelope("Echo", (object?)("a")) { Cid = "c1", Type = MessageEnvelope.MessageTypeRequest }.Serialize());
                var first = MessageEnvelope.Deserialize(await raw.ReadLineAsync());
                Assert.AreEqual(MessageEnvelope.MessageTypeResponse, first.Type);
                Assert.AreEqual("c1", first.Cid);
                Assert.IsNull(await raw.TryReadLineAsync(TimeSpan.FromMilliseconds(300)));
            }

            // 1.4-shaped request: hello first, then the reply.
            using (var raw = new RawTcpClient(port))
            {
                await raw.WriteLineAsync(new MessageEnvelope("Echo", (object?)("b")) { Cid = "c2", Type = MessageEnvelope.MessageTypeRequest, Caps = new[] { "cancel" } }.Serialize());
                var hello = MessageEnvelope.Deserialize(await raw.ReadLineAsync());
                Assert.AreEqual(MessageEnvelope.MessageTypeHello, hello.Type);
                CollectionAssert.Contains(hello.Caps, MessageEnvelope.CapabilityCancel);
                var reply = MessageEnvelope.Deserialize(await raw.ReadLineAsync());
                Assert.AreEqual("c2", reply.Cid);
                Assert.AreEqual("b", reply.GetPayloadAsString());
            }

            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Server_IgnoresCancelForUnknownCid_AndDoesNotRaiseIt()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            var received = new ConcurrentQueue<string>();
            server.MessageReceived += (_, e) => received.Enqueue(e.Message.EventName);
            await server.StartAsync(port);

            using (var raw = new RawTcpClient(port))
            {
                await raw.WriteLineAsync(new MessageEnvelope("X", (object?)null) { Cid = "nope", Type = MessageEnvelope.MessageTypeCancel }.Serialize());
                await raw.WriteLineAsync(new MessageEnvelope("Marker", (object?)("m")).Serialize());
                await WaitUntil(() => received.Count > 0);
            }

            CollectionAssert.AreEqual(new[] { "Marker" }, received.ToArray());
            await server.StopAsync();
        }

        [TestMethod]
        public void Envelope_Caps_OmittedWhenNull_RoundTripsWhenSet()
        {
            var plain = new MessageEnvelope("E", (object?)("p")).Serialize();
            Assert.IsFalse(plain.Contains("caps"), plain);

            var withCaps = new MessageEnvelope("E", (object?)("p")) { Caps = new[] { "cancel" } }.Serialize();
            CollectionAssert.AreEqual(new[] { "cancel" }, MessageEnvelope.Deserialize(withCaps).Caps);
        }

        // ---- keepalive -----------------------------------------------------------------------

        private sealed class InspectableTcpConnection : TcpConnection
        {
            public Socket Socket => _client.Client;
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_KeepAlive_SetsSocketOptions()
        {
            var port = GetFreePort();
            var server = new TcpServer { KeepAlive = TimeSpan.FromSeconds(9) };
            server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
            await server.StartAsync(port);

            var client = new InspectableTcpConnection { KeepAlive = TimeSpan.FromSeconds(7) };
            await client.ConnectAsync("127.0.0.1", port);

            Assert.AreEqual(1, Convert.ToInt32(client.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)) != 0 ? 1 : 0);
#if !NETFRAMEWORK
            Assert.AreEqual(7, (int)client.Socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
#endif
            var reply = await client.SendAndWaitAsync(new EventMessage("Ping", ""), Wait);
            Assert.AreEqual("pong", reply.GetPayloadAsString());

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task TcpConnection_WithoutKeepAlive_LeavesSocketAlone()
        {
            var port = GetFreePort();
            var server = new TcpServer();
            await server.StartAsync(port);

            var client = new InspectableTcpConnection();
            await client.ConnectAsync("127.0.0.1", port);
            Assert.AreEqual(0, Convert.ToInt32(client.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)));

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        public void KeepAlive_MustBePositive()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TcpConnection { KeepAlive = TimeSpan.Zero });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new TcpServer { KeepAlive = TimeSpan.FromSeconds(-1) });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Connection { KeepAlive = TimeSpan.Zero });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Server { KeepAlive = TimeSpan.Zero });
            Assert.IsNull(new TcpConnection().KeepAlive);
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Facades_KeepAlive_EndToEnd()
        {
            var port = GetFreePort();
            using var server = new Server { TransportType = TransportType.Tcp, KeepAlive = TimeSpan.FromSeconds(5) };
            server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
            await server.StartAsync(port.ToString());

            using var client = new Connection { TransportType = TransportType.Tcp, KeepAlive = TimeSpan.FromSeconds(5) };
            await client.ConnectAsync($"127.0.0.1:{port}");
            Assert.AreEqual("pong", (await client.SendAndWaitAsync(new EventMessage("Ping", ""), Wait)).GetPayloadAsString());

            await client.CloseAsync();
            await server.StopAsync();
        }

        // ---- CurrentUserOnly pipes ----------------------------------------------------------

        [TestMethod]
        [Timeout(10000)]
        public async Task Pipe_CurrentUserOnly_SameUser_Works()
        {
            var pipeName = NewPipeName();
            using var server = new Server { TransportType = TransportType.Pipe, CurrentUserOnly = true };
            server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
            await server.StartAsync(pipeName);

            using var client = new Connection { TransportType = TransportType.Pipe, CurrentUserOnly = true };
            await client.ConnectAsync(pipeName);
            Assert.AreEqual("pong", (await client.SendAndWaitAsync(new EventMessage("Ping", ""), Wait)).GetPayloadAsString());

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(10000)]
        public async Task Pipe_CurrentUserOnlyServer_AcceptsDefaultClient_OfTheSameUser()
        {
            var pipeName = NewPipeName();
            var server = new PipeServer { CurrentUserOnly = true };
            server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
            await server.StartAsync(pipeName);

            var client = new PipeClientWrapper();
            await client.ConnectAsync(pipeName);
            Assert.AreEqual("pong", (await client.SendAndWaitAsync(new EventMessage("Ping", ""), Wait)).GetPayloadAsString());

            await client.CloseAsync();
            await server.StopAsync();
        }

        [TestMethod]
        public void CurrentUserOnly_IsOffByDefault()
        {
            Assert.IsFalse(new PipeServer().CurrentUserOnly);
            Assert.IsFalse(new PipeConnection().CurrentUserOnly);
            Assert.IsFalse(new Server().CurrentUserOnly);
            Assert.IsFalse(new Connection().CurrentUserOnly);
        }

        // ---- helpers ------------------------------------------------------------------------

        internal static string NewPipeName() => "hrpc14-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        internal static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Condition not met in time.");
                }

                await Task.Delay(20);
            }
        }

        private static int ClientTaskCount(object server)
        {
            var field = server.GetType().GetField("_clientTasks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return ((System.Collections.ICollection)field!.GetValue(server)!).Count;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    internal static class TestTaskExtensions
    {
        public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
            {
                Assert.Fail("Timed out waiting.");
            }

            return await task;
        }
    }

    /// <summary>A bare TCP peer that speaks raw lines, standing in for a hand-written or 1.3 peer.</summary>
    internal abstract class RawLinePeer : IDisposable
    {
        private StreamReader? _reader;
        private Stream? _stream;
        private Task<string?>? _pendingRead;

        protected void Attach(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
        }

        public async Task<string> ReadLineAsync()
        {
            var line = await TryReadLineAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(line, "Expected a line from the peer.");
            return line!;
        }

        public async Task<string?> TryReadLineAsync(TimeSpan timeout)
        {
            _pendingRead ??= _reader!.ReadLineAsync();
            if (await Task.WhenAny(_pendingRead, Task.Delay(timeout)) != _pendingRead)
            {
                return null;
            }

            var line = await _pendingRead;
            _pendingRead = null;
            return line;
        }

        public async Task WriteLineAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _stream!.WriteAsync(bytes, 0, bytes.Length);
        }

        public abstract void Dispose();
    }

    internal sealed class RawTcpPeer : RawLinePeer
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private TcpClient? _accepted;

        public RawTcpPeer()
        {
            _listener.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task AcceptAsync()
        {
            _accepted = await _listener.AcceptTcpClientAsync();
            Attach(_accepted.GetStream());
        }

        public override void Dispose()
        {
            _accepted?.Close();
            _listener.Stop();
        }
    }

    internal sealed class RawTcpClient : RawLinePeer
    {
        private readonly TcpClient _client = new TcpClient();

        public RawTcpClient(int port)
        {
            _client.Connect(IPAddress.Loopback, port);
            Attach(_client.GetStream());
        }

        public override void Dispose()
        {
            _client.Close();
        }
    }
}

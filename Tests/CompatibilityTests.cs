#if !NETFRAMEWORK
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Core;
using HRpc.Models;

namespace HRpc.Tests
{
    /// <summary>
    /// Interop between this build and the released HRpc 1.3.2 package, both directions, over TCP
    /// and named pipes. The 1.3.2 assembly comes from NuGet (see HRpc.Tests.csproj) and is loaded
    /// side by side in its own AssemblyLoadContext, so the peer is the real shipped binary rather
    /// than a simulation of it. net48 has no AssemblyLoadContext; the wire is identical there.
    /// </summary>
    [TestClass]
    public class CompatibilityTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

        [TestMethod]
        public void LoadsTheReleased132Assembly()
        {
            StringAssert.Contains(V13.Assembly.Location, Path.Combine("compat", "v1.3.2"));

            // The shipped assembly carries no version attributes, so check it is genuinely pre-1.4.
            Assert.IsNull(V13.Assembly.GetType("HRpc.Core.TcpServer")!.GetMethod("BroadcastAsync"));
            Assert.IsNull(V13.Assembly.GetType("HRpc.Core.RequestContext"));
            Assert.IsNotNull(typeof(TcpServer).GetMethod("BroadcastAsync"));
            Assert.AreNotSame(typeof(TcpServer).Assembly, V13.Assembly);
        }

        // ---- 1.3 client -> 1.4 server ----------------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public Task OldClient_NewTcpServer() => OldClientAgainstNewServer(TransportType.Tcp, maxConcurrent: 1);

        [TestMethod]
        [Timeout(15000)]
        public Task OldClient_NewPipeServer() => OldClientAgainstNewServer(TransportType.Pipe, maxConcurrent: 1);

        [TestMethod]
        [Timeout(15000)]
        public Task OldClient_NewTcpServer_ConcurrentRequests() => OldClientAgainstNewServer(TransportType.Tcp, maxConcurrent: 4);

        private static async Task OldClientAgainstNewServer(TransportType transport, int maxConcurrent)
        {
            var target = transport == TransportType.Tcp ? V14FeatureTests.GetFreePort().ToString() : V14FeatureTests.NewPipeName();
            using var server = new Server { TransportType = transport, MaxConcurrentRequests = maxConcurrent };
            var serverEvents = new ConcurrentQueue<string>();
            var clientIds = new ConcurrentQueue<string>();
            server.MessageReceived += (_, e) => serverEvents.Enqueue(e.Message.EventName + "=" + e.Message.GetPayloadAsString());
            server.ClientConnected += (_, e) => clientIds.Enqueue(e.ClientId!);
            server.OnRequest("Echo", msg => Task.FromResult<object?>(msg.GetPayloadAsString()));
            server.OnRequest("Typed", msg => Task.FromResult<object?>(new { Sum = 3, Name = "x" }));
            server.OnRequest("Ctx", (msg, ctx) => Task.FromResult<object?>(ctx.ClientId));
            server.OnRequest("Fail", msg => throw new ArgumentException("bad input"));
            await server.StartAsync(target);

            var client = V13.NewClient(transport);
            var clientEvents = new ConcurrentQueue<string>();
            var clientErrors = new ConcurrentQueue<string>();
            V13.On(client, "MessageReceived", e => clientEvents.Enqueue((string)e.Message.EventName + "=" + (string)e.Message.GetPayloadAsString()));
            V13.On(client, "ErrorOccurred", e => clientErrors.Enqueue((string)e.Message));
            await V13.ConnectAsync(client, transport, target);

            // Requests, including the 1.4 context overload and typed payloads.
            Assert.AreEqual("hi", await V13.RequestAsync(client, "Echo", "hi"));
            StringAssert.Contains(await V13.RequestRawAsync(client, "Typed"), "\"Sum\":3");
            await V14FeatureTests.WaitUntil(() => clientIds.Count == 1);
            Assert.AreEqual(clientIds.Single(), await V13.RequestAsync(client, "Ctx", ""));

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => V13.RequestAsync(client, "Echo", "n" + i)));
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).Select(i => "n" + i).ToArray(), concurrent);

            // Errors still map to 1.3's RequestFailedException.
            var failure = await V13.CatchAsync(V13.RequestAsync(client, "Fail", ""));
            Assert.AreEqual("RequestFailedException", failure.GetType().Name);
            Assert.AreSame(V13.Assembly, failure.GetType().Assembly);
            Assert.AreEqual("ArgumentException", (string)((dynamic)failure).Code);

            // Fire-and-forget events.
            await V13.SendAsync(client, "Note", "one");
            await V14FeatureTests.WaitUntil(() => serverEvents.Count == 1);
            CollectionAssert.AreEqual(new[] { "Note=one" }, serverEvents.ToArray());

            // Server push reaches a 1.3 client as an ordinary event.
            await server.SendAsync(clientIds.Single(), new EventMessage("Push", "direct"));
            Assert.AreEqual(1, await server.BroadcastAsync(new EventMessage("Push", "all")));
            await V14FeatureTests.WaitUntil(() => clientEvents.Count == 2);
            await Task.Delay(200);

            // Nothing else: a 1.3 client never advertises caps, so it never sees a hello.
            CollectionAssert.AreEqual(new[] { "Push=direct", "Push=all" }, clientEvents.ToArray());
            Assert.AreEqual(0, clientErrors.Count, string.Join("; ", clientErrors));

            await V13.CloseAsync(client);
            await V14FeatureTests.WaitUntil(() => server.ConnectedClients.Count == 0);
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task OldClient_TimesOut_NewServerKeepsServingIt()
        {
            // A 1.3 client gives up silently (it has no cancel), so the 1.4 handler simply runs
            // to completion and the connection carries on as it did with a 1.3 server.
            var port = V14FeatureTests.GetFreePort();
            var server = new TcpServer();
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnRequest("Slow", async (msg, ctx) =>
            {
                await gate.Task;
                completed.TrySetResult(!ctx.CancellationToken.IsCancellationRequested);
                return "late";
            });
            server.OnRequest("Echo", msg => Task.FromResult<object?>(msg.GetPayloadAsString()));
            await server.StartAsync(port);

            var client = V13.NewClient(TransportType.Tcp);
            await V13.ConnectAsync(client, TransportType.Tcp, port.ToString());

            var timeout = await V13.CatchAsync(V13.RequestAsync(client, "Slow", "", TimeSpan.FromMilliseconds(200)));
            Assert.IsInstanceOfType(timeout, typeof(TimeoutException));

            gate.TrySetResult(true);
            Assert.IsTrue(await completed.Task.WithTimeout(Wait), "No cancel arrives from a 1.3 client.");
            Assert.AreEqual("still here", await V13.RequestAsync(client, "Echo", "still here"));

            await V13.CloseAsync(client);
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task OldPipeClient_ReceivesNewPipeServerInitialMessage()
        {
            var pipeName = V14FeatureTests.NewPipeName();
            var server = new PipeServer { InitialMessage = new EventMessage("Welcome", "v14") };
            await server.StartAsync(pipeName);

            var client = V13.NewClient(TransportType.Pipe);
            var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            V13.On(client, "MessageReceived", e => got.TrySetResult((string)e.Message.EventName + "=" + (string)e.Message.GetPayloadAsString()));
            await V13.ConnectAsync(client, TransportType.Pipe, pipeName);

            Assert.AreEqual("Welcome=v14", await got.Task.WithTimeout(Wait));
            await V13.CloseAsync(client);
            await server.StopAsync();
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task OldFacadeConnection_NewFacadeServer()
        {
            var port = V14FeatureTests.GetFreePort();
            using var server = new Server { TransportType = TransportType.Tcp, KeepAlive = TimeSpan.FromSeconds(10) };
            server.OnRequest("Echo", msg => Task.FromResult<object?>(msg.GetPayloadAsString()));
            await server.StartAsync(port.ToString());

            dynamic client = V13.New("HRpc.Core.Connection");
            client.TransportType = (dynamic)V13.TransportType(TransportType.Tcp);
            await (Task)client.ConnectAsync($"127.0.0.1:{port}", CancellationToken.None);
            Assert.AreEqual("via facade", await V13.RequestAsync((object)client, "Echo", "via facade"));

            await (Task)client.CloseAsync();
            await server.StopAsync();
        }

        // ---- 1.4 client -> 1.3 server ----------------------------------------------------

        [TestMethod]
        [Timeout(15000)]
        public Task NewClient_OldTcpServer() => NewClientAgainstOldServer(TransportType.Tcp);

        [TestMethod]
        [Timeout(15000)]
        public Task NewClient_OldPipeServer() => NewClientAgainstOldServer(TransportType.Pipe);

        private static async Task NewClientAgainstOldServer(TransportType transport)
        {
            var target = transport == TransportType.Tcp ? V14FeatureTests.GetFreePort().ToString() : V14FeatureTests.NewPipeName();
            var server = V13.NewServer(transport);
            var serverEvents = new ConcurrentQueue<string>();
            var serverErrors = new ConcurrentQueue<string>();
            V13.On(server, "MessageReceived", e => serverEvents.Enqueue((string)e.Message.EventName));
            V13.On(server, "ErrorOccurred", e => serverErrors.Enqueue((string)e.Message));
            V13.OnRequest(server, "Echo", msg => Task.FromResult<object?>((string)msg.GetPayloadAsString()));
            V13.OnRequest(server, "Fail", msg => throw new InvalidOperationException("nope"));
            var slowGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            V13.OnRequest(server, "Slow", async msg => { await slowGate.Task; return "late"; });
            await V13.StartAsync(server, transport, target);

            using var client = new Connection { TransportType = transport };
            var clientEvents = new ConcurrentQueue<string>();
            var clientErrors = new ConcurrentQueue<string>();
            client.MessageReceived += (_, e) => clientEvents.Enqueue(e.Message.EventName);
            client.ErrorOccurred += (_, e) => clientErrors.Enqueue(e.Message);
            await client.ConnectAsync(transport == TransportType.Tcp ? "127.0.0.1:" + target : target);

            // The first request carries "caps"; a 1.3 server must ignore the unknown field.
            Assert.AreEqual("first", (await client.SendAndWaitAsync(new EventMessage("Echo", "first"), Wait)).GetPayloadAsString());
            var many = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => client.SendAndWaitAsync(new EventMessage("Echo", "n" + i), Wait)));
            CollectionAssert.AreEqual(Enumerable.Range(0, 10).Select(i => "n" + i).ToArray(), many.Select(m => m.GetPayloadAsString()).ToArray());

            var failure = await Assert.ThrowsExceptionAsync<RequestFailedException>(
                () => client.SendAndWaitAsync(new EventMessage("Fail", ""), Wait));
            Assert.AreEqual(nameof(InvalidOperationException), failure.Code);

            // Cancellation and timeout against a 1.3 server: the caller gets the usual
            // exceptions, nothing is sent (1.3 would surface "cancel" as a stray event), and the
            // eventual late reply is dropped instead of leaking into MessageReceived.
            using (var cts = new CancellationTokenSource(150))
            {
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                    () => client.SendAndWaitAsync(new EventMessage("Slow", ""), TimeSpan.FromSeconds(30), cts.Token));
            }

            await Assert.ThrowsExceptionAsync<TimeoutException>(
                () => client.SendAndWaitAsync(new EventMessage("Slow", ""), TimeSpan.FromMilliseconds(150)));
            slowGate.TrySetResult(true);
            await Task.Delay(300);

            await client.SendAsync(new EventMessage("Note", "x"));
            await V14FeatureTests.WaitUntil(() => serverEvents.Count >= 1);
            await Task.Delay(200);

            CollectionAssert.AreEqual(new[] { "Note" }, serverEvents.ToArray(), "Only the real event may reach the 1.3 server.");
            Assert.AreEqual(0, clientEvents.Count, "Late replies must not surface as events: " + string.Join(",", clientEvents));
            Assert.AreEqual(0, serverErrors.Count, string.Join("; ", serverErrors));
            Assert.AreEqual(0, clientErrors.Count, string.Join("; ", clientErrors));

            // The connection is still healthy afterwards.
            Assert.AreEqual("after", (await client.SendAndWaitAsync(new EventMessage("Echo", "after"), Wait)).GetPayloadAsString());

            await client.CloseAsync();
            await V13.StopAsync(server);
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task NewKeepAliveClient_OldServer()
        {
            // Keepalive is a socket option, not a message, so a 1.3 server is unaffected.
            var port = V14FeatureTests.GetFreePort();
            var server = V13.NewServer(TransportType.Tcp);
            V13.OnRequest(server, "Echo", msg => Task.FromResult<object?>((string)msg.GetPayloadAsString()));
            await V13.StartAsync(server, TransportType.Tcp, port.ToString());

            var client = new TcpClientWrapper { KeepAlive = TimeSpan.FromSeconds(5) };
            await client.ConnectAsync("127.0.0.1", port);
            Assert.AreEqual("ka", (await client.SendAndWaitAsync(new EventMessage("Echo", "ka"), Wait)).GetPayloadAsString());

            await client.CloseAsync();
            await V13.StopAsync(server);
        }

        [TestMethod]
        [Timeout(15000)]
        public async Task OldServerPush_ToNewClient_StillArrivesAsEvent()
        {
            // 1.3 PipeServer's InitialMessage is its only push; a 1.4 client must still raise it.
            var pipeName = V14FeatureTests.NewPipeName();
            dynamic server = V13.New("HRpc.Core.PipeServer");
            server.InitialMessage = (dynamic)V13.EventMessage("Welcome", "v13");
            await (Task)server.StartAsync(pipeName, CancellationToken.None);

            var client = new PipeClientWrapper();
            var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.MessageReceived += (_, e) => got.TrySetResult(e.Message.EventName + "=" + e.Message.GetPayloadAsString());
            await client.ConnectAsync(pipeName);

            Assert.AreEqual("Welcome=v13", await got.Task.WithTimeout(Wait));
            await client.CloseAsync();
            await (Task)server.StopAsync();
        }

        // ---- the 1.3.2 assembly, driven by reflection --------------------------------------

        /// <summary>
        /// Thin, reflection-based driver for the 1.3.2 types. The old types live in another load
        /// context, so they are distinct from this build's types even though the names match.
        /// </summary>
        private static class V13
        {
            private const string CompatVersion = "1.3.2";

            public static readonly Assembly Assembly = Load();

            private static Assembly Load()
            {
                var path = Path.Combine(AppContext.BaseDirectory, "compat", "v" + CompatVersion, "HRpc.dll");
                Assert.IsTrue(File.Exists(path), $"Missing {path}; it is copied from the HRpc {CompatVersion} NuGet package at build time.");
                return new IsolatedContext().LoadFromAssemblyPath(path);
            }

            private sealed class IsolatedContext : AssemblyLoadContext
            {
                public IsolatedContext() : base("HRpc-" + CompatVersion) { }

                // Only HRpc itself is isolated; System.Text.Json and the rest come from the
                // default context.
                protected override Assembly? Load(AssemblyName assemblyName) => null;
            }

            public static object New(string typeName, params object?[] args)
                => Activator.CreateInstance(Assembly.GetType(typeName, throwOnError: true)!, args)!;

            public static object EventMessage(string name, string payload) => New("HRpc.Models.EventMessage", name, payload);

            public static object TransportType(TransportType value)
                => Enum.Parse(Assembly.GetType("HRpc.TransportType", throwOnError: true)!, value.ToString());

            public static object NewClient(TransportType transport)
                => New(transport == HRpc.TransportType.Tcp ? "HRpc.Core.TcpClientWrapper" : "HRpc.Core.PipeClientWrapper");

            public static object NewServer(TransportType transport)
                => New(transport == HRpc.TransportType.Tcp ? "HRpc.Core.TcpServer" : "HRpc.Core.PipeServer");

            public static Task ConnectAsync(object client, TransportType transport, string target)
            {
                dynamic c = client;
                return transport == HRpc.TransportType.Tcp
                    ? (Task)c.ConnectAsync("127.0.0.1", int.Parse(target), CancellationToken.None)
                    : (Task)c.ConnectAsync(target, CancellationToken.None);
            }

            public static Task StartAsync(object server, TransportType transport, string target)
            {
                dynamic s = server;
                return transport == HRpc.TransportType.Tcp
                    ? (Task)s.StartAsync(int.Parse(target), CancellationToken.None)
                    : (Task)s.StartAsync(target, CancellationToken.None);
            }

            public static Task StopAsync(object server) => (Task)((dynamic)server).StopAsync();

            public static Task CloseAsync(object client) => (Task)((dynamic)client).CloseAsync();

            public static Task SendAsync(object client, string name, string payload)
                => (Task)((dynamic)client).SendAsync((dynamic)EventMessage(name, payload));

            public static async Task<string> RequestAsync(object client, string name, string payload, TimeSpan? timeout = null)
            {
                var reply = await RequestCoreAsync(client, name, payload, timeout);
                return (string)reply.GetPayloadAsString();
            }

            public static async Task<string> RequestRawAsync(object client, string name)
            {
                var reply = await RequestCoreAsync(client, name, "", null);
                return reply.PayloadValue.GetRawText();
            }

            private static async Task<dynamic> RequestCoreAsync(object client, string name, string payload, TimeSpan? timeout)
            {
                Task task = ((dynamic)client).SendAndWaitAsync((dynamic)EventMessage(name, payload), timeout ?? Wait, CancellationToken.None);
                await task;
                return ((dynamic)task).Result;
            }

            public static async Task<Exception> CatchAsync(Task task)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {
                    return ex;
                }

                Assert.Fail("Expected the call to fail.");
                return null!;
            }

            /// <summary>Subscribes to a 1.3 event; the callback gets the old args as dynamic.</summary>
            public static void On(object target, string eventName, Action<dynamic> callback)
            {
                var evt = target.GetType().GetEvent(eventName)!;
                var sink = new EventSink(callback);
                var handler = Delegate.CreateDelegate(evt.EventHandlerType!, sink,
                    typeof(EventSink).GetMethod(nameof(EventSink.Handle))!);
                evt.AddEventHandler(target, handler);
            }

            /// <summary>Registers a 1.3 request handler; the callback gets the old message as dynamic.</summary>
            public static void OnRequest(object server, string eventName, Func<dynamic, Task<object?>> handler)
            {
                var method = server.GetType().GetMethods()
                    .Single(m => m.Name == "OnRequest" && m.GetParameters().Length == 2);
                var sink = new RequestSink(handler);
                var del = Delegate.CreateDelegate(method.GetParameters()[1].ParameterType, sink,
                    typeof(RequestSink).GetMethod(nameof(RequestSink.Handle))!);
                method.Invoke(server, new object[] { eventName, del });
            }

            private sealed class EventSink
            {
                private readonly Action<dynamic> _callback;

                public EventSink(Action<dynamic> callback) => _callback = callback;

                // Bound to EventHandler<OldArgs> through parameter contravariance.
                public void Handle(object? sender, EventArgs e) => _callback(e);
            }

            private sealed class RequestSink
            {
                private readonly Func<dynamic, Task<object?>> _handler;

                public RequestSink(Func<dynamic, Task<object?>> handler) => _handler = handler;

                // Bound to Func<OldIEventMessage, Task<object?>> through parameter contravariance.
                public Task<object?> Handle(object message) => _handler(message);
            }
        }
    }
}
#endif

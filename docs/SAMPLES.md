# HRpc samples

Each sample below is a complete program: paste it into the `Program.cs` of a new console app
(`dotnet new console`, then `dotnet add package HRpc`) and run it. Server and client run in the
same process so every sample works on its own; in a real application they are separate
processes, usually on the same machine (pipes) or across a network (TCP).

The samples need HRpc 1.4 or later, except where noted.

| Sample | Shows |
| --- | --- |
| [Typed request/response](#typed-requestresponse) | Request and reply contracts as records, plus a small typed `CallAsync` helper |
| [Errors from handlers](#errors-from-handlers) | Turning server exceptions into client-side errors you can branch on |
| [A desktop app and its background service](#a-desktop-app-and-its-background-service) | Named pipes restricted to the current user |
| [Progress updates during a long request](#progress-updates-during-a-long-request) | Pushing to the calling client while its request runs |
| [Cancelling long-running work](#cancelling-long-running-work) | A cancel button that stops the server's work, with requests running in parallel |
| [Chat room](#chat-room) | Per-client state, broadcast, join and leave |
| [Reconnecting client](#reconnecting-client) | Detecting a lost server and reconnecting with backoff |
| [Graceful shutdown](#graceful-shutdown) | Telling clients the server is going away before stopping |
| [Hosting a server in a worker service](#hosting-a-server-in-a-worker-service) | Running a server inside the .NET Generic Host |
| [Testing your handlers](#testing-your-handlers) | An in-process server in a unit test |

## Typed request/response

Define the contract as records shared by both sides, and wrap `SendAndWaitAsync` once so call
sites read like method calls.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
server.OnRequest("GetQuote", async msg =>
{
    var request = msg.GetPayload<QuoteRequest>()!;
    await Task.Delay(10); // look the price up somewhere
    return new QuoteReply(request.Symbol, 101.25m, DateTimeOffset.UtcNow);
});
await server.StartAsync("9100");

var client = new Connection { TransportType = TransportType.Tcp };
await client.ConnectAsync("127.0.0.1:9100");

QuoteReply quote = await client.CallAsync<QuoteReply>("GetQuote", new QuoteRequest("ACME"));
Console.WriteLine($"{quote.Symbol}: {quote.Price} at {quote.At:T}");

await client.CloseAsync();
await server.StopAsync();

public record QuoteRequest(string Symbol);
public record QuoteReply(string Symbol, decimal Price, DateTimeOffset At);

public static class ConnectionExtensions
{
    public static async Task<T> CallAsync<T>(this Connection connection, string name, object? request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var reply = await connection.SendAndWaitAsync(new EventMessage(name, request),
            timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        return reply.GetPayload<T>()
            ?? throw new InvalidOperationException($"'{name}' returned an empty reply.");
    }
}
```

## Errors from handlers

When a handler throws, the client's `SendAndWaitAsync` throws `RequestFailedException`. Its
`Code` is the exception's type name and its `Message` is the exception's message, so a
dedicated exception type per failure gives the client something to branch on. Only the type
name and message cross the wire, never the stack trace.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
server.OnRequest("Withdraw", msg =>
{
    var amount = msg.GetPayload<decimal>();
    if (amount > 100m)
    {
        throw new InsufficientFundsException($"Balance is 100, cannot withdraw {amount}.");
    }

    return Task.FromResult<object?>(100m - amount);
});
await server.StartAsync("9101");

var client = new Connection { TransportType = TransportType.Tcp };
await client.ConnectAsync("127.0.0.1:9101");

foreach (var amount in new[] { 40m, 250m })
{
    try
    {
        var reply = await client.SendAndWaitAsync(new EventMessage("Withdraw", amount), TimeSpan.FromSeconds(5));
        Console.WriteLine($"Withdrew {amount}, balance {reply.GetPayload<decimal>()}");
    }
    catch (RequestFailedException ex) when (ex.Code == nameof(InsufficientFundsException))
    {
        Console.WriteLine($"Declined: {ex.Message}");
    }
    catch (TimeoutException)
    {
        Console.WriteLine("The server did not answer in time.");
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine("The connection dropped while waiting.");
    }
}

await client.CloseAsync();
await server.StopAsync();

public class InsufficientFundsException : Exception
{
    public InsufficientFundsException(string message) : base(message) { }
}
```

## A desktop app and its background service

A UI and a service running on the same machine for the same user talk over a named pipe.
`CurrentUserOnly` on both sides keeps other users' processes from connecting to the service,
and keeps the app from talking to an impostor server started by another user.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

const string PipeName = "contoso-sync-service";

// The background service.
var service = new Server { TransportType = TransportType.Pipe, CurrentUserOnly = true };
var paused = false;
service.OnRequest("GetStatus", _ => Task.FromResult<object?>(new SyncStatus(paused, FilesPending: 3)));
service.OnRequest("SetPaused", msg =>
{
    paused = msg.GetPayload<bool>();
    return Task.FromResult<object?>(null);
});
await service.StartAsync(PipeName);

// The desktop app.
var app = new Connection { TransportType = TransportType.Pipe, CurrentUserOnly = true };
await app.ConnectAsync(PipeName);

await app.SendAndWaitAsync(new EventMessage("SetPaused", true), TimeSpan.FromSeconds(5));
var status = (await app.SendAndWaitAsync(new EventMessage("GetStatus", ""), TimeSpan.FromSeconds(5)))
    .GetPayload<SyncStatus>()!;
Console.WriteLine($"Paused: {status.Paused}, files pending: {status.FilesPending}");

await app.CloseAsync();
await service.StopAsync();

public record SyncStatus(bool Paused, int FilesPending);
```

## Progress updates during a long request

A handler knows which client is calling (`ctx.ClientId`), so it can push progress events to
that client while it works and still return one final reply. Progress arrives on the client
as ordinary `MessageReceived` events, before the reply.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
server.OnRequest("Import", async (msg, ctx) =>
{
    var job = msg.GetPayload<ImportJob>()!;
    for (var done = 1; done <= job.Files; done++)
    {
        await Task.Delay(50, ctx.CancellationToken); // import one file
        await server.SendAsync(ctx.ClientId, new EventMessage("ImportProgress", new Progress(job.Id, done, job.Files)));
    }

    return $"Imported {job.Files} files";
});
await server.StartAsync("9102");

var client = new Connection { TransportType = TransportType.Tcp };
client.MessageReceived += (_, e) =>
{
    if (e.Message.EventName == "ImportProgress")
    {
        var p = e.Message.GetPayload<Progress>()!;
        Console.WriteLine($"[{p.JobId}] {p.Done}/{p.Total}");
    }
};
await client.ConnectAsync("127.0.0.1:9102");

var result = await client.SendAndWaitAsync(new EventMessage("Import", new ImportJob("job-1", 5)), TimeSpan.FromSeconds(30));
Console.WriteLine(result.GetPayloadAsString());

await client.CloseAsync();
await server.StopAsync();

public record ImportJob(string Id, int Files);
public record Progress(string JobId, int Done, int Total);
```

## Cancelling long-running work

Pass a `CancellationToken` from your UI's cancel button to `SendAndWaitAsync`. The caller gets
`TaskCanceledException` at once, and a 1.4+ server trips the handler's
`ctx.CancellationToken`, so the work actually stops. `MaxConcurrentRequests` lets one client
run several requests side by side instead of one after another.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp, MaxConcurrentRequests = 4 };
server.OnRequest("Render", async (msg, ctx) =>
{
    var frames = msg.GetPayload<int>();
    try
    {
        for (var i = 0; i < frames; i++)
        {
            await Task.Delay(100, ctx.CancellationToken); // render one frame
        }

        return $"rendered {frames} frames";
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"server: render of {frames} frames stopped early");
        throw;
    }
});
await server.StartAsync("9103");

var client = new Connection { TransportType = TransportType.Tcp };
await client.ConnectAsync("127.0.0.1:9103");

using var cancelButton = new CancellationTokenSource();
var longJob = client.SendAndWaitAsync(new EventMessage("Render", 100), TimeSpan.FromMinutes(5), cancelButton.Token);
var shortJob = client.SendAndWaitAsync(new EventMessage("Render", 3), TimeSpan.FromMinutes(5));

// The short job finishes while the long one is still running.
Console.WriteLine($"client: {(await shortJob).GetPayloadAsString()}");

cancelButton.Cancel(); // the user clicks Cancel
try
{
    await longJob;
}
catch (TaskCanceledException)
{
    Console.WriteLine("client: cancelled");
}

await Task.Delay(200); // give the server's message a moment to print
await client.CloseAsync();
await server.StopAsync();
```

## Chat room

The server keeps per-client state keyed by `ClientId`, broadcasts to everyone, and cleans up
in `ClientDisconnected`. Clients just send events and listen.

```csharp
using System.Collections.Concurrent;
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
var names = new ConcurrentDictionary<string, string>();

server.OnRequest("Join", async (msg, ctx) =>
{
    var name = msg.GetPayloadAsString();
    names[ctx.ClientId] = name;
    await server.BroadcastAsync(new EventMessage("System", $"{name} joined"));
    return names.Values.ToArray(); // who is here
});

server.MessageReceived += async (_, e) =>
{
    if (e.Message.EventName == "Say" && names.TryGetValue(e.ClientId!, out var from))
    {
        await server.BroadcastAsync(new EventMessage("Said", new ChatLine(from, e.Message.GetPayloadAsString())));
    }
};

server.ClientDisconnected += async (_, e) =>
{
    if (names.TryRemove(e.ClientId!, out var name))
    {
        await server.BroadcastAsync(new EventMessage("System", $"{name} left"));
    }
};

await server.StartAsync("9104");

var alice = await JoinAsync("alice");
var bob = await JoinAsync("bob");

await alice.SendAsync(new EventMessage("Say", "hi bob"));
await Task.Delay(200);
await bob.CloseAsync();
await Task.Delay(200);

await alice.CloseAsync();
await server.StopAsync();

static async Task<Connection> JoinAsync(string name)
{
    var connection = new Connection { TransportType = TransportType.Tcp };
    connection.MessageReceived += (_, e) =>
    {
        var text = e.Message.EventName == "Said"
            ? $"{e.Message.GetPayload<ChatLine>()!.From}: {e.Message.GetPayload<ChatLine>()!.Text}"
            : $"* {e.Message.GetPayloadAsString()}";
        Console.WriteLine($"({name} sees) {text}");
    };
    await connection.ConnectAsync("127.0.0.1:9104");

    var here = await connection.SendAndWaitAsync(new EventMessage("Join", name), TimeSpan.FromSeconds(5));
    Console.WriteLine($"({name} sees) in the room: {string.Join(", ", here.GetPayload<string[]>()!)}");
    return connection;
}

public record ChatLine(string From, string Text);
```

## Reconnecting client

HRpc does not reconnect by itself. `Disconnected` tells you the connection is gone, and
`ConnectAsync` can be called again on the same `Connection` (its event subscriptions are
kept). This wrapper retries with exponential backoff until it is closed. Over TCP, set
`KeepAlive` too, so a server that vanished without closing the connection is noticed.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
server.OnRequest("Ping", _ => Task.FromResult<object?>("pong"));
await server.StartAsync("9105");

await using var client = new ReconnectingConnection(TransportType.Tcp, "127.0.0.1:9105");
client.Connection.Connected += (_, _) => Console.WriteLine("connected");
client.Connection.Disconnected += (_, _) => Console.WriteLine("disconnected");
await client.StartAsync();

await server.StopAsync(); // the server restarts...
await Task.Delay(500);
await server.StartAsync("9105");

await client.WaitUntilConnectedAsync(TimeSpan.FromSeconds(10));
var reply = await client.Connection.SendAndWaitAsync(new EventMessage("Ping", ""), TimeSpan.FromSeconds(5));
Console.WriteLine(reply.GetPayloadAsString());

await server.StopAsync();

public sealed class ReconnectingConnection : IAsyncDisposable
{
    private readonly string _target;
    private readonly CancellationTokenSource _closing = new();
    private int _reconnecting;

    public ReconnectingConnection(TransportType transport, string target)
    {
        _target = target;
        Connection = new Connection
        {
            TransportType = transport,
            KeepAlive = transport == TransportType.Tcp ? TimeSpan.FromSeconds(15) : null,
        };
        Connection.Disconnected += (_, _) => StartReconnecting();
    }

    public Connection Connection { get; }

    public Task StartAsync() => ReconnectLoopAsync();

    public async Task WaitUntilConnectedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!Connection.IsConnected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Not reconnected in time.");
            }

            await Task.Delay(50);
        }
    }

    private void StartReconnecting()
    {
        // Disconnected is raised on HRpc's receive path: reconnect elsewhere, and only once.
        if (!_closing.IsCancellationRequested && Interlocked.Exchange(ref _reconnecting, 1) == 0)
        {
            _ = Task.Run(ReconnectLoopAsync);
        }
    }

    private async Task ReconnectLoopAsync()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                try
                {
                    await Connection.ConnectAsync(_target, _closing.Token);
                    return;
                }
                catch (Exception) when (!_closing.IsCancellationRequested)
                {
                    await Task.Delay(delay, _closing.Token);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed while reconnecting.
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await Connection.CloseAsync();
        Connection.Dispose();
    }
}
```

## Graceful shutdown

Tell clients before stopping, give in-flight work a moment, then stop. `StopAsync` closes every
connection and trips the `CancellationToken` of any handler still running.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var server = new Server { TransportType = TransportType.Tcp };
await server.StartAsync("9106");

var client = new Connection { TransportType = TransportType.Tcp };
client.MessageReceived += (_, e) =>
{
    if (e.Message.EventName == "ServerShuttingDown")
    {
        Console.WriteLine($"client: server is going away ({e.Message.GetPayloadAsString()}), saving work");
    }
};
client.Disconnected += (_, _) => Console.WriteLine("client: disconnected");
await client.ConnectAsync("127.0.0.1:9106");

int told = await server.BroadcastAsync(new EventMessage("ServerShuttingDown", "maintenance"));
Console.WriteLine($"server: told {told} client(s)");
await Task.Delay(500); // let clients react
await server.StopAsync();

await Task.Delay(200);
client.Dispose();
```

## Hosting a server in a worker service

In a service or ASP.NET Core app, start the server from a `BackgroundService` so it starts and
stops with the host. Needs the `Microsoft.Extensions.Hosting` package
(`dotnet add package Microsoft.Extensions.Hosting`), or a `dotnet new worker` project.

```csharp
using HRpc;
using HRpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<Clock>();
builder.Services.AddHostedService<RpcHost>();
using var host = builder.Build();

// Run for a few seconds in this sample; a real service calls host.RunAsync().
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(2));
await host.RunAsync(lifetime.Token);

public sealed class Clock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public sealed class RpcHost : BackgroundService
{
    private readonly Server _server = new() { TransportType = TransportType.Pipe, CurrentUserOnly = true };

    public RpcHost(Clock clock, ILogger<RpcHost> logger)
    {
        _server.OnRequest("Now", _ => Task.FromResult<object?>(clock.Now));
        _server.ClientConnected += (_, e) => logger.LogInformation("Client {ClientId} connected", e.ClientId);
        _server.ErrorOccurred += (_, e) => logger.LogWarning(e.Ex, "HRpc: {Message}", e.Message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _server.StartAsync("my-worker-service", stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
        finally
        {
            await _server.StopAsync();
        }
    }

    public override void Dispose()
    {
        _server.Dispose();
        base.Dispose();
    }
}
```

## Testing your handlers

Start a real server in the test on a unique pipe name, so tests can run in parallel without
port clashes, and talk to it with a real client. This example uses MSTest; the same shape works
with xUnit or NUnit.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CalculatorServiceTests
{
    [TestMethod]
    public async Task Add_ReturnsTheSum()
    {
        var pipeName = "test-" + Guid.NewGuid().ToString("N");
        using var server = new Server { TransportType = TransportType.Pipe };
        CalculatorService.Register(server);
        await server.StartAsync(pipeName);

        using var client = new Connection { TransportType = TransportType.Pipe };
        await client.ConnectAsync(pipeName);

        var reply = await client.SendAndWaitAsync(new EventMessage("Add", new[] { 2, 3 }), TimeSpan.FromSeconds(5));

        Assert.AreEqual(5, reply.GetPayload<int>());
        await client.CloseAsync();
        await server.StopAsync();
    }
}

// The code under test: handlers registered in one place, used by the real app and the tests.
public static class CalculatorService
{
    public static void Register(Server server)
    {
        server.OnRequest("Add", msg =>
        {
            var numbers = msg.GetPayload<int[]>()!;
            return Task.FromResult<object?>(numbers.Sum());
        });
    }
}
```

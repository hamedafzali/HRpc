# HRpc

[![CI](https://github.com/hamedafzali/HRpc/actions/workflows/ci.yml/badge.svg)](https://github.com/hamedafzali/HRpc/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/HRpc.svg)](https://www.nuget.org/packages/HRpc/)

HRpc is a lightweight .NET library for event-driven TCP and Named Pipe messaging.

## Install

```bash
dotnet add package HRpc
```

## Supported Frameworks

- `net48`
- `net8.0`
- `net9.0`
- `net10.0`

## Message Format

HRpc exchanges newline-delimited JSON envelopes. `payload` may be any JSON value -- a string, number, object, array, or `null`:

```json
{ "v": 2, "eventName": "MyEvent", "payload": { "any": "JSON value" } }
```

## Constructing payloads: `EventMessage` has two constructors with different meanings

- `new EventMessage(name, string payload)` embeds the string **literally as a JSON string value**. It is never parsed or sniffed, even if it looks like JSON -- use it only when the payload genuinely is text (e.g. `"Ping"`).
- `new EventMessage(name, object? payload)` serializes the argument by its runtime type and embeds it as a **nested JSON value** -- use this for a POCO, an anonymous object, a primitive, or `null`.

```csharp
new EventMessage("Greeting", "Hello");                 // payload: "Hello"        (string)
new EventMessage("Order", new { id = 1, qty = 2 });     // payload: {"id":1,"qty":2} (object)
```

If you already hold pre-serialized JSON text and want it embedded as a nested value (not re-escaped as a string), use `EventMessage.FromJson(name, json)` instead of either constructor:

```csharp
var json = "{\"id\":1,\"qty\":2}"; // from another system, already serialized
var msg = EventMessage.FromJson("Order", json); // payload: {"id":1,"qty":2}, not "{\"id\":1,\"qty\":2}"
```

Read a payload back with `msg.GetPayload<T>()` (typed, throws `JsonException`/`FormatException` on a shape mismatch), `msg.TryGetPayload<T>(out var value)` (non-throwing), or `msg.GetPayloadAsString()` (never throws -- returns the string content if the payload is a JSON string, otherwise the raw JSON text of whatever value is present).

## API Overview

HRpc provides unified `Connection` and `Server` classes for both TCP and Named Pipe transports. Select the transport type using the `TransportType` property.

### Transport Types

- `TransportType.Tcp`: TCP/IP networking
- `TransportType.Pipe`: Local Named Pipes (same machine)

## Quick Start (Client)

### TCP Client

```csharp
using HRpc;
using HRpc.Core;

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

await connection.ConnectAsync("127.0.0.1:9000");

connection.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await connection.SendAsync(new EventMessage("Hello", "World"));
await connection.CloseAsync();
```

### Named Pipe Client

```csharp
using HRpc;
using HRpc.Core;

var connection = new Connection();
connection.TransportType = TransportType.Pipe;

await connection.ConnectAsync("my-hrpc-pipe");

connection.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await connection.SendAsync(new EventMessage("Hello", "World"));
await connection.CloseAsync();
```

## Quick Start (Server)

### TCP Server

```csharp
using HRpc;
using HRpc.Core;

var server = new Server();
server.TransportType = TransportType.Tcp;

server.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await server.StartAsync("9000");
```

### Named Pipe Server

```csharp
using HRpc;
using HRpc.Core;

var server = new Server();
server.TransportType = TransportType.Pipe;

server.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await server.StartAsync("my-hrpc-pipe");
```

## Advanced Usage

### Structured (typed) payloads

Send a POCO or anonymous object directly -- it is serialized and embedded as a nested JSON value, not a re-escaped string:

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

public record OrderCreated(int OrderId, decimal Total);

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

connection.MessageReceived += (sender, e) =>
{
    if (e.Message.EventName == "OrderCreated")
    {
        var order = e.Message.GetPayload<OrderCreated>();
        Console.WriteLine($"Order {order?.OrderId}: {order?.Total}");
    }
};

await connection.ConnectAsync("127.0.0.1:9000");
await connection.SendAsync(new EventMessage("OrderCreated", new OrderCreated(1, 42.50m)));
await connection.CloseAsync();
```

### Request/response (`OnRequest` + `SendAndWaitAsync`)

For a correlated call-and-reply pattern -- send a request, get exactly one matching reply back -- register a handler on the server with `OnRequest` and call `SendAndWaitAsync` on the client. HRpc generates and matches the correlation id (`cid`) for you; see [PROTOCOL.md](https://github.com/hamedafzali/HRpc/blob/main/docs/PROTOCOL.md#requestresponse-v130) for the full wire semantics.

```csharp
using HRpc;
using HRpc.Core;

// Server
var server = new Server { TransportType = TransportType.Tcp };
server.OnRequest("Add", async msg =>
{
    var numbers = msg.GetPayload<int[]>()!;
    return numbers[0] + numbers[1];
});
await server.StartAsync("9000");

// Client
var client = new Connection { TransportType = TransportType.Tcp };
await client.ConnectAsync("127.0.0.1:9000");

var response = await client.SendAndWaitAsync(new EventMessage("Add", new[] { 2, 3 }), TimeSpan.FromSeconds(5));
Console.WriteLine(response.GetPayload<int>()); // 5

await client.CloseAsync();
```

If the handler throws, `SendAndWaitAsync` throws `RequestFailedException` (with `Code`/`Message` from the handler's exception) on the client. If no matching response arrives within the timeout, it throws `TimeoutException`. If the connection drops while a call is pending, it throws `InvalidOperationException`. `OnRequest` can be called before or after `StartAsync`. An `eventName` with no registered handler falls through to `MessageReceived` as usual -- this is purely additive to the existing pub/sub API.

### Cancelling requests

Pass a `CancellationToken` to `SendAndWaitAsync`, or let the timeout expire. Either way, a 1.4+ server cancels the handler's work. To observe that, register the handler with the two-parameter overload, which also tells you which client is calling:

```csharp
server.OnRequest("Report", async (msg, ctx) =>
{
    // ctx.CancellationToken trips if the caller cancels or times out, disconnects, or the server stops.
    var report = await BuildReportAsync(msg.GetPayload<ReportQuery>()!, ctx.CancellationToken);
    return report;
});

using var cts = new CancellationTokenSource();
var pending = client.SendAndWaitAsync(new EventMessage("Report", query), TimeSpan.FromSeconds(30), cts.Token);
cts.Cancel(); // the caller gets TaskCanceledException, and the server stops BuildReportAsync
```

A cancelled request gets no reply. A reply that still arrives after a timeout or cancellation is discarded rather than raised as `MessageReceived`. Against a 1.3 server, cancellation only affects the caller, exactly as before: nothing extra is sent.

### Server push

A server can send events to one client or to all of them. Every connected client has an id, available as `ClientId` on `ClientConnected`, `ClientDisconnected`, server-side `MessageReceived`, and the request context:

```csharp
server.ClientConnected += (_, e) => Console.WriteLine($"client {e.ClientId} connected");
server.MessageReceived += (_, e) => Console.WriteLine($"{e.ClientId} sent {e.Message.EventName}");

await server.SendAsync(clientId, new EventMessage("PriceChanged", price)); // one client
int reached = await server.BroadcastAsync(new EventMessage("Shutdown", "in 5 min")); // all clients
IReadOnlyCollection<string> ids = server.ConnectedClients;
```

Pushed messages arrive on the client as ordinary `MessageReceived` events, so clients on any version, including 1.3, receive them. `SendAsync` throws `InvalidOperationException` if the client is unknown or has disconnected. `BroadcastAsync` skips clients that fail and returns how many it reached.

### Concurrent requests

By default a server handles each client's messages one at a time, in order, as in 1.3. To let one client's slow requests run side by side, raise `MaxConcurrentRequests`:

```csharp
var server = new Server { TransportType = TransportType.Tcp, MaxConcurrentRequests = 8 };
```

It is a per-client limit. Events are still delivered in order but no longer wait for running handlers, and replies come back as handlers finish. `SendAndWaitAsync` matches them by id, so callers are unaffected.

### Detecting dead peers and restricting pipes

```csharp
// TCP keepalive: detect a peer that vanished without closing (cable pulled, VM paused).
var client = new Connection { TransportType = TransportType.Tcp, KeepAlive = TimeSpan.FromSeconds(30) };
var server = new Server { TransportType = TransportType.Tcp, KeepAlive = TimeSpan.FromSeconds(30) };

// Named pipes: only processes running as the current user may connect (server),
// and only a server running as the current user is accepted (client).
var pipeServer = new Server { TransportType = TransportType.Pipe, CurrentUserOnly = true };
var pipeClient = new Connection { TransportType = TransportType.Pipe, CurrentUserOnly = true };
```

Both are off by default and change nothing on the wire, so they work with peers on any version. With `KeepAlive` set, a dead peer is detected about that long plus a few seconds after traffic stops, and the connection then closes and raises `Disconnected` or `ClientDisconnected`.

### EventDispatcher for pub/sub patterns

Use `EventDispatcher` to subscribe to specific events and emit messages.

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var dispatcher = new EventDispatcher();

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

await connection.ConnectAsync("127.0.0.1:9000");

// Subscribe to "Ping" events
using var subscription = dispatcher.Subscribe(connection, "Ping", msg =>
{
    Console.WriteLine($"Ping received: {msg.GetPayloadAsString()}");

    // Respond with "Pong"
    Task.Run(() => dispatcher.Emit(connection, new EventMessage("Pong", "Response")));
});

// Send a ping
await dispatcher.Emit(connection, new EventMessage("Ping", "Hello"));

await connection.CloseAsync();
```

### Error Handling

Handle connection errors and invalid messages.

```csharp
using HRpc;
using HRpc.Core;

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

connection.Connected += (sender, e) => Console.WriteLine("Connected!");
connection.Disconnected += (sender, e) => Console.WriteLine("Disconnected!");
connection.ErrorOccurred += (sender, e) => Console.WriteLine($"Error: {e.Message}");

try
{
    await connection.ConnectAsync("127.0.0.1:9000");
    // Use connection...
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
}
finally
{
    await connection.CloseAsync();
}
```

### Full Client-Server Example

#### Server

```csharp
using HRpc;
using HRpc.Core;

var server = new Server();
server.TransportType = TransportType.Tcp;

server.MessageReceived += (sender, e) =>
{
    // One-way events land here. To reply to a client, register an OnRequest handler
    // and have the client call SendAndWaitAsync (see "Request/response" above).
    Console.WriteLine($"Server received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await server.StartAsync("9000");
Console.WriteLine("Server started on port 9000");
```

#### Client

```csharp
using HRpc;
using HRpc.Core;
using HRpc.Models;

var client = new Connection();
client.TransportType = TransportType.Tcp;

client.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Client received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");
};

await client.ConnectAsync("127.0.0.1:9000");
Console.WriteLine("Client connected");

await client.SendAsync(new EventMessage("Test", "Hello from client!"));

await client.CloseAsync();
```

## Behavior Notes

- `ConnectAsync(...)` throws on failure and also raises `ErrorOccurred`.
- A malformed message raises `ErrorOccurred` and is skipped; the connection stays up.
- A message larger than `MaxMessageSizeBytes` (default 1 MiB) is fatal: `ErrorOccurred` fires with `LineTooLongException` and the connection is dropped.
- A throwing `MessageReceived` subscriber doesn't disconnect the connection or block other subscribers.
- `IsConnected` reflects tracked connection state, not a raw socket check.
- A server handles each client's messages in arrival order. With the default `MaxConcurrentRequests = 1`, it finishes each request handler before reading on.
- A 1.4 client and a 1.3 server, or the reverse, work together. New features that need both sides (request cancellation) turn themselves off against a 1.3 peer.
- `EventDispatcher.Subscribe(...)` returns `IDisposable`; dispose it to unsubscribe.
- There is no built-in authentication or encryption. For internet-facing use, run behind TLS and an authentication boundary.

## Documentation

| Document | Contents |
|----------|----------|
| [Protocol](https://github.com/hamedafzali/HRpc/blob/main/docs/PROTOCOL.md) | Wire format, versioning, request/response semantics, error-handling rules |
| [Changelog](https://github.com/hamedafzali/HRpc/blob/main/CHANGELOG.md) | Every release, with migration notes |
| [Roadmap](https://github.com/hamedafzali/HRpc/blob/main/docs/ROADMAP.md) | Planned work, cross-platform notes, and how HRpc compares to gRPC, StreamJsonRpc and H.Pipes |
| [Releasing](https://github.com/hamedafzali/HRpc/blob/main/docs/RELEASING.md) | How versions are published to NuGet (maintainers) |

Source: [github.com/hamedafzali/HRpc](https://github.com/hamedafzali/HRpc) · License: MIT

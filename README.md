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

### EventDispatcher for Request-Response Patterns

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

server.MessageReceived += async (sender, e) =>
{
    Console.WriteLine($"Server received: {e.Message.EventName} - {e.Message.GetPayloadAsString()}");

    // Echo back
    if (sender is Server srv)
    {
        // Note: Server doesn't have SendAsync; messages are handled via events
        // For response, you might need to use a separate connection or custom logic
    }
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

await Task.Delay(1000); // Wait for response

await client.CloseAsync();
```

## Error Handling

- `ConnectAsync(...)` throws on failure and also raises `ErrorOccurred`.
- Invalid message payloads raise `ErrorOccurred`.
- `EventDispatcher.Subscribe(...)` returns `IDisposable`; dispose it to unsubscribe.

## Links

- Source: [https://github.com/hamedafzali/HRpc](https://github.com/hamedafzali/HRpc)
- License: MIT

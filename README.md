# HRpc

HRpc is a lightweight .NET library for event-driven TCP and Named Pipe messaging.

## Install

```bash
dotnet add package HRpc
```

## Supported Frameworks

- `net48`
- `net6.0`
- `net7.0`
- `net9.0`

## Message Format

HRpc exchanges newline-delimited JSON envelopes:

```json
{ "eventName": "MyEvent", "payload": "Any string payload" }
```

## API Overview

HRpc provides unified `Connection` and `Server` classes for both TCP and Named Pipe transports. Select the transport type using the `TransportType` property.

### Transport Types

- `TransportType.Tcp`: TCP/IP networking
- `TransportType.Pipe`: Local Named Pipes (same machine)

## Quick Start (Client)

### TCP Client

```csharp
using TcpEventFramework.Core;

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

await connection.ConnectAsync("127.0.0.1:9000");

connection.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.Payload}");
};

await connection.SendAsync(new EventMessage("Hello", "World"));
await connection.CloseAsync();
```

### Named Pipe Client

```csharp
using TcpEventFramework.Core;

var connection = new Connection();
connection.TransportType = TransportType.Pipe;

await connection.ConnectAsync("my-hrpc-pipe");

connection.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.Payload}");
};

await connection.SendAsync(new EventMessage("Hello", "World"));
await connection.CloseAsync();
```

## Quick Start (Server)

### TCP Server

```csharp
using TcpEventFramework.Core;

var server = new Server();
server.TransportType = TransportType.Tcp;

server.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.Payload}");
};

await server.StartAsync("9000");
```

### Named Pipe Server

```csharp
using TcpEventFramework.Core;

var server = new Server();
server.TransportType = TransportType.Pipe;

server.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Received: {e.Message.EventName} - {e.Message.Payload}");
};

await server.StartAsync("my-hrpc-pipe");
```

## Advanced Usage

### EventDispatcher for Request-Response Patterns

Use `EventDispatcher` to subscribe to specific events and emit messages.

```csharp
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var dispatcher = new EventDispatcher();

var connection = new Connection();
connection.TransportType = TransportType.Tcp;

await connection.ConnectAsync("127.0.0.1:9000");

// Subscribe to "Ping" events
using var subscription = dispatcher.Subscribe(connection, "Ping", msg =>
{
    Console.WriteLine($"Ping received: {msg.Payload}");

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
using TcpEventFramework.Core;

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
using TcpEventFramework.Core;

var server = new Server();
server.TransportType = TransportType.Tcp;

server.MessageReceived += async (sender, e) =>
{
    Console.WriteLine($"Server received: {e.Message.EventName} - {e.Message.Payload}");

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
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var client = new Connection();
client.TransportType = TransportType.Tcp;

client.MessageReceived += (sender, e) =>
{
    Console.WriteLine($"Client received: {e.Message.EventName} - {e.Message.Payload}");
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

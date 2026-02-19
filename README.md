# HRpc

HRpc is a lightweight .NET library for TCP-based, event-driven messaging.

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

## Quick Start (Client)

```csharp
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var dispatcher = new EventDispatcher();
using var connection = new TcpClientWrapper();

await connection.ConnectAsync("127.0.0.1", 9000);

using var subscription = dispatcher.Subscribe(connection, "Greeting", msg =>
{
    Console.WriteLine($"Received: {msg.Payload}");
});

await dispatcher.Emit(connection, new EventMessage("Greeting", "Hello"));
await connection.CloseAsync();
```

## Quick Start (Local Named Pipe Server)

```csharp
using TcpEventFramework.Core;

var server = new PipeServer();
server.MessageReceived += (_, e) =>
{
    Console.WriteLine($"{e.Message.EventName}: {e.Message.Payload}");
};

await server.StartAsync("my-hrpc-pipe");
```

## Quick Start (Local Named Pipe Client)

```csharp
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var dispatcher = new EventDispatcher();
using var connection = new PipeClientWrapper();

await connection.ConnectAsync("my-hrpc-pipe");

using var subscription = dispatcher.Subscribe(connection, "Greeting", msg =>
{
    Console.WriteLine($"Received: {msg.Payload}");
});

await dispatcher.Emit(connection, new EventMessage("Greeting", "Hello"));
await connection.CloseAsync();
```

## Quick Start (Server)

```csharp
using TcpEventFramework.Core;

var server = new TcpServer();
server.MessageReceived += (_, e) =>
{
    Console.WriteLine($"{e.Message.EventName}: {e.Message.Payload}");
};

await server.StartAsync(9000);
```

## Error Handling

- `ConnectAsync(...)` throws on failure and also raises `ErrorOccurred`.
- Invalid message payloads raise `ErrorOccurred`.
- `EventDispatcher.Subscribe(...)` returns `IDisposable`; dispose it to unsubscribe.

## Links

- Source: [https://github.com/hamedafzali/HRpc](https://github.com/hamedafzali/HRpc)
- License: MIT

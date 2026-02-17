# HRpc

HRpc is a lightweight C# library for TCP-based, event-driven messaging.

## Features

- Event-driven messaging over TCP
- JSON message envelope (`eventName`, `payload`)
- Client connection abstraction (`ITcpConnection`)
- TCP server with client/message events (`ITcpServer`)

## Prerequisites

- .NET SDK 9.0+

## Build and Test

```bash
dotnet build /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln
dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln
```

## Quick Start (Client)

```csharp
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var dispatcher = new EventDispatcher();
using var connection = new TcpClientWrapper();

await connection.ConnectAsync("127.0.0.1", 9000);

using var subscription = dispatcher.Subscribe(connection, "TestEvent", msg =>
{
    Console.WriteLine($"Received: {msg.Payload}");
});

await dispatcher.Emit(connection, new EventMessage("TestEvent", "Hello World"));
await connection.CloseAsync();
```

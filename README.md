# HRpc

HRpc is a lightweight, high-performance C# library for TCP-based, event-driven communication. It provides a robust framework for dispatching events, serializing messages, and managing TCP connections in a structured and testable manner.

---

## Features

- **Event-Driven Messaging:** Easily subscribe to and emit events over TCP connections.

- **Message Serialization:** Simplified message encoding and decoding via `MessageEnvelope`.

- **Flexible Dispatcher:** Subscribe multiple handlers for different event types.

- **Testable Architecture:** Designed for unit testing with support for mocks.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

- Recommended IDE: Visual Studio, VS Code, or JetBrains Rider

---

## Project Structure

HRpc/

├─ Core/ # Core library classes and interfaces

├─ Tests/ # Unit and integration tests

├─ HRpc.sln # Solution file

└─ README.md # Project documentation

- **Core:** Contains the main implementation (`EventDispatcher`, `TcpClientWrapper`, models, and interfaces).

- **Tests:** Contains MSTest unit tests and mocks.

---

## Getting Started

### Clone and Build

```bash

git clone "https://github.com/hamedafzali/HRpc"

cd HRpc

dotnet build  HRpc.sln

dotnet test  HRpc.sln  --logger  "console;verbosity=detailed"





using TcpEventFramework.Core;

using TcpEventFramework.Models;

using TcpEventFramework.Interfaces;



var dispatcher  =  new  EventDispatcher();

var connection  =  new  TcpClientWrapper("127.0.0.1", 9000);



dispatcher.Subscribe(connection, "TestEvent",  msg =>

{

Console.WriteLine($"Received: {msg.Payload}");

});



await dispatcher.Emit(connection, new  EventMessage("TestEvent", "Hello World"));



```

Contributing

We welcome contributions from the community:

Fork the repository

Create a feature branch (git checkout -b feature/MyFeature)

Commit your changes (git commit -am "Add feature")

Push to the branch (git push origin feature/MyFeature)

Submit a pull request

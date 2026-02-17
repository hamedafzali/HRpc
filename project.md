# HRpc NuGet Project Notes

## Package Identity

- Package ID: `HRpc`
- Assembly: `HRpc.dll`
- License: `MIT`
- Repository: [https://github.com/hamedafzali/HRpc](https://github.com/hamedafzali/HRpc)
- Current Version: `1.0.0`

## Purpose

HRpc is a lightweight, event-driven TCP communication library for .NET.  
It provides a consistent client/server abstraction for line-delimited JSON messages with an event-based programming model.

## Target Frameworks

- `net6.0`
- `net7.0`
- `net9.0`

## Public API Surface

### Core Types

- `TcpEventFramework.Core.TcpClientWrapper`
- `TcpEventFramework.Core.TcpServer`
- `TcpEventFramework.Core.EventDispatcher`
- `TcpEventFramework.Models.EventMessage`
- `TcpEventFramework.Models.MessageEnvelope`

### Interfaces

- `TcpEventFramework.Interfaces.ITcpConnection`
- `TcpEventFramework.Interfaces.ITcpClient`
- `TcpEventFramework.Interfaces.ITcpServer`
- `TcpEventFramework.Interfaces.IEventMessage`

### Event Args

- `TcpEventFramework.Events.MessageReceivedEventArgs`
- `TcpEventFramework.Events.ConnectionEventArgs`
- `TcpEventFramework.Events.ErrorEventArgs`

## Message Protocol

HRpc transmits one JSON envelope per line:

```json
{"eventName":"EventName","payload":"Any string payload"}
```

Notes:
- UTF-8 encoding
- Newline (`\n`) delimits each message
- Invalid JSON payloads trigger `ErrorOccurred`

## Behavioral Guarantees

- `ConnectAsync(...)` throws on connection failure and also raises `ErrorOccurred`
- `EventDispatcher.Subscribe(...)` returns `IDisposable` for explicit unsubscribe
- `IsConnected` reflects tracked connection state, not raw socket heuristic
- `TcpServer` emits `MessageReceived` for valid incoming envelopes

## Recommended Usage

```csharp
using TcpEventFramework.Core;
using TcpEventFramework.Models;

var dispatcher = new EventDispatcher();
using var connection = new TcpClientWrapper();

await connection.ConnectAsync("127.0.0.1", 9000);

using var subscription = dispatcher.Subscribe(connection, "Ping", msg =>
{
    Console.WriteLine($"Received: {msg.Payload}");
});

await dispatcher.Emit(connection, new EventMessage("Ping", "Pong"));
await connection.CloseAsync();
```

## NuGet Packaging Status

Configured in `HRpc.csproj`:

- `GeneratePackageOnBuild=true`
- `PackageReadmeFile=README.md`
- `PackageLicenseExpression=MIT`
- `RepositoryUrl` and `PackageProjectUrl` set

Package output (default):

- `bin/Debug/HRpc.1.0.0.nupkg`

## Pre-Publish Checklist

1. Update `<Version>` in `HRpc.csproj`.
2. Ensure `README.md` reflects final API.
3. Run `dotnet test HRpc.sln`.
4. Run `dotnet pack -c Release`.
5. Validate package metadata and readme rendering on NuGet.
6. Publish with `dotnet nuget push` using an API key.

## Release Runbook

Run from `/Users/hamed.afzali/Desktop/Repos/HRpc`:

```bash
dotnet clean HRpc.sln
dotnet restore HRpc.sln
dotnet test HRpc.sln -c Release
dotnet pack HRpc.csproj -c Release -o ./artifacts
```

Verify package contents:

```bash
ls -la ./artifacts
```

Publish to NuGet:

```bash
dotnet nuget push "./artifacts/HRpc.<VERSION>.nupkg" \
  --api-key "<NUGET_API_KEY>" \
  --source "https://api.nuget.org/v3/index.json" \
  --skip-duplicate
```

## Release Gates

- Tests pass in `Release` configuration
- Package contains readme and expected metadata
- Public API changes are intentional and documented
- Version increment matches semantic versioning rules

## Post-Publish Checks

1. Open package page on NuGet and confirm readme renders correctly.
2. Install in a clean sample project:
   `dotnet add package HRpc --version <VERSION>`.
3. Run a basic connect/send/receive smoke test.

## Rollback Strategy

NuGet packages are immutable. If a bad version is published:

1. Deprecate the affected version on NuGet.
2. Publish a fixed patch version (for example `1.0.1`).
3. Update release notes to direct users to the patched version.

## Supporting Files

- Release notes template: `/Users/hamed.afzali/Desktop/Repos/HRpc/RELEASE_NOTES_TEMPLATE.md`
- Step-by-step publish checklist: `/Users/hamed.afzali/Desktop/Repos/HRpc/PUBLISH_CHECKLIST.md`

## Versioning Guidance

Use semantic versioning:

- Patch: bug fixes, no API change (`1.0.x`)
- Minor: backward-compatible features (`1.x.0`)
- Major: breaking API/protocol changes (`x.0.0`)

## Known Scope Limits

- Payload is string-based (no typed schema enforcement)
- Protocol is newline-delimited JSON only
- No built-in auth, encryption, or message replay protection

For production internet-facing usage, run behind TLS-enabled channels and authentication boundaries.

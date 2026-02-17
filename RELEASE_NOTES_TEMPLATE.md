# HRpc v1.0.1 Release Notes

Release date: 2026-02-17
NuGet: https://www.nuget.org/packages/HRpc/1.0.1

## Summary

- Reliability-focused release that hardens connection lifecycle handling, adds cancellation support, and improves release documentation.

## Added

- `ITcpServer.MessageReceived` event for server-side message observation.
- Publish workflow docs: `project.md` and `PUBLISH_CHECKLIST.md`.

## Changed

- `ITcpConnection.ConnectAsync` now accepts `CancellationToken`.
- `ITcpServer.StartAsync` now accepts `CancellationToken`.
- `EventDispatcher.Subscribe` now returns `IDisposable` for explicit unsubscribe.
- `TcpConnection.IsConnected` now uses tracked state instead of raw socket heuristic.

## Fixed

- `ConnectAsync` now surfaces failures to callers (throws) while still raising `ErrorOccurred`.
- Message envelope deserialization now validates null results and throws `FormatException` for invalid payloads.
- Server now parses incoming envelopes and raises message events instead of discarding lines.

## Breaking Changes

- Method signature updates:
  - `ConnectAsync(string host, int port)` -> `ConnectAsync(string host, int port, CancellationToken cancellationToken = default)`
  - `StartAsync(int port)` -> `StartAsync(int port, CancellationToken cancellationToken = default)`
- `EventDispatcher.Subscribe(...)` return type changed from `void` to `IDisposable`.

## Migration Notes

- Existing call sites continue to compile when using default optional parameters.
- If you relied on previous `Subscribe` behavior, store and dispose returned subscription when handler should be removed.

## Verification

- Target frameworks validated: `net6.0`, `net7.0`, `net9.0`
- Test command: `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- Pack command: `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## Checksums / Artifacts

- `HRpc.1.0.1.nupkg`

## Contributors

- Hamed Afzali

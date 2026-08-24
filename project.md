# HRpc NuGet Project Notes

## Package Identity

- Package ID: `HRpc`
- Assembly: `HRpc.dll`
- License: `MIT`
- Repository: [https://github.com/hamedafzali/HRpc](https://github.com/hamedafzali/HRpc)
- Current Version: `1.2.0`

## Purpose

HRpc is a lightweight, event-driven TCP communication library for .NET.  
It provides a consistent client/server abstraction for line-delimited JSON messages with an event-based programming model.

## Target Frameworks

- `net48`
- `net8.0`
- `net9.0`

## Public API Surface

See [README.md](README.md) for the full public API — Quick Start, Advanced Usage, and
constructor/accessor semantics with code samples.

## Message Protocol

See [PROTOCOL.md](PROTOCOL.md) for wire format, versioning, and the full
FATAL/RECOVERABLE error-handling table.

## Behavioral Guarantees

- `ConnectAsync(...)` throws on connection failure and also raises `ErrorOccurred`
- `EventDispatcher.Subscribe(...)` returns `IDisposable` for explicit unsubscribe
- `IsConnected` reflects tracked connection state, not raw socket heuristic
- `TcpServer`/`PipeServer` emit `MessageReceived` for valid incoming envelopes
- Maximum message size defaults to 1 MiB (`MessageSizeLimits.DefaultMaxMessageSizeBytes`),
  configurable via `MaxMessageSizeBytes` on each transport; an oversized line is fatal
  (`LineTooLongException`, connection dropped) — see PROTOCOL.md
- There is currently no server-to-client response/push channel beyond
  `PipeServer.InitialMessage` (a single envelope sent once at connect time, before that
  client's own messages are read)

## Recommended Usage

```csharp
using HRpc.Core;
using HRpc.Models;

var dispatcher = new EventDispatcher();
using var connection = new TcpClientWrapper();

await connection.ConnectAsync("127.0.0.1", 9000);

using var subscription = dispatcher.Subscribe(connection, "Ping", msg =>
{
    Console.WriteLine($"Received: {msg.GetPayloadAsString()}");
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

Package output (default, version derived from the build — see "Release Runbook" below):

- `bin/Debug/HRpc.<VERSION>.nupkg`

## Release Runbook

Publishing is automated by `.github/workflows/publish.yml`, triggered by pushing a
`v*` tag (e.g. `v1.2.0`, `v1.2.0-preview.1`). It re-runs the full CI test matrix, then:
version is derived from the tag (not from `HRpc.csproj`'s `<Version>`, which is
overridden via `/p:Version=` at pack time and is only a local-build convenience), a
version guard rejects any tag not strictly greater than the highest version already on
NuGet, then it packs and pushes both `.nupkg` and `.snupkg` to nuget.org via NuGet
Trusted Publishing (OIDC): the `publish` job requests a GitHub-issued OIDC token,
exchanges it for a short-lived NuGet API key via `NuGet/login@v1` (using the `NUGET_USER`
repo secret to identify the nuget.org account), and uses that key for both pushes. No
long-lived `NUGET_API_KEY` secret is stored or used by CI.

**`NUGET_USER` must be the nuget.org profile username, not the GitHub username** — these
are two different accounts and, for this repo, two different strings (nuget.org:
`afzali.hamed`, GitHub: `hamedafzali`). `NuGet/login@v1` resolves the `user:` input via
nuget.org's own account lookup and matches trust policies strictly by that account's ID —
it is never matched against the GitHub org/repo owner. Setting `NUGET_USER` to the GitHub
username produces a token-exchange HTTP 401 with the message "No matching trust policy
owned by user `<value>`" — the exact same message nuget.org returns when the username
doesn't exist at all, which is why this is easy to misdiagnose as a policy-configuration
problem. This cost four separate failed publish attempts to isolate (each attempt tried a
different wrong value, and each produced the identical error). If a token exchange 401s
with that message, check the nuget.org username first, before touching the policy.

It also supports a `workflow_dispatch` dry run (build/test/pack/version-guard only, no
push, no secret required) — see `PUBLISH_CHECKLIST.md` for the exact commands.

Manual local packaging (for a dry run without CI, or local inspection) still works:

```bash
dotnet clean HRpc.sln
dotnet restore HRpc.sln
dotnet test HRpc.sln -c Release
dotnet pack HRpc.csproj -c Release /p:Version=<VERSION> -o ./artifacts
```

Verify package contents:

```bash
ls -la ./artifacts
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
2. Publish a fixed patch version (for example `1.2.1`).
3. Update release notes to direct users to the patched version.

## Supporting Files

- Release notes template: `/Users/hamed.afzali/Desktop/Repos/HRpc/RELEASE_NOTES_TEMPLATE.md`
- Step-by-step publish checklist: `/Users/hamed.afzali/Desktop/Repos/HRpc/PUBLISH_CHECKLIST.md`

## Versioning Guidance

Use semantic versioning:

- Patch: bug fixes, no API change (`1.2.x`)
- Minor: backward-compatible features (`1.x.0`)
- Major: breaking API/protocol changes (`x.0.0`)

Pre-release channels use a `-` suffix (e.g. `1.2.0-preview.1`); `publish.yml` derives the
NuGet push channel (`stable` vs `preview`) from the presence of that suffix.

## Known Scope Limits

- Payload is a typed, deferred-parse JSON value (no schema enforcement beyond
  JSON-shape validity — see the payload API above)
- Protocol is newline-delimited JSON only
- No built-in auth, encryption, or message replay protection
- No server-to-client response/push channel beyond `PipeServer.InitialMessage`

## Latest Implementation Status

- Namespace renamed `TcpEventFramework.*` -> `HRpc.*` (breaking).
- Wire protocol bumped to v2: `payload` is a typed JSON value, not always a string;
  `IEventMessage.Payload` removed with no compatibility shim, replaced by
  `PayloadValue`/`GetPayload<T>()`/`TryGetPayload<T>()`/`GetPayloadAsString()`.
- Malformed messages are recoverable (connection survives, `ErrorOccurred` fires) unless
  the failure is framing-level (oversized line, unsupported protocol version, transport
  error), which remains fatal.
- Added local Named Pipe client/server transport via `PipeConnection`/`PipeClientWrapper`/`PipeServer`.
- Uses the same newline-delimited JSON `MessageEnvelope` protocol as TCP.
- Pipe transport is local-only (same-machine), no remote pipe host support.
- Removed legacy `pipetemplate/` sample sources (they depended on external GMG/BasicTools packages).
- Added `net48` target (dropping `net6.0`/`net7.0`, adding `net8.0`) and implemented
  NETFRAMEWORK compatibility for cancellation-aware reads and async disposal.
- Publishing is now automated via a tag-triggered GitHub Actions workflow
  (`.github/workflows/publish.yml`) instead of manual `dotnet nuget push`.

For production internet-facing usage, run behind TLS-enabled channels and authentication boundaries.

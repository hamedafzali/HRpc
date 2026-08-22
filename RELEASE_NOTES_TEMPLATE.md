# HRpc v1.2.0 Release Notes

Release date: 2026-08-20
NuGet: https://www.nuget.org/packages/HRpc/1.2.0

## Summary

Major stabilization release: five breaking changes land together (namespace rename,
wire-format/protocol-version bump, `IEventMessage` interface change, dropped TFMs) plus
two safety-oriented behavior changes and a bug fix. See `CHANGELOG.md`'s `[1.2.0]`
entry for full migration guidance — read it in full before upgrading from 1.1.x, in
the "Migration order" it specifies.

## Breaking Changes

- Namespace rename: `TcpEventFramework.*` → `HRpc.*` (mechanical, no type/behavior
  change).
- Protocol version 2: `MessageEnvelope.payload` is typed JSON, not always a
  pre-serialized string. 1.1.x-shaped envelopes remain readable; a v1.2.0-written
  non-string payload is generally not readable by a 1.1.x peer.
- `IEventMessage.Payload` (`string`) removed — no `[Obsolete]` fallback. Any external
  `IEventMessage` implementer must migrate to `PayloadValue`/`GetPayload<T>()`/
  `TryGetPayload<T>()`/`GetPayloadAsString()`.
- `EventMessage` constructor semantics: `(string, string)` embeds literally as a JSON
  string; `(string, object?)` serializes and embeds as a nested JSON value; use the new
  `EventMessage.FromJson(string, string)` for pre-serialized JSON text. See
  CHANGELOG.md — this is the change most likely to compile cleanly and misbehave
  silently.
- Target frameworks: `net6.0`/`net7.0` dropped (out of support), `net8.0` added.
  Supported set is now `net48`, `net8.0`, `net9.0`.

## Behavior Changes (non-breaking, but changes observable semantics)

- Malformed messages no longer disconnect the connection — `ErrorOccurred` fires, the
  message is skipped, reading continues. Oversized messages and unsupported protocol
  versions remain fatal.
- A throwing `MessageReceived` subscriber no longer disconnects the connection or
  blocks delivery to other subscribers.

## Added

- Protocol version field (`v`) with `CurrentProtocolVersion`/`LegacyProtocolVersion`/
  `MinimumSupportedVersion`.
- `cid`/`type`/`error` fields on `MessageEnvelope`, reserved for v1.3.0 — inert in
  v1.2.0.
- Typed payload accessors (`PayloadValue`, `GetPayload<T>()`, `TryGetPayload<T>()`,
  `GetPayloadAsString()`) on both `MessageEnvelope` and `IEventMessage`/`EventMessage`.
- `MaxMessageSizeBytes` (default 1 MB) on all connection/server types — a new hard cap
  on a single incoming message.
- Packaging: SourceLink, deterministic/CI builds, `.snupkg` symbol package,
  `PackageReadmeFile`, refined `PackageTags`.

## Fixed

- `PipeServer` now handles more than one message per client connection (previously
  returned after the first message — every subsequent message was silently lost).
- `MessageEnvelope.Serialize()` no longer mutates the instance's `Version`.

## Migration Notes

See `CHANGELOG.md`'s `[1.2.0]` entry, "Migration order" section, for the full
step-by-step upgrade path and the `EventMessage` constructor pitfall in particular.

## Verification

- Target frameworks validated: `net48`, `net8.0`, `net9.0`
- Test command: `dotnet test /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.sln -c Release`
- Pack command: `dotnet pack /Users/hamed.afzali/Desktop/Repos/HRpc/HRpc.csproj -c Release -o /Users/hamed.afzali/Desktop/Repos/HRpc/artifacts`

## Checksums / Artifacts

- `HRpc.1.2.0.nupkg`
- `HRpc.1.2.0.snupkg`

## Contributors

- Hamed Afzali

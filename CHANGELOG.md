# Changelog

All notable changes to HRpc are documented in this file.

## [Unreleased]

## [1.2.0] - 2026-08-24

If you are upgrading from 1.1.x, read this entire entry before touching code. This
release lands **five breaking changes at once**: a namespace rename, a wire-format
change, an interface change, a dropped-TFM change, and (implicitly) a behavior change
around what "the connection died" means. None of them are individually large, but
skipping one will produce a failure that looks unrelated to the upgrade.

### Fixed (found by a fresh-consumer test against the published `1.2.0-preview.2`
package, after CI was already green — before stable)

Every fix above this point was found by CI/unit tests run against source. These two
were only found by installing `1.2.0-preview.2` from nuget.org into a brand-new
project, as a first-time consumer would, and following the README exactly — proof
that "CI is green" and "the README's own examples work for a new user" are not the
same claim.

- **`server.StartAsync(...)`, called exactly as every README example calls it,
  deadlocked forever.** `TcpServer.StartAsync`/`PipeServer.StartAsync` ran their
  entire accept loop inline inside the `async Task` method body, so the returned
  `Task` only completed once the server was stopped — meaning `await
  server.StartAsync(...)` never returned while the server was up. This bug predates
  1.2.0 entirely (present since the library's first commit, confirmed via `git log`)
  but had never been caught because every existing test used a fire-and-forget
  `var serverTask = server.StartAsync(...)` pattern rather than the README's plain
  `await`. Fixed: `StartAsync` now performs synchronous setup (bind/listen, or pipe
  validation) and returns as soon as the server is actually listening; the accept
  loop runs as a tracked background task that `StopAsync` now awaits during
  teardown. No signature change — existing fire-and-forget callers are unaffected.
- **`GetPayload<T>()`/`TryGetPayload<T>()` silently returned a `T` with every member
  defaulted instead of throwing, on a genuine shape mismatch (net8.0/net9.0).**
  Two compounding causes: `System.Text.Json` matches property names
  case-sensitively by default (so ordinary camelCase JSON — e.g. from
  `EventMessage.FromJson`, or any non-.NET peer — failed to bind to a PascalCase C#
  type), and it does not throw when a JSON object's constructor/property
  parameters go unmatched, unless the target type uses `required` members (which
  caller-supplied `T` is never guaranteed to). This directly violated the
  documented `GetPayload<T>()` contract ("throws ... on a shape mismatch,"
  analogous to `int.Parse`) — a payload with essentially nothing in common with
  `T` returned a zeroed-out `T` rather than failing loudly. Fixed:
  `PropertyNameCaseInsensitive` is now enabled for the case-mismatch scenario, and
  a new shape check throws `JsonException` when a JSON object shares **no**
  property name at all (case-insensitively) with any public property of `T` — see
  `PROTOCOL.md`'s note on this check's coarse-match limitation. `Dictionary<TKey,
  TValue>`-shaped targets are exempted (their own public properties never match
  JSON payload keys). The net48/Newtonsoft.Json path already matched property
  names case-insensitively and gains only the new shape check.
- **`dotnet pack` on this multi-targeted (`net48;net8.0;net9.0`) project
  intermittently skipped building one or more target frameworks before packing**,
  producing `NU5026` ("...bin/Release/net48/HRpc.dll...not found on disk") — a
  release-pipeline issue, not a runtime one, but one that could have shipped an
  incomplete package. `publish.yml` now builds explicitly, then packs with
  `--no-build`, rather than relying on `dotnet pack`'s implicit build.

### Fixed (found by CI on `stabilize/v1.2.0`, before first release)

- **Disconnect/teardown could throw `ObjectDisposedException` out of `CloseAsync`,
  sometimes suppressing `Disconnected`/`ClientDisconnected` entirely.** `TcpConnection`
  and `TcpServer` each read `Socket.RemoteEndPoint` *after* the socket could already be
  closed — `TcpConnection.RaiseDisconnected()` ran from `ReceiveLoopAsync`'s `finally`,
  racing `CloseAsync`'s own disposal of the same client; `TcpServer.HandleClientAsync`'s
  `finally` re-read the endpoint at teardown instead of the one already captured at
  accept time. `Socket.RemoteEndPoint` throws `ObjectDisposedException` on a disposed
  socket on **every** target framework, so this was never a net48-specific issue — net48
  merely made it deterministic, because its synchronous `Stream.Dispose()` path in
  `CloseAsync` gives the racing continuation no scheduling gap to lose the race in;
  net8.0/net9.0's `await Stream.DisposeAsync()` path has the identical race with lower
  (but nonzero) probability of manifesting. Fixed by capturing the remote endpoint once,
  at connect/accept time, and never reading it again during teardown.
- **`TcpServer.StartAsync`'s accept loop surfaced normal shutdown as a fault.** Calling
  `StopAsync()` disposes the listener while an accept is pending; on net48 (whose
  `AcceptTcpClientAsync` overload has no `CancellationToken` support) the pending accept
  observed that as `ObjectDisposedException` rather than cancellation, so a routine
  `StopAsync()` call could rethrow out of `StartAsync` and report a spurious error. Now
  scoped: `ObjectDisposedException` is treated as clean shutdown only when our own
  cancellation was already requested, so an unrelated/genuine `ObjectDisposedException`
  still surfaces normally.
- **`TcpServer.StartAsync` could hang forever on net48 if the caller cancelled the token
  without also calling `StopAsync`.** `TcpListener.AcceptTcpClientAsync()` has no
  `CancellationToken`-aware overload on net48, so a pending accept never observed the
  token becoming cancelled — nothing else was calling `Stop()` on it, so the accept
  blocked indefinitely. This was previously masked by the `ObjectDisposedException`
  crash above, since a run never got past that failure to reach this path. Fixed by
  registering a callback on the token that stops the listener, which unblocks the
  pending accept as the same `ObjectDisposedException` the shutdown catch above already
  treats as clean cancellation. No effect on net8.0/net9.0, which already cancel
  natively via the token-accepting overload.
- **A throwing `Disconnected`/`ClientDisconnected` subscriber could abort cleanup.**
  These events were invoked unguarded from inside `finally` blocks (`TcpConnection`,
  `PipeConnection`, `TcpServer.HandleClientAsync`, `PipeServer.HandleClientAsync`), so an
  exception from one subscriber could stop later cleanup (e.g. `client.Close()`,
  `stream.Dispose()`) from running and escape the finally entirely — defeating the error
  taxonomy the same way the `ObjectDisposedException` issue above did. All four are now
  invoked per-subscriber via the same guarded dispatch already used for
  `MessageReceived`/`ErrorOccurred`; a throwing subscriber is swallowed and traced rather
  than propagated.

- **`GetPayload<T>()` threw a raw `FormatException` instead of a `JsonException` for a
  type-mismatched payload, net48 only.** `JToken.ToObject<T>()` falls through to
  `Convert.ChangeType` for primitive-type coercion (e.g. requesting a string payload as
  `int`), which throws `FormatException`/`OverflowException`/`InvalidCastException`
  directly rather than wrapping it, unlike `System.Text.Json`'s
  `JsonElement.Deserialize<T>()`, which always throws `JsonException` for the equivalent
  shape mismatch. `GetPayload<T>()` now normalizes these into
  `Newtonsoft.Json.JsonException` on net48 so callers see one exception type for a
  payload/type mismatch regardless of which serializer backs the current target
  framework.
- **`ConnectAsync` threw `OperationCanceledException` instead of `TaskCanceledException`
  for a pre-cancelled token, net48 only.** The net48 path used
  `cancellationToken.ThrowIfCancellationRequested()`, which throws the bare base type;
  net8.0/net9.0's `TcpClient.ConnectAsync(..., cancellationToken)` overload throws
  `TaskCanceledException` specifically, since that's what a canceled `Task`'s awaiter
  always throws. Switched net48 to `await Task.FromCanceled(cancellationToken)` so both
  paths throw the same, more specific exception type.

  > Both of the exception-type fixes above (`GetPayload<T>` and `ConnectAsync`) change
  > the concrete exception type a net48 caller observes. Since neither shipped in any
  > prior release, this is not a breaking change to already-shipped 1.2.0 behavior —
  > but if you wrote code against a pre-release `stabilize/v1.2.0` build that `catch`es
  > `FormatException` or `OperationCanceledException` specifically at these two call
  > sites, update it to the types above.

### Migration order

1. **Namespace rename.** Every `using TcpEventFramework.*;` becomes `using HRpc.*;`
   (`HRpc.Core`, `HRpc.Events`, `HRpc.Interfaces`, `HRpc.Models`, `HRpc.Utils`). This is
   a pure find-and-replace — no type was renamed, moved, or restructured, only the
   namespace prefix changed. Do this first; nothing else compiles usefully until you do.
2. **`IEventMessage.Payload` is gone.** Fix every read site (see "IEventMessage /
   EventMessage typed payload" below) — usually `.Payload` → `.GetPayloadAsString()`.
3. **Re-check every `EventMessage` construction site.** The constructor you call now
   controls wire shape (see "EventMessage constructor semantics" below, the change most
   likely to compile cleanly and misbehave silently).
4. **If you target `net48`**, no action needed — `Newtonsoft.Json` is still pulled in
   for you automatically. **If you targeted `net6.0` or `net7.0`**, retarget your
   project to `net8.0` or `net9.0` before upgrading the package reference (see
   "Target framework changes" below).
5. Re-run your test suite. If you have tests asserting "a malformed message disconnects
   the client," they will now fail — see "Malformed messages" below; that assertion is
   no longer true by design.

### Breaking: namespace rename (`TcpEventFramework.*` → `HRpc.*`)

Every public namespace changed prefix: `TcpEventFramework.Core` → `HRpc.Core`,
`.Events` → `HRpc.Events`, `.Interfaces` → `HRpc.Interfaces`, `.Models` → `HRpc.Models`,
`.Utils` → `HRpc.Utils`. No type name, member, or behavior changed as part of this —
it is a mechanical rename to match the package id (`HRpc`) the library has shipped
under since 1.0.0. Fix with a project-wide find-and-replace of `TcpEventFramework` →
`HRpc` across your `using` directives and any fully-qualified references.

### Breaking (wire format): protocol version 2 — `MessageEnvelope.payload` is typed JSON

Previously every payload — even a structured object — had to be pre-serialized to a
string by the caller, which the envelope serializer then re-escaped inside the outer
JSON (double encoding, inflated size, no typed access on receive). As of 1.2.0,
`MessageEnvelope.PayloadValue` (`JsonElement`/`JToken`) carries the payload as a
deferred-parse JSON value, and an object/array payload is written as a *nested* JSON
value on the wire instead of an escaped string.

- A 1.1.x-shaped envelope (payload always a string, `v` absent) is still fully
  **readable** by 1.2.0 — receiving from an un-upgraded peer is safe.
- A non-string payload **written** by 1.2.0 is generally **not** readable by a 1.1.x
  peer. If you have a mixed fleet mid-rollout, upgrade all readers of a given event
  stream before any writer starts sending structured (non-string) payloads on it.
- `CurrentProtocolVersion` was bumped from `1` to `2`. `LegacyProtocolVersion` /
  `MinimumSupportedVersion` stay at `1`, so `v`-absent / `v:1` envelopes from old peers
  continue to be accepted.
- The old string-only surface (`MessageEnvelope(string, string)` constructor, `Payload`
  string property) still compiles but is now `[Obsolete]`, pointing at
  `MessageEnvelope(string, object?)` / `PayloadValue` / `GetPayload<T>()` /
  `TryGetPayload<T>()` / `GetPayloadAsString()`.

See `PROTOCOL.md`'s [Payload representation](PROTOCOL.md#payload-representation-b3)
section for the full compatibility matrix.

### Breaking (interface): `IEventMessage.Payload` removed, no compatibility shim

The typed-payload representation above is now propagated up to the public
`MessageReceived`/`SendAsync` surface, closing a gap where double-encoding was still
present end-to-end even after the `MessageEnvelope` change. `IEventMessage`/
`EventMessage` now expose the same `PayloadValue` / `GetPayload<T>()` /
`TryGetPayload<T>()` / `GetPayloadAsString()` accessors as `MessageEnvelope`.

**Unlike `MessageEnvelope.Payload` above, there is no `[Obsolete]` fallback here** —
`IEventMessage.Payload` is simply gone, and any external `IEventMessage` implementer
must migrate before this compiles. Migrating a read site:

- If you only ever sent/expected plain string payloads: replace `.Payload` with
  `.GetPayloadAsString()` — byte-for-byte the same value, never throws.
- If you want typed access to a structured payload: use `.GetPayload<T>()` (throws
  `JsonException`/`FormatException` on a shape mismatch) or `.TryGetPayload<T>(out var
  value)` (non-throwing).

### Breaking (silent risk): `EventMessage` constructor semantics changed

**This is the change most likely to compile cleanly and produce a wrong result at
runtime — read this section even if the other three don't apply to you.**

`EventMessage` now has two constructors with deliberately different wire behavior,
selected by ordinary C# overload resolution — **not** by inspecting the argument's
content:

```csharp
new EventMessage("Greeting", "Hello");
// payload embedded literally as a JSON STRING: "Hello"

new EventMessage("Order", new { id = 1, qty = 2 });
// payload serialized by runtime type and embedded as a NESTED JSON VALUE: {"id":1,"qty":2}
```

The trap: if your existing 1.1.x code built a JSON string by hand and passed it to the
`string` overload — e.g. `new EventMessage("Order", jsonString)` where `jsonString` is
already `"{\"id\":1,\"qty\":2}"` — 1.2.0 will still compile that call (it resolves to
the `string` overload) and will now send the payload as a **string containing escaped
JSON text**, i.e. `"{\"id\":1,\"qty\":2}"` as the wire value, not the nested object
`{"id":1,"qty":2}` a caller instinctively expects from a "JSON-shaped" argument. This
is exactly the double-encoding the wire-format change above was meant to eliminate —
you can silently reintroduce it at any call site that hand-serializes JSON before
constructing an `EventMessage`.

If you have pre-serialized JSON text and want it embedded as a nested value (not
re-escaped), use the new factory instead of either constructor:

```csharp
var json = "{\"id\":1,\"qty\":2}"; // already serialized by something else
var msg = EventMessage.FromJson("Order", json); // payload: {"id":1,"qty":2}
```

Audit every `EventMessage` construction site during migration: `(string, string)` for
genuinely textual payloads, `(string, object?)` for anything you want serialized for
you, `FromJson(string, string)` for text you already know is JSON. See PROTOCOL.md's
[`IEventMessage`/`EventMessage` typed payload](PROTOCOL.md#ieventmessageeventmessage-typed-payload-b3-ext--breaking-for-implementers)
section for the full reasoning.

### Behavior change: malformed messages no longer disconnect the connection

Previously (in every release through 1.1.2), receiving a line that failed to
deserialize — malformed JSON, for example — caused `ErrorOccurred` to fire and then
immediately dropped the connection, exactly as if the transport itself had failed. As
of 1.2.0, that class of error is treated as **recoverable**: `ErrorOccurred` still
fires, but the offending message is skipped and the connection keeps reading
subsequent messages normally. This does **not** apply to an oversized message
(`LineTooLongException`) or an unsupported protocol version
(`UnsupportedProtocolVersionException`) — those remain fatal and still disconnect,
since in both cases the byte framing itself can no longer be trusted, unlike a
bounded, correctly-terminated line that simply failed to parse.

If any consumer built reconnect/retry logic around "a single malformed message means
the connection is dead," or a test asserts that behavior, it will no longer be
triggered — the connection now survives on its own. See `PROTOCOL.md`'s
[Error handling on receive](PROTOCOL.md#error-handling-on-receive) section for the
full per-error-class table.

- **A throwing `MessageReceived` subscriber no longer disconnects the connection or
  suppresses delivery to other subscribers**, for the same underlying reason.
  Previously, an exception thrown inside a consumer's own `MessageReceived` handler
  was caught by the same blanket handler that caught genuine protocol/transport
  errors, which both disconnected the connection and (since a single `Invoke` call on
  a multicast delegate aborts partway through its invocation list) could prevent other
  subscribers on the same event from ever seeing that message. Subscribers are now
  invoked individually; a throwing subscriber surfaces via `ErrorOccurred` but does
  not affect the connection or any other subscriber.

### Breaking: target framework changes — `net6.0`/`net7.0` dropped, `net8.0` added

`net6.0` and `net7.0` are out of support upstream and are no longer built or tested.
Supported frameworks are now `net48`, `net8.0`, `net9.0`. If your project targets
`net6.0` or `net7.0`, retarget to `net8.0` (current LTS) or `net9.0` before taking this
package version — there is no code-level change required beyond the TFM itself, since
no 1.1.x code path was conditioned on `net6.0`/`net7.0` specifically (only on
`net48` vs. everything else).

`Newtonsoft.Json` is now referenced only on the `net48` target (it backs the
`net48`-only serialization path); `net8.0`/`net9.0` consumers no longer pull it in as
a transitive dependency.

### Added

- Protocol version field (`v`) on the wire envelope, with a documented three-constant
  version-negotiation model (`CurrentProtocolVersion`, `LegacyProtocolVersion`,
  `MinimumSupportedVersion`). See `PROTOCOL.md`.
- Optional `cid` (correlation id), `type` (message-type discriminator), and `error`
  (`MessageError`: `code` + `message`) fields on `MessageEnvelope`, reserved for a
  future v1.3.0 request/response layer. Absent-by-default; no v1.2.0 code interprets
  them.
- Typed payload access on `MessageEnvelope`: `PayloadValue`, `GetPayload<T>()`,
  `TryGetPayload<T>()`, `GetPayloadAsString()`, and a new
  `MessageEnvelope(string eventName, object? payload)` constructor.
- Typed payload access on `IEventMessage`/`EventMessage`, mirroring `MessageEnvelope`:
  `PayloadValue`, `GetPayload<T>()`, `TryGetPayload<T>()`, `GetPayloadAsString()`, a new
  `EventMessage(string eventName, object? payload)` constructor, and a new
  `EventMessage.FromJson(string eventName, string json)` factory.
- **`MaxMessageSizeBytes`**, default **1 MB** (`1024 * 1024` bytes), on `Connection`,
  `Server`, `TcpConnection`, `TcpServer`, `PipeConnection`, and `PipeServer`. A peer
  that sends a single line exceeding this before a newline terminator raises
  `ErrorOccurred` with a `LineTooLongException` and the connection is closed — this is
  a hard cap that did not exist before 1.2.0 (a malicious or misbehaving peer could
  previously force unbounded buffering). Increase it if you intentionally send
  messages larger than 1 MB.
- A swallowed `ErrorOccurred` subscriber exception is now written to
  `System.Diagnostics.Trace.WriteLine` instead of being silently discarded, so a bug in
  a consumer's own error handler doesn't produce total silence.

### Fixed

- **`PipeServer` now handles more than one message per client connection.** Previously
  it returned (and effectively dropped the connection's read loop) after the first
  message — every message after the first was silently lost. If you were working
  around this by reconnecting per-message, that workaround is no longer necessary.
- `MessageEnvelope.Serialize()` no longer mutates the instance's `Version` — a new
  outgoing envelope must be constructed via `MessageEnvelope(string, object?)`, which
  stamps `CurrentProtocolVersion` at construction time instead.

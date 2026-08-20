# Changelog

All notable changes to HRpc are documented in this file.

## [Unreleased]

## [1.2.0] - 2026-08-20

If you are upgrading from 1.1.x, read this entire entry before touching code. This
release lands **five breaking changes at once**: a namespace rename, a wire-format
change, an interface change, a dropped-TFM change, and (implicitly) a behavior change
around what "the connection died" means. None of them are individually large, but
skipping one will produce a failure that looks unrelated to the upgrade.

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

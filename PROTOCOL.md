# HRpc Wire Protocol

This document describes the wire format HRpc uses over both of its transports (TCP and
Named Pipes). It is the contract that any future protocol change is measured against —
if a change isn't described here, it isn't part of the protocol yet.

## Framing

Messages are UTF-8 encoded JSON objects, one per line, delimited by `\n`. A trailing `\r`
before the `\n` is tolerated and stripped (CRLF-terminated peers work correctly). Each
line is exactly one envelope; there is no batching or multiplexing of multiple envelopes
onto one line.

A connection carries a stream of these lines in one direction at a time per transport
class — `TcpConnection`/`PipeConnection` receive from a `TcpServer`/`PipeServer`, and send
via `SendAsync`. There is currently no server-to-client push channel beyond
`PipeServer.InitialMessage` (a single envelope sent once, immediately after a client
connects, before that client's own messages are read).

### Maximum message size

Each receive loop enforces a maximum size, in UTF-8 encoded bytes, for a single line,
excluding the trailing newline delimiter. This is a defensive bound against unbounded
memory growth from a misbehaving or malicious peer — the reader checks accumulated size
incrementally, before allocating the full line, so an oversized message is never fully
buffered before rejection.

- Default: 1,048,576 bytes (1 MiB) — see `MessageSizeLimits.DefaultMaxMessageSizeBytes`.
- Configurable via the `MaxMessageSizeBytes` property on `TcpConnection`, `PipeConnection`,
  `TcpServer`, `PipeServer`, and the `Connection`/`Server` facades (which propagate their
  value to the underlying transport at connect/start time).
- On breach: `ErrorOccurred` fires with a `LineTooLongException`, and the connection is
  closed immediately. The offending message is never parsed and the connection is never
  resynchronized on the next newline — a peer that can't stay under the limit is
  considered untrustworthy for the rest of that connection, not just for one message.

## Error handling on receive

Not every failure while receiving is equally severe, and HRpc does not treat them all the
same way. Two axes matter:

1. **Is the byte framing itself still trustworthy?** If a line boundary was found (the
   reader saw a `\n` before any limit was hit) and the bytes up to that boundary simply
   failed to parse, the *next* line is still exactly where it should be — resynchronizing
   costs nothing. If the framing itself is in doubt (a message that never terminated
   within the size limit, or an envelope whose declared schema version this build doesn't
   understand), there is no way to know where the next valid line boundary actually is,
   so continuing to read risks misinterpreting subsequent bytes entirely.
2. **Did the failure happen in HRpc's own read/parse path, or inside a caller-supplied
   `MessageReceived` subscriber?** A bug in a consumer's own event handler is the
   consumer's problem, not evidence that the wire protocol broke — it must not be treated
   as a transport failure.

This is a desktop IPC library: one buggy or unexpected message should not be able to kill
the whole channel when the framing is intact and recovery is free. That principle drives
the classification below.

| Error class | Example | `ErrorOccurred` fires? | Connection survives? | What the peer should expect |
|---|---|---|---|---|
| **FATAL** | `LineTooLongException` (oversized line, no `\n` seen), `UnsupportedProtocolVersionException` (`v` outside the supported range), `IOException`/`ObjectDisposedException` (genuine transport failure) | Yes | **No** — connection is dropped | The offending message is never delivered, no further messages on this connection are read, `Disconnected`/`ClientDisconnected` fires. A new connection must be established to continue. |
| **RECOVERABLE** | Malformed JSON inside a correctly `\n`-terminated line; any other non-version-related deserialization failure | Yes | **Yes** — read loop continues | The offending message is skipped and never delivered as a `MessageReceived` event. The *next* line on the same connection is read and processed normally — no reconnect needed. |
| **Subscriber exception** | A `MessageReceived` handler throws (application bug, not a protocol violation) | Yes, with a message identifying it as coming from a subscriber | **Yes** — unaffected | The message *was* successfully framed and parsed; the failure is isolated to one handler. Other subscribers on the same event still receive the message, and the connection keeps reading normally. |

Implementation notes:

- The FATAL/RECOVERABLE split is implemented per receive loop (`TcpConnection`,
  `PipeConnection`, `TcpServer`, `PipeServer`) around the `MessageEnvelope.Deserialize`
  call only. The recoverable path is an *allow-list*, not a catch-all: `Utils.
  ReceiveLoopErrors.IsRecoverableParseFailure` matches only `FormatException` (thrown by
  `Deserialize` itself when the JSON parses to `null`) and the active serializer's own
  parse-failure type (`System.Text.Json.JsonException` on net8.0/net9.0,
  `Newtonsoft.Json.JsonException` on net48). Everything else — including
  `UnsupportedProtocolVersionException`, an `OperationCanceledException` surfacing from
  inside that same try, and anything that isn't a genuine parse failure at all (an
  `OutOfMemoryException`, or a bug inside HRpc's own deserialization path) — falls through
  to the loop's outer handler and is treated as fatal. This is deliberate: a broad
  `catch (Exception)` here would have silently mislabeled a process-level problem or an
  internal bug as "the peer sent a malformed message." Anything thrown by the line reader
  itself (`LineTooLongException`, or a transport-level `IOException`/
  `ObjectDisposedException`) is fatal by construction — it is never routed through the
  recoverable path at all, since it isn't thrown from inside `Deserialize`.
- `MessageReceived` subscribers are invoked individually (via `Utils.SafeInvoke`, which
  walks the event's `GetInvocationList()`), not as a single blanket call. A subscriber's
  exception is caught around *that one delegate only*, reported through `ErrorOccurred`,
  and does not prevent the remaining subscribers in the invocation list from receiving the
  same message.
- `ErrorOccurred` itself is raised through the same `SafeInvoke` guard, for the same
  reason: a throwing `ErrorOccurred` subscriber must not be able to affect the connection
  (or, transitively, disconnect it, or defeat the RECOVERABLE/subscriber-isolation
  guarantees above by escaping into the loop's outer catch). Unlike a throwing
  `MessageReceived` subscriber, a throwing `ErrorOccurred` subscriber's exception is not
  re-reported anywhere — there is no further event to escalate to, and doing so would risk
  unbounded recursion if that subscriber throws on every invocation. It is intentionally
  swallowed rather than re-raised, but **not silently**: the swallowed exception is written
  to `System.Diagnostics.Trace.WriteLine` (prefixed `[HRpc] ErrorOccurred subscriber threw
  and was swallowed: ...`) so a bug in a consumer's own logging/error handler doesn't
  produce total silence with no visible signal at all. `Trace.WriteLine` (not
  `Debug.WriteLine`) is used deliberately — `Debug.WriteLine` is `[Conditional("DEBUG")]`
  and compiles out of Release builds, which is what actually ships to NuGet, while `TRACE`
  is defined by default in both Debug and Release SDK-style configurations. A consumer that
  wants to see these writes can attach a `System.Diagnostics.TraceListener` (e.g.
  `Trace.Listeners.Add(new ConsoleTraceListener())`); by default .NET has no listener
  attached, so this diagnostic is inert unless a listener is present.
- **No built-in flood protection**: a peer sending a tight loop of malformed lines
  produces one `ErrorOccurred` per line, indefinitely, rather than being escalated to a
  disconnect. This is deliberate — each `ErrorOccurred` corresponds 1:1 with a line the
  peer actually sent, so this is not an amplification a malicious peer can exploit for
  more effect than the bytes they're already sending; and the "right" threshold (a count?
  a time window? per-connection or global?) is an application policy question HRpc has no
  basis to answer generically. A consumer that wants to guard against a malformed-message
  flood can already do so without any new API: track consecutive `ErrorOccurred` events in
  its own handler and call `CloseAsync()` (or `StopAsync()`/drop the client) once it
  decides enough is enough.

## Envelope schema

Every line deserializes to a JSON object with the following fields. Field names are
intentionally short — this schema is repeated on every single message.

| JSON field  | Type                     | Required | Meaning |
|-------------|--------------------------|----------|---------|
| `v`         | number                   | No       | Protocol version. Absent is treated as `1`. See [Version negotiation](#version-negotiation). |
| `eventName` | string                   | Yes      | The event name / message topic, as supplied by the sender. |
| `payload`   | any JSON value           | Yes      | The message payload, as supplied by the sender. **As of B3 (v1.2.0), this is typed JSON, not necessarily a string** — see [Payload representation](#payload-representation-b3) below. |
| `cid`       | string                   | No       | **RESERVED for v1.3.0.** Correlation id. See below. |
| `type`      | string                   | No       | **RESERVED for v1.3.0.** Message-type discriminator. See below. |
| `error`     | object                   | No       | **RESERVED for v1.3.0.** Error payload (`code` + `message`). See below. |

Example — a plain fire-and-forget event with a typed object payload (v1.2.0+):

```json
{"v":2,"eventName":"OrderCreated","payload":{"orderId":42}}
```

Example — the 1.1.x shape, still fully readable by 1.2.0 (see below):

```json
{"eventName":"OrderCreated","payload":"{\"orderId\":42}"}
```

### Payload representation (B3)

Before v1.2.0, `payload` was always a JSON string. A caller holding a structured object had to
serialize it to a string themselves, and the envelope serializer then escaped that string again
inside the outer JSON — e.g. `{"a":1}` became `"payload":"{\"a\":1}"` — which double-encoded the
payload, inflated message size (every `"` became `\"`), and gave the receiving end no typed
access without a second manual parse.

As of B3, `MessageEnvelope.PayloadValue` stores the payload as a deferred-parse JSON value —
`System.Text.Json.JsonElement` on net8.0/net9.0, `Newtonsoft.Json.Linq.JToken` on net48 —
instead of a flat `string`. A payload that is itself an object or array is now written as a
*nested JSON value* on the wire, not a re-escaped string:

```json
{"v":2,"eventName":"Foo","payload":{"a":1}}
```

Access:

- `GetPayload<T>()` — deserializes the payload as `T`. Throws (an exception matched by
  `Utils.ReceiveLoopErrors.IsRecoverableParseFailure` — i.e. `System.Text.Json.JsonException`/
  `Newtonsoft.Json.JsonException`/`FormatException`) if the payload's actual shape doesn't
  deserialize as `T`. Analogous to `int.Parse`. The shape check is deliberately coarse: it throws
  only when a JSON object shares **no** property names at all (case-insensitively) with any public
  property of `T`. A payload with one coincidentally-matching field name and an otherwise wrong
  shape passes the check and can still silently bind with defaults on the rest — this is a real
  limitation of the heuristic, not a bug, and is unrelated to the exemption for dictionary-shaped
  `T` (`IDictionary`/`IDictionary<TKey, TValue>`), which skips the check entirely because a
  dictionary's own public properties (`Count`, `Keys`, `Values`, `Comparer`) never match JSON
  payload keys even on a legitimate payload.
- `TryGetPayload<T>(out T? value)` — the non-throwing counterpart, analogous to `int.TryParse`.
  Returns `false` (and `value` is `default`) for the same class of failure `GetPayload<T>` would
  throw for.
- `GetPayloadAsString()` — never throws. Returns the string content directly if the payload is a
  JSON string (preserving the pre-B3 raw-string behavior for callers who want it), otherwise the
  raw JSON text of whatever value is present (object, array, number, bool, null).

The obsolete string-only surface (`MessageEnvelope(string, string)` constructor, `Payload`
string property) is still present and still compiles, marked `[Obsolete]` with a message
pointing at the new API. `Payload`'s getter returns `GetPayloadAsString()`; its setter accepts a
plain string and stores it as-is (so old code that assigns `Payload = someString` keeps
producing a JSON-string payload, not a nested object, exactly as before).

### `IEventMessage`/`EventMessage` typed payload (B3-EXT) — BREAKING for implementers

B3 (above) only changed `MessageEnvelope.PayloadValue`, the wire-level envelope type. That left a
gap: a consumer using the public `MessageReceived` event and `SendAsync` — the vast majority of
consumers — still went through `EventMessage.Payload` (a flat `string`), so double-encoding was
still present end-to-end through the public API even after B3 landed. B3-EXT closes that gap by
propagating the same typed-payload representation up to `IEventMessage`/`EventMessage`.

**This is a breaking interface change for anyone implementing `IEventMessage` directly**: the
`string Payload { get; }` member is removed with no compatibility shim (unlike `MessageEnvelope`,
which kept an `[Obsolete]` `Payload` property). `IEventMessage` now exposes the same
`PayloadValue`/`GetPayload<T>()`/`TryGetPayload<T>()`/`GetPayloadAsString()` accessor set as
`MessageEnvelope`, backed by the same deferred-parse `JsonElement`/`JToken` storage and the same
shared conversion logic (`Utils.PayloadCodec`, used internally by both types).

#### Two constructors, two distinct meanings

`EventMessage` has two public constructors, and the choice between them is deliberate rather than
inferred — HRpc never sniffs or auto-parses a string argument to guess intent:

- **`EventMessage(string eventName, string payload)`** — the argument is embedded **literally as
  a JSON string value**, verbatim, even if it looks like JSON. Correct only when the payload
  genuinely is text (e.g. `"Ping"`). `new EventMessage("Foo", "Bar")` binds here because C#
  overload resolution prefers an exact `string` match over `object?`.
- **`EventMessage(string eventName, object? payload)`** — the argument is serialized by its
  *runtime type* and embedded as a **nested JSON value**. Correct for a POCO, an anonymous
  object, a primitive, or `null`. Any non-`string` argument has only this overload available, so
  it is selected automatically with zero special-casing.

A caller already holding pre-serialized JSON text who wants the *nested* shape (not an escaped
string) should use neither constructor directly, but the static factory:

- **`EventMessage.FromJson(string eventName, string json)`** — parses `json` and embeds it as a
  nested JSON value via the same internal pass-through path the receive loops use. Throws (the
  active serializer's own JSON exception type) if `json` is not valid JSON — a caller-driven,
  synchronous conversion analogous to `int.Parse`, not a receive-loop path, so there is no
  recoverable/fatal distinction to make here.

```csharp
new EventMessage("Greeting", "Hello");                    // payload: "Hello"
new EventMessage("Order", new { id = 1, qty = 2 });        // payload: {"id":1,"qty":2}
EventMessage.FromJson("Order", "{\"id\":1,\"qty\":2}");    // payload: {"id":1,"qty":2}
```

The old string constructor is deliberately **not** marked `[Obsolete]` — it remains the correct,
literal choice for genuinely textual payloads. There is no ambiguity to warn about once the two
constructors' meanings are fixed by their signatures rather than by guessing at the argument's
content.

#### Receive-loop boundary: construction cannot fail on payload shape

All four receive loops construct `EventMessage` via an `internal` pass-through constructor that
takes the already-parsed `PayloadValue` from the deserialized `MessageEnvelope` and assigns it
directly, with no conversion or re-serialization. Constructing `EventMessage` inside a receive
loop therefore cannot throw due to payload shape — there is no method call in that path to reason
about. A shape mismatch can only surface later, when application code explicitly calls
`GetPayload<T>()`/`TryGetPayload<T>()` on the already-delivered message — outside the loop, where
a throw is ordinary application-level error handling, not a threat to the connection.

#### Wire compatibility

- **A 1.1.x-shaped envelope (payload always an escaped JSON string, `v` absent) is fully
  readable by 1.2.0.** `PayloadValue` stores it as a `JsonElement`/`JToken` of kind `String`;
  `GetPayloadAsString()` and `GetPayload<string>()` both return the original string content
  unchanged.
- **A 1.2.0 envelope with a non-string (object/array/number/bool) payload is generally NOT
  readable by a 1.1.x peer.** 1.1.x deserializes `payload` as a flat `.NET` `string`; handed a
  JSON object or array in that position, its (de)serializer throws rather than silently
  coercing. A 1.2.0 envelope whose payload happens to be a plain JSON string remains readable by
  1.1.x, since that's byte-identical to what 1.1.x itself would have produced.
- This is why `CurrentProtocolVersion` was bumped from `1` to `2` in B3 — see
  [Version negotiation](#version-negotiation).

### Reserved fields: `cid`, `type`, `error` (v1.2.0 adds the shape, not the behavior)

v1.2.0 adds these three fields to `MessageEnvelope` purely so a future request/response layer
(planned for v1.3.0) does not require another breaking wire-format change. **No HRpc code in
v1.2.0 generates, matches on, dispatches on, or otherwise interprets any of these fields.** They
exist on the type and on the wire; that is all.

- **`cid`** (`MessageEnvelope.Cid`, `string?`) — a correlation id. Absent on ordinary
  fire-and-forget events. HRpc never auto-generates one; a future request/response layer is
  expected to set and match on it.
- **`type`** (`MessageEnvelope.Type`, `string?`) — a message-type discriminator. Known values are
  defined as documentation/convenience constants (`MessageEnvelope.MessageTypeEvent`,
  `MessageTypeRequest`, `MessageTypeResponse`, `MessageTypeErrorResponse`) but are **not**
  enforced: `Deserialize` does not validate this field against any allow-list. **An absent or
  unrecognized `type` value is never fatal** — it deserializes into `Type` as-is (or stays `null`
  if absent) and is not otherwise acted upon. What (if anything) a future version does with an
  unrecognized value is a decision deferred entirely to whichever version implements
  request/response.
- **`error`** (`MessageEnvelope.Error`, `MessageError?`) — an error payload with `code` and
  `message` string fields (`MessageError.Code` / `MessageError.Message`, JSON `"code"` /
  `"message"`). Fields only; no propagation, throwing, or surfacing logic exists in v1.2.0.

All three are optional and **absent-by-default on the wire**: each is `null` unless explicitly
set, and both serializers are configured to omit the key entirely when the value is `null`
(`NullValueHandling.Ignore` for Newtonsoft, `[JsonIgnore(Condition = JsonIgnoreCondition.
WhenWritingNull)]` for STJ). A plain `new MessageEnvelope(eventName, payload)` therefore still
serializes to exactly `{"v":2,"eventName":"...","payload":...}` with no `cid`/`type`/`error`
keys present — a 1.2.0 fire-and-forget message carries no more bytes for these fields than a
1.1.x message did, `v` (added in B1) and the payload representation change (B3) aside. Neither
`MessageEnvelope` constructor stamps or mutates `Cid`, `Type`, or `Error` — they stay `null`
unless a caller sets them explicitly, consistent with the `Serialize()`-never-mutates invariant
documented above.

### Unknown fields are ignored

A receiver MUST ignore any JSON field it does not recognize, rather than treating it as
an error. This is what lets the schema grow in later protocol versions (for example, a
future correlation id or message-type field) without breaking older peers that don't
know about the new field. Both serializers HRpc uses honor this by default with no
extra configuration:

- `System.Text.Json.JsonSerializer.Deserialize` ignores unmapped members by default.
- `Newtonsoft.Json.JsonConvert.DeserializeObject` ignores unmapped members by default
  (`MissingMemberHandling.Ignore`).

Neither serializer is configured to change this default — do not add
`UnmappedMemberHandling.Disallow` (STJ) or `MissingMemberHandling.Error` (Newtonsoft) to
the envelope's (de)serialization path without updating this document, since doing so
would turn every future additive protocol change into a breaking one.

### Version negotiation

The `v` field identifies the envelope schema version the sender used. Three distinct
constants govern this, each with a different job — do not collapse them into one:

| Constant                   | Meaning                                          | Value |
|-----------------------------|---------------------------------------------------|-------|
| `CurrentProtocolVersion`    | The version this build *emits* for a brand new outgoing envelope. Stamped by the `MessageEnvelope(string, object?)` constructor, not by `Serialize()` — `Serialize()` never mutates or overrides `Version` (see its doc comment); this matters for a receive-then-forward path, where re-serializing a received envelope must preserve its original version rather than relabeling it. Increments over time. | `2` |
| `LegacyProtocolVersion`     | The version assumed when `v` is *absent* on receive. Identifies one specific historical wire shape — every HRpc release before v1.2.0, none of which ever emitted `v`, and whose `payload` is guaranteed to be a JSON string — and must **never** change, no matter how high `CurrentProtocolVersion` climbs. | `1`, permanently |
| `MinimumSupportedVersion`   | The oldest version this build still *accepts* on receive. | `1` |

`LegacyProtocolVersion` and `MinimumSupportedVersion` both stay at `1`; they answer
different questions and will diverge from `CurrentProtocolVersion` (and potentially from
each other) as the protocol evolves — a version bump does not retroactively change what an
absent `v` field means, and does not necessarily drop support for the version being
superseded. **B3 (v1.2.0) is the first field-level exercise of this three-constant model
in a genuine breaking-change scenario**: the payload representation change (see
[Payload representation](#payload-representation-b3) above) is not purely additive — a
1.1.x peer cannot read a non-string payload — so `CurrentProtocolVersion` moved to `2`
while `LegacyProtocolVersion`/`MinimumSupportedVersion` correctly stayed at `1`, keeping a
v-absent or `v:1` envelope (guaranteed string payload) fully readable exactly as before.

- **Missing `v` field**: treated as `LegacyProtocolVersion` (`1`). A 1.1.x sender talking
  to a 1.2.0+ receiver must keep working exactly as before, forever, regardless of how
  many times `CurrentProtocolVersion` has incremented since.
- **`v` between `MinimumSupportedVersion` and `CurrentProtocolVersion` inclusive**:
  processed normally.
- **`v` above `CurrentProtocolVersion`, or below `MinimumSupportedVersion` (including
  `0` and negative values)**: `ErrorOccurred` fires with an
  `UnsupportedProtocolVersionException`, and **the connection is dropped** — the message
  is not skipped, and the loop does not attempt to keep reading. A version bump is
  reserved for changes that are not purely additive (see
  [Unknown fields are ignored](#unknown-fields-are-ignored) above — purely additive
  changes don't need a version bump at all), so once a peer sends a version this build
  doesn't recognize as valid, there is no way to know whether the *framing* itself has
  changed underneath the still-recognized fields. Continuing to read from the stream at
  that point risks misinterpreting the bytes that follow, so the whole connection is
  closed rather than attempting to skip only the offending message. This mirrors how an
  oversized message (see above) is handled: abort, don't try to resynchronize.

All three constants are defined once, on
`HRpc.Models.MessageEnvelope`.

## Serializer parity (STJ / Newtonsoft)

`MessageEnvelope` is serialized with `System.Text.Json` on net8.0/net9.0 and with
`Newtonsoft.Json` on net48 (`#if NETFRAMEWORK`). Every envelope field carries an explicit
`[JsonPropertyName]` (STJ) / `[JsonProperty]` (Newtonsoft) attribute pair with the same
literal name, so both serializers produce and accept byte-identical JSON for the same
values. As of D1, `HRpc.Tests` multi-targets net48;net8.0;net9.0 and CI runs the full
suite on both a Windows runner (net48/Newtonsoft) and a Linux runner (net8.0/net9.0/STJ),
so this is verified directly by the same test source running against both serializers
(see `Tests/UnitTests.cs`, the `MessageEnvelope_*` tests) — not simulated via a
hand-written fixture standing in for a serializer the test host couldn't run. Must be
preserved for any new field: **always add both attributes together, with the same name,
for every envelope field.** A field with only one of the two attributes silently uses a
different wire name (or PascalCase) on the other target framework, breaking
cross-runtime compatibility — before D1 no test host could catch this locally; now the
net48 CI leg does. This applies to the nested `MessageError` type (the `error`
field's payload) exactly as it does to `MessageEnvelope` itself: `Code` and `Message` each carry
paired `[JsonPropertyName]`/`[JsonProperty]` attributes with matching literal names (`"code"`,
`"message"`).

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
via `SendAsync`. The server writes back to a client in only two cases:
`PipeServer.InitialMessage` (a single envelope sent once, immediately after a client
connects, before that client's own messages are read) and the `response`/`errorResponse`
to a request (see [Request/response](#requestresponse-v130)). There is no unsolicited
server-to-client push yet; it is planned for 1.4.0 (see [ROADMAP.md](ROADMAP.md)).

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
| `cid`       | string                   | No       | Correlation id. Set by `SendAndWaitAsync` on a request and echoed back by the matching `response`/`errorResponse`. See [Request/response](#requestresponse-v130) below. |
| `type`      | string                   | No       | Message-type discriminator: `event` (default/absent), `request`, `response`, `errorResponse`, and since v1.4.0 `hello` and `cancel`. See [Request/response](#requestresponse-v130) and [Capabilities and cancellation](#capabilities-and-cancellation-v140) below. |
| `error`     | object                   | No       | Error payload (`code` + `message`), present on `errorResponse` only. See [Request/response](#requestresponse-v130) below. |
| `caps`      | array of strings         | No       | Capabilities the sender supports (v1.4.0+). Sent by a client on its first request and by a server in its `hello`. See [Capabilities and cancellation](#capabilities-and-cancellation-v140) below. |

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

### Request/response (v1.3.0)

`cid`, `type`, and `error` were reserved (shape-only, unused) in v1.2.0 specifically for this
layer. As of v1.3.0, HRpc implements a correlated request/response pattern on top of the plain
event envelope, without changing framing, version negotiation, or the public `IEventMessage`
payload surface at all.

- **`cid`** (`MessageEnvelope.Cid`, `string?`) — a correlation id. `Connection.SendAndWaitAsync`
  (and the underlying `TcpConnection`/`PipeConnection.SendAndWaitAsync`) generates one via
  `Guid.NewGuid().ToString("N")` per call and stamps it on the outgoing `request` envelope. The
  peer's `response`/`errorResponse` echoes the same `cid` back unchanged so the caller can match
  it to the pending call. Absent on ordinary fire-and-forget events — `SendAsync` never sets it.
- **`type`** (`MessageEnvelope.Type`, `string?`) — a message-type discriminator with four known
  values, exposed as constants on `MessageEnvelope`: `MessageTypeEvent` ("event", the implicit
  default — absent/`null` on ordinary messages), `MessageTypeRequest` ("request", set by
  `SendAndWaitAsync`), `MessageTypeResponse` ("response", set by a handler's successful reply),
  `MessageTypeErrorResponse` ("errorResponse", set when a handler throws). `Deserialize` still
  does not validate this field against an allow-list — an absent or unrecognized `type` is never
  fatal, and a receiver that doesn't recognize request/response semantics simply sees these as
  ordinary events with extra ignored fields (see
  [Unknown fields are ignored](#unknown-fields-are-ignored)).
- **`error`** (`MessageEnvelope.Error`, `MessageError?`) — present only on `errorResponse`
  envelopes. `code` is the throwing handler's exception type name (`ex.GetType().Name`), and
  `message` is `ex.Message`. On the client, `SendAndWaitAsync` surfaces this as a thrown
  `RequestFailedException` with matching `Code`/`Message` properties.

#### Server: registering a handler

`TcpServer.OnRequest(eventName, handler)` / `PipeServer.OnRequest(eventName, handler)` (and the
`Server` facade's `OnRequest`, which works whether called before or after `StartAsync`) register
an `async Func<IEventMessage, Task<object?>>` for one `eventName`. When an incoming envelope has
`type: "request"` and a `cid`, and its `eventName` has a registered handler, the handler is
invoked and its return value becomes the `response` envelope's payload — written directly back on
that same client's connection, never broadcast. A `request`-typed envelope for an `eventName`
with **no** registered handler falls through to `MessageReceived` exactly like any other message;
request/response is additive to the pub/sub surface, not a replacement for it. A handler that
throws does not propagate to `ErrorOccurred` or close the connection — it is translated into an
`errorResponse` envelope instead, consistent with the "one bad message shouldn't kill the
connection" philosophy in [Error handling on receive](#error-handling-on-receive) above.

Since v1.4.0 there is a second overload, `OnRequest(eventName, (message, context) => ...)`. Its
`RequestContext` carries the calling client's `ClientId` and a `CancellationToken` that is
cancelled when the client cancels the request, disconnects, or the server stops. See
[Capabilities and cancellation](#capabilities-and-cancellation-v140). The one-parameter overload
still works and behaves as before.

#### Client: `SendAndWaitAsync`

`Connection.SendAndWaitAsync(message, timeout, cancellationToken = default)` (and the
`TcpConnection`/`PipeConnection` methods it delegates to) sends `message` as a `request` envelope
with a freshly generated `cid`, and returns a `Task<IEventMessage>` that completes when a matching
`response`/`errorResponse` envelope arrives on the same connection. Behavior:

- **Success** (`response`): the task completes with an `IEventMessage` wrapping the handler's
  return value, readable via the normal `GetPayload<T>()`/`GetPayloadAsString()` accessors.
- **Handler threw** (`errorResponse`): the task faults with `RequestFailedException`, whose
  `Message` is the handler exception's message and whose `Code` is its type name.
- **Timeout**: if no matching response arrives within `timeout`, the task faults with
  `TimeoutException`. Distinguished internally from a caller-supplied `cancellationToken`
  cancellation (which instead propagates as `OperationCanceledException`/`TaskCanceledException`)
  via a linked `CancellationTokenSource`. Since v1.4.0, a response that arrives after the
  timeout or cancellation is discarded (see
  [Late responses are dropped](#late-responses-are-dropped)), and a 1.4+ server is told to
  cancel the request.
- **Connection drops while pending**: every still-pending `SendAndWaitAsync` call on that
  connection faults with `InvalidOperationException("Connection closed while awaiting a
  response.")`, so a dropped connection never leaves a caller awaiting forever.

Response envelopes are intercepted in the normal receive loop before `MessageReceived` fires —
matched by `cid` against a pending-calls table — so a `response`/`errorResponse` is never also
delivered as an ordinary event.

#### Wire example

```json
{"v":2,"eventName":"Add","type":"request","cid":"3f9a...","payload":[2,3]}
{"v":2,"eventName":"Add","type":"response","cid":"3f9a...","payload":5}
```

```json
{"v":2,"eventName":"Explode","type":"request","cid":"7b21...","payload":"x"}
{"v":2,"eventName":"Explode","type":"errorResponse","cid":"7b21...","error":{"code":"InvalidOperationException","message":"boom"}}
```

All three fields remain optional and **absent-by-default on the wire** for ordinary events: each
is `null` unless explicitly set, and both serializers omit the key entirely when the value is
`null` (`NullValueHandling.Ignore` for Newtonsoft, `[JsonIgnore(Condition = JsonIgnoreCondition.
WhenWritingNull)]` for STJ). A plain `new MessageEnvelope(eventName, payload)` still serializes to
exactly `{"v":2,"eventName":"...","payload":...}` with no `cid`/`type`/`error` keys present —
request/response adds no bytes to fire-and-forget traffic that doesn't use it.

### Capabilities and cancellation (v1.4.0)

1.4.0 lets a client cancel a request it no longer wants, without changing anything a 1.3
peer can see. It adds two message types and one field. All of it is opt-in by negotiation,
so a 1.4 peer talking to a 1.3 peer behaves exactly like two 1.3 peers.

**Negotiation.**

1. A 1.4 client adds `"caps":["cancel"]` to its outgoing `request` envelopes until one of
   them has been written successfully. In practice that is only the first request. Plain
   events never carry `caps`, so fire-and-forget traffic is byte-for-byte what 1.3 sends.
   A 1.3 server ignores the unknown field (see
   [Unknown fields are ignored](#unknown-fields-are-ignored)).

   ```json
   {"v":2,"eventName":"Add","payload":[2,3],"cid":"3f9a...","type":"request","caps":["cancel"]}
   ```
2. A 1.4 server that sees `caps` on a request writes one `hello` envelope back before it
   handles that request:

   ```json
   {"v":2,"eventName":"","payload":null,"type":"hello","caps":["cancel"]}
   ```

   A server never sends `hello` to a client that did not advertise `caps`, so a 1.3 client
   never receives one.
3. A 1.4 client that receives `hello` records the server's capabilities. `hello` is never
   raised as `MessageReceived`.

**Cancel.** When a `SendAndWaitAsync` call times out or its `CancellationToken` fires, and
only if the server has said `hello` with `cancel`, the client sends:

```json
{"v":2,"eventName":"Add","payload":null,"cid":"3f9a...","type":"cancel"}
```

This is best-effort and fire-and-forget: a write failure is ignored, and the caller has
already received its `TimeoutException` or `TaskCanceledException`. A client never sends
`cancel` to a server that did not say `hello`, because a 1.3 server would raise it as an
ordinary `MessageReceived` event.

On the server, `cancel` is handled as soon as it is read, without waiting behind queued
messages:

- If the request is still queued, its handler never runs.
- If the handler is running, the `CancellationToken` on its `RequestContext` is cancelled.
- A cancelled request gets no `response` or `errorResponse`. If the handler throws
  `OperationCanceledException` while its token is cancelled, nothing is sent. An
  `OperationCanceledException` the handler throws for its own reasons, with the token not
  cancelled, still becomes an `errorResponse` as in 1.3.
- A `cancel` for an unknown or finished `cid` is ignored. `cancel` is never raised as
  `MessageReceived`.

The handler token is also cancelled when the client disconnects or the server stops.

### Late responses are dropped

A client remembers the `cid`s of its last 1024 requests that timed out or were cancelled.
A `response` or `errorResponse` that later arrives for one of them is discarded. Before
1.4.0, it was raised as `MessageReceived`. A response with a `cid` the client never sent
still falls through to `MessageReceived`, as in 1.3.

### Ordering and concurrency on the server

By default (`MaxConcurrentRequests = 1`), a server processes each client's messages one at a
time, in the order they arrived. It awaits each request handler before it looks at the next
message. This is the 1.3 behavior: an event sent after a request is delivered only after that
request's handler has finished.

With `MaxConcurrentRequests` set above 1, up to that many request handlers per client run at
once. Events are still raised in arrival order, but no longer wait for running handlers, and
responses are written as handlers finish, so they may come back in a different order than the
requests. Clients match responses by `cid`, so this is invisible to `SendAndWaitAsync`
callers. The setting is per server and changes nothing on the wire.

Each client's unprocessed messages are buffered in a bounded queue of 64. When it is full,
the server stops reading from that client until there is room. TCP or pipe backpressure then
slows the sender instead of the server buffering without limit.

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

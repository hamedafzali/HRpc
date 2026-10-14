# HRpc Roadmap

Where HRpc is heading. This is a plan, not a promise: priorities can change, and nothing here
ships until it appears in [`CHANGELOG.md`](../CHANGELOG.md).

Guiding rules:

- **Stay small.** HRpc is newline-delimited JSON over a stream. New features build on that
  envelope; they don't replace it.
- **Non-breaking by default.** Everything up to 2.0 is additive. Public API is removed only
  in a major version (see [Versioning](RELEASING.md#versioning)).
- **Transports share one core.** A new transport should only supply "open a stream". Framing,
  request/response and error handling are written once.

## Shipped

| Version | What |
|---------|------|
| 1.2.0 | Typed JSON payloads (protocol v2), named-pipe transport, recoverable parse errors, max message size, `net48`/`net8.0`/`net9.0`. |
| 1.3.0 | Correlated request/response: `OnRequest` on servers, `SendAndWaitAsync` on connections. |
| 1.3.1 | Writes serialized per connection (fixes frame corruption under concurrent sends). `Logger` marked `[Obsolete]`. CI on Node 24 actions. |

## 1.4.0: core cleanup and server push (non-breaking)

1. **Shared stream-transport base class.** `TcpConnection`/`PipeConnection` and
   `TcpServer`/`PipeServer` are about 85% identical. Move the receive loop, framing, write lock,
   request/response correlation and error handling into one internal base. Every later
   transport depends on this.
2. **Server push.**
   - Client IDs on `ClientConnected` and `MessageReceived`.
   - `SendAsync(clientId, message)` and `BroadcastAsync(message)` on servers.
   - A per-client write lock (same fix as 1.3.1, on the server side).

   This removes the current "no server-to-client channel beyond `PipeServer.InitialMessage`"
   limit.
3. **Run `OnRequest` handlers off the receive loop.** Today one slow handler blocks every other
   message on that connection (head-of-line blocking).
4. **Optional heartbeat / dead-peer detection.** A configurable ping interval and timeout, so a
   silently dropped peer is noticed without waiting for a write to fail.

## 1.5.0: new transports

| Transport | Why | Notes |
|-----------|-----|-------|
| **Stdio** | Talk to a child process over stdin/stdout. Cheap on top of the 1.4 base. | Same model MCP servers use for local tools. |
| **WebSocket** | Browser and cross-network clients, proxy friendly. | Server: `HttpListener`. Client: `ClientWebSocket`. No extra dependencies. |
| **TLS for TCP** | Encryption without an external tunnel. | `SslStream` wrapped around the existing TCP stream. |

Lower priority: an in-memory transport for tests. Unix domain sockets add little over named
pipes plus TCP, so they're not planned.

## 1.6.0: AI integration

1. **Streaming responses.** A request can get several response chunks under one `cid`, read on
   the caller's side as an `IAsyncEnumerable`. Useful for LLM token streams, progress
   reporting and large result sets.
2. **`HRpc.Mcp`, a separate package.** It exposes `OnRequest` handlers as
   [Model Context Protocol](https://modelcontextprotocol.io) tools, built on the official C# MCP
   SDK. That lets an AI assistant call into an HRpc app.
   - MCP is JSON-RPC 2.0, so HRpc's own wire format can't simply "be" MCP. A bridge package
     keeps the core protocol unchanged and free of dependencies.

HRpc will **not** embed an LLM client. Calling a model is the application's job.

## 2.0: breaking cleanup (only if needed)

Bundled into one major release, and only once there's enough to justify it:

- Remove `Utils.Logger` (obsolete since 1.3.1).
- Fix interface naming: `ITcpServer` is used for pipes too, and `IPipeConnection` inherits
  `ITcpConnection`.
- Add the 1.3–1.6 methods (`SendAndWaitAsync`, `OnRequest`, push) to the interfaces. Until
  then they live only on the concrete classes, to avoid breaking existing implementers.

## Known gaps (not yet scheduled)

- **Authentication.** There is no handshake or credential check. Run behind a trusted boundary.
- **Auto-reconnect.** A dropped client stays dropped. The application must reconnect.
- **Encryption.** None until TLS lands in 1.5. Use a TLS tunnel for anything internet-facing.
- **Replay protection.** None, and none planned at the library level.

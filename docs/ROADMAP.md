# HRpc Roadmap

Where HRpc is heading, and why. This is a plan, not a promise: priorities can change, and
nothing here ships until it appears in [`CHANGELOG.md`](../CHANGELOG.md).

_Last reviewed: 2026-10-17._

## Where HRpc fits

There are mature options for .NET inter-process communication. HRpc should not try to beat
them at their own game. It should be the best choice in its own niche.

| Option | Strengths | Costs | Pick it when |
|--------|-----------|-------|--------------|
| **gRPC** over UDS / named pipes ([built into ASP.NET Core, .NET 8+](https://learn.microsoft.com/aspnet/core/grpc/interprocess)) | Contracts, codegen, streaming, every language | `.proto` files, codegen, ASP.NET Core host, no `net48` server | Large systems with a strict schema |
| **[StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc)** (JSON-RPC 2.0) | Standard protocol, method proxies, cancellation, MessagePack | Method-call model, more concepts to learn | Calling methods on another process |
| **[H.Pipes](https://github.com/HavenDV/H.Pipes)** | Typed messages, multi-client pipe server | Named pipes only. Defaults to `BinaryFormatter`. | Windows desktop, .NET on both ends |
| **HRpc** | Events and request/response, one dependency-free package, `net48` to `net10`, a wire format any language can speak in about 20 lines | Smaller feature set, younger ecosystem | Simple messaging between local processes, including non-.NET ones |

**HRpc's niche:** the simplest way to connect a .NET process to anything else (another .NET
app, a Python script, an Electron/Tauri UI, an AI agent) with plain newline-delimited JSON
over TCP, pipes, stdio or WebSockets. The plan below reinforces that niche:

1. **Run everywhere .NET runs.** Current target frameworks (`net10.0` since 1.3.2), all three
   desktop OSes, AOT.
2. **Speak to everything.** A language-neutral protocol spec plus small reference clients.
3. **Fill the gaps users hit first:** server push, cancellation, security defaults.

## Shipped

| Version | What |
|---------|------|
| 1.2.0 | Typed JSON payloads (protocol v2), named-pipe transport, recoverable parse errors, max message size, `net48`/`net8.0`/`net9.0`. |
| 1.3.0 | Correlated request/response: `OnRequest` on servers, `SendAndWaitAsync` on connections. |
| 1.3.1 | Writes serialized per connection (fixes frame corruption under concurrent sends). `Logger` marked `[Obsolete]`. CI on Node 24 actions. |
| 1.3.2 | `net10.0` target, ahead of .NET 8/9 end of support on 2026-11-10. CI on Linux **and macOS** for every modern target. |
| 1.4.0 | Server push (`SendAsync`/`BroadcastAsync`, client ids), request cancellation with a `RequestContext` token, `MaxConcurrentRequests`, late replies dropped, TCP `KeepAlive`, `CurrentUserOnly` pipes, a shared internal transport core, and a fix for the `PipeServer` idle CPU/memory leak. Wire-compatible with 1.3 in both directions, tested against the released 1.3.2 binary. |

## Left over from 1.4.0

- **Application-level heartbeat.** 1.4.0 shipped TCP keepalive (`KeepAlive`) instead: it
  needs no protocol change and works against any peer. A ping/pong envelope would also cover
  named pipes and half-open proxies. Reconsider it together with auto-reconnect.
- **Drop `net9.0` from the package** (optional). .NET 9 apps fall back to the `net8.0`
  build, so this only trims the package. Deferred so 1.4.0 changes no install surface.
- **Assembly version.** The package sets `GenerateAssemblyInfo=false`, so `HRpc.dll` reports
  version 0.0.0.0 in every release so far. Stamp the real version once it is clear no
  consumer depends on 0.0.0.0 (for example in a binding redirect).

## 1.5.0: transports and AOT

| Item | Why | Notes |
|------|-----|-------|
| **Stdio transport** | Talk to a child process over stdin/stdout. No ports, no pipe names, no firewall prompts. | The standard way editors, tools and MCP servers talk to helper processes. Also the easiest way for an Electron/Tauri app to run a .NET "sidecar" process. |
| **WebSocket transport** | The only option in browsers (Blazor WebAssembly, web UIs). Works through proxies and across networks. | Server: `HttpListener`. Client: `ClientWebSocket`. No extra dependencies. |
| **Unix domain socket transport** | An explicit, choosable socket path (e.g. under `$XDG_RUNTIME_DIR`). Python, Node, Go and Rust connect natively. Microsoft recommends UDS for cross-platform IPC. | Supported on Windows 10+ too. Complements, rather than replaces, named pipes. |
| **TLS for TCP** | Encryption without an external tunnel. | `SslStream` around the existing TCP stream. |
| **Native AOT / trimming support** | AOT is now common for CLI tools, sidecars and MCP servers. | Today `MessageEnvelope` is serialized with reflection-based System.Text.Json. Use a source-generated `JsonSerializerContext` internally. Add `JsonTypeInfo<T>` overloads for payloads. Set `IsAotCompatible` on `net8.0+` builds. |

Lower priority: an in-memory transport for tests.

## 1.6.0: cross-language support

Any language can parse newline-delimited JSON. The missing piece is making that official.

1. **A language-neutral protocol spec.** Split the wire rules out of
   [`PROTOCOL.md`](PROTOCOL.md), which also covers .NET API behavior, into a short spec:
   framing, envelope fields, request/response, cancel, errors.
2. **Conformance test vectors.** A folder of example messages and expected handling (valid,
   malformed, oversized, unknown fields) that other implementations can test against.
3. **Reference clients** for **Python** and **TypeScript/Node**, each a small package of a few
   hundred lines, kept in this repo and tested in CI against the .NET server. These cover
   the most common cases: Python scripts and ML tooling, and Electron or web UIs.

## 1.7.0: AI integration

1. **Streaming responses.** A request can get several response chunks under one `cid`, read
   on the caller's side as an `IAsyncEnumerable`. Useful for LLM token streams, progress
   reporting and large results. Builds on the 1.4 cancellation work.
2. **`HRpc.Mcp`, a separate package.** It exposes `OnRequest` handlers as
   [Model Context Protocol](https://modelcontextprotocol.io) tools, built on the official
   [C# MCP SDK](https://csharp.sdk.modelcontextprotocol.io/). The SDK's transports are stdio
   (local) and Streamable HTTP (remote). This lets AI assistants like Claude, Copilot and
   Cursor call into an HRpc application.
   - MCP is JSON-RPC 2.0, so HRpc's own wire format can't simply "be" MCP. A bridge package
     keeps the core protocol unchanged and free of dependencies.
   - The bridge is net8.0+ only, matching the SDK.

## 2.0: breaking cleanup (only when there's enough to justify it)

- Remove `Utils.Logger` (obsolete since 1.3.1).
- Fix interface naming: `ITcpServer` is used for pipes too, and `IPipeConnection` inherits
  `ITcpConnection`.
- Add the 1.3–1.7 methods (`SendAndWaitAsync`, `OnRequest`, push) to the interfaces.
- Make pipes `CurrentUserOnly` by default (secure by default).
- Re-evaluate `net48` + Newtonsoft versus `netstandard2.0` + System.Text.Json. One serializer
  everywhere would remove a whole class of behavior differences between `net48` and `net8.0+`.
  It could also make HRpc usable from Unity (see below).

## Cross-platform notes

How HRpc behaves on each platform today, and what the plan changes.

| Platform | TCP | Named pipes | Notes / plan |
|----------|-----|-------------|--------------|
| **Windows** (`net48`, `net8+`) | ✅ | ✅ Native Windows pipes | `net48` tested in CI. `net8+` builds are tested on Linux/macOS. |
| **Linux** (`net8+`) | ✅ | ✅ Emulated (see below) | Tested in CI. |
| **macOS** (`net8+`) | ✅ | ✅ Emulated, short-path caveat | Tested in CI since 1.3.2. |
| **Browser / Blazor WASM** | ❌ No raw sockets | ❌ | Needs the WebSocket transport (1.5). |
| **Android / iOS (MAUI)** | ✅ Probably | Not useful | Untested. TCP to a desktop or server is the realistic use. |
| **Unity** | Untested | Untested | IL2CPP builds have reported `NamedPipeServerStream` failures. Revisit with the 2.0 `netstandard2.0` question. |
| **Non-.NET peers** | ✅ | ⚠️ See below | Any language can speak the wire format today. Reference clients and a UDS transport make it first-class (1.5–1.6). |

**Named pipes outside Windows.** On Linux and macOS, .NET implements
`NamedPipeServerStream`/`NamedPipeClientStream` on top of Unix domain sockets. The socket file
is created at `/tmp/<pipeName>`
([runtime source](https://source.dot.net/dotnet/NamedPipeUtil.cs.html)). Consequences:

- A non-.NET process must connect to the Unix socket `/tmp/<pipeName>`. It is not a FIFO
  (`mkfifo`) and not a Windows-style `\\.\pipe\` path.
- macOS has a short socket-path limit (104 bytes), so keep pipe names short.
- `PipeSecurity` ACLs are Windows-only. On Unix, access is controlled by the socket file's
  permissions, which is another reason for the `CurrentUserOnly` option.
- Pipes are same-machine only on every OS. HRpc does not support remote Windows pipes over
  SMB, by design. Use TCP, TLS or WebSocket across machines.

## Considered, not planned

| Idea | Why not |
|------|---------|
| **Embedding an LLM client** | Calling a model is the application's job. HRpc carries the messages; `HRpc.Mcp` covers the AI-tool use case. |
| **Full JSON-RPC 2.0 compatibility mode** | StreamJsonRpc already does this well. HRpc's value is its simpler event-plus-request model. |
| **MessagePack / binary payloads** | Would break "any language in 20 lines". Revisit only with measured demand. |
| **Remote Windows named pipes (SMB)** | Windows-only and firewall-hostile. TCP+TLS or WebSocket covers it. |
| **Service discovery / message broker** | Out of scope. Use a real broker (RabbitMQ, NATS, Redis) for many-to-many messaging. |

## Known gaps (not yet scheduled)

- **Authentication.** No handshake or credential check. Run behind a trusted boundary.
  `CurrentUserOnly` (pipes) and TLS (TCP) cover the local and transport parts. A token
  handshake is a candidate for after 1.5.
- **Auto-reconnect.** A dropped client stays dropped. The application must reconnect.
  Since 1.4.0, `KeepAlive` makes a silent TCP drop raise `Disconnected`, which is the
  signal a reconnect loop needs. Built-in reconnect is a candidate for 1.5.
- **Replay protection.** None, and none planned at the library level.

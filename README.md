# Cirreum Invocation WebSockets

[![NuGet Version](https://img.shields.io/nuget/v/Cirreum.Invocation.WebSockets.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Invocation.WebSockets/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cirreum.Invocation.WebSockets.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Invocation.WebSockets/)
[![GitHub Release](https://img.shields.io/github/v/release/cirreum/Cirreum.Invocation.WebSockets?style=flat-square&labelColor=1F1F1F&color=FF3B2E)](https://github.com/cirreum/Cirreum.Invocation.WebSockets/releases)
[![License](https://img.shields.io/github/license/cirreum/Cirreum.Invocation.WebSockets?style=flat-square&labelColor=1F1F1F&color=F2F2F2)](https://github.com/cirreum/Cirreum.Invocation.WebSockets/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-003D8F?style=flat-square&labelColor=1F1F1F)](https://dotnet.microsoft.com/)

**Raw WebSocket invocation source for the Cirreum Invocation provider family.**

## Overview

`Cirreum.Invocation.WebSockets` is the L3 Infrastructure package that surfaces raw WebSocket connections as a registered invocation source within the Cirreum framework. It supplies a concrete `InvocationProviderRegistrar` that:

- Maps WebSocket endpoints from configuration (`Cirreum:Invocation:Providers:WebSocket:Instances:{key}`).
- Drives each connection through a `WebSocketOrchestrator` that publishes `IInvocationContext` per WebSocket message — putting raw WebSocket on the same unified seam as HTTP and SignalR.
- Supports a two-phase connection model: an optional companion HTTP request endpoint (e.g. Twilio incoming-call webhook) that initiates the WebSocket flow, plus the WebSocket endpoint itself.
- Wires up the per-instance auth scheme via `RequireAuthorization()` when one is configured.

Apps do **not** reference this package directly — they install the matching L5 Runtime Extensions package (`Cirreum.Runtime.Invocation.WebSockets`) which exposes the `AddWebSocketInvocation()`, `AddWebSocket<THandler>()`, and `MapWebSocketInvocation()` extensions and pulls this package in transitively.

## Architectural position

```
L2 Core
  Cirreum.InvocationProvider               ← abstractions: IInvocationContext, registrar base, ...

L3 Infrastructure
  Cirreum.Invocation.SignalR               ← peer for SignalR Hubs
  Cirreum.Invocation.WebSockets            ← THIS PACKAGE — registrar, orchestrator, settings

L4 Runtime
  Cirreum.Runtime.InvocationProvider       ← IInvocationBuilder seam, AddInvocation, RegisterInvocationProvider

L5 Runtime Extensions
  Cirreum.Runtime.Invocation.WebSockets    ← AddWebSocketInvocation + AddWebSocket<THandler> + MapWebSocketInvocation
```

## What's in the box

| Type | Role |
|---|---|
| `WebSocketHandler` (`Cirreum.Invocation.WebSockets`) | Public abstract base class for app-defined message handlers — lifecycle hooks `OnAcceptAsync`, `OnSelectSubProtocolAsync`, `OnConnectedAsync`, `OnMessageAsync(IInvocationContext, ...)`, `OnDisconnectedAsync(DisconnectInfo, CancellationToken)`. Two handler-bound typed `SendAsync` overloads forward to `Connection.SendAsync` (no accessor lookup). Resolved as scoped — one instance per connection |
| `IWebSocketConnection` (`Cirreum.Invocation.WebSockets`) | Public interface — extends `IInvocationConnection` with `SendBytesAsync(ReadOnlyMemory<byte>, WebSocketMessageType, CancellationToken)` for raw frame writes (binary protocols, audio chunks, pre-serialized payloads). Inside a handler, call `this.Connection.SendBytesAsync(...)` directly — `WebSocketHandler.Connection` is typed as `IWebSocketConnection`. From cross-cutting code (`accessor.Current?.Connection`), downcast to this interface when raw frame access is needed |
| `IWebSocketUrlBuilder` (`Cirreum.Invocation.WebSockets`) | Public helper for constructing absolute `wss://` URLs with auto-extracted route values from `RouteValues`, `Query`, and `Form` — implicit instance resolution via endpoint metadata when used inside a request endpoint, explicit overload otherwise |
| `WebSocketInvocationRegistrar` (`Cirreum.Invocation`) | Concrete registrar — wires the orchestrator, registers `IWebSocketUrlBuilder`, validates settings + hard caps, maps WebSocket and request endpoints with method default and authorization scheme |
| `WebSocketInvocationSettings` / `WebSocketInvocationInstanceSettings` (`Cirreum.Invocation.Configuration`) | Typed settings bound from `Cirreum:Invocation:Providers:WebSocket` — `Path`, `RequestPath`, `Scheme`, `Enabled`, `DisconnectTimeoutSeconds`, `MaxMessageSizeBytes`, `ReceiveBufferSizeBytes`, `KeepAliveInterval`, `KeepAliveTimeout` |
| `WebSocketHandlerMapping` (`Cirreum.Invocation.WebSockets`) | DI-stashed `(InstanceKey, HandlerType, RequestHandler?, RequestMethod?, ConfigureRequestRoute?)` record produced by the L5 `AddWebSocket<THandler>` extension and consumed by the registrar |
| `WebSocketInstanceMetadata` (`Cirreum.Invocation.WebSockets`) | Endpoint metadata attached to the request endpoint — enables `IWebSocketUrlBuilder.Build(HttpContext)` to resolve the active instance implicitly |
| `WebSocketOrchestrator` (`Cirreum.Invocation.WebSockets`, internal) | Per-connection driver — runs the handler lifecycle, manages the frame receive loop, creates per-message DI scopes, manages the bounded disconnect-cleanup CTS linked to `IHostApplicationLifetime.ApplicationStopping`, captures `handler.SerializerOptions` onto the `WebSocketConnection` at upgrade time, emits source-generated framework logs |
| `WebSocketConnection` (`Cirreum.Invocation.WebSockets`, internal) | `IWebSocketConnection` adapter wrapping `WebSocket` + originating HTTP context; implements `Abort()`, the typed `SendAsync<T>` overloads (using captured `JsonSerializerOptions` from the handler), and `SendBytesAsync` for raw frame writes |
| `WebSocketInvocationContext` (`Cirreum.Invocation.WebSockets`, internal) | `IInvocationContext` adapter — used both for in-flight messages and for synthetic scopes around connection lifecycle hooks; disconnect-path overload exposes the bounded cleanup CTS as `Aborted` so ambient consumers stay coherent with the explicit hook parameter |

## How registration works

The L5 `AddWebSocket<THandler>(instanceKey, request: ...)` extension does two things:

1. Stashes a `WebSocketHandlerMapping(instanceKey, typeof(THandler), requestHandler, requestMethod, configureRequestRoute)` record in DI. Each handler type can be mapped to exactly one instance key (uniqueness check throws on conflict).
2. Registers `THandler` as a scoped service — one instance per connection.

The L5 `AddWebSocketInvocation()` extension (called once per host) calls `builder.HostBuilder.RegisterInvocationProvider<WebSocketInvocationRegistrar, WebSocketInvocationSettings, WebSocketInvocationInstanceSettings>()` from L4. The L4 helper:

- Binds `Cirreum:Invocation:Providers:WebSocket` from `IConfiguration` to `WebSocketInvocationSettings`.
- Calls `registrar.Register(...)` — services phase — which:
  - Binds the entire provider settings as `IOptions<WebSocketInvocationSettings>` for `IWebSocketUrlBuilder`.
  - Registers `IWebSocketUrlBuilder` → `WebSocketUrlBuilder` (singleton, instance-aware via endpoint metadata).
  - Registers `WebSocketOrchestrator` (singleton).
  - Validates per-instance settings: hard caps on `DisconnectTimeoutSeconds` (≤ 300), `MaxMessageSizeBytes` (≤ 8 MB), `ReceiveBufferSizeBytes` (≤ 64 KB); `Path`/`RequestPath` must start with `/`.
- Stashes an `InvocationProviderMapping` in DI capturing the deferred `registrar.Map(...)` closure.

The L5 `MapWebSocketInvocation()` endpoint-mapping method calls `app.UseWebSockets()` and resolves all `InvocationProviderMapping` entries with `ProviderName == WebSocketInvocationRegistrar.ProviderKey`. The registrar's `MapSource` then, for each enabled instance:

1. Validates `RequestPath` ↔ `request:` builder pairing — throws at startup if either is set without the other.
2. Maps the WebSocket endpoint at `Path` (using `Map()` so both GET / HTTP/1.1 and CONNECT / HTTP/2+ are accepted), excludes it from OpenAPI/Swagger, applies `RequireAuthorization` when `Scheme` is set.
3. Maps the request endpoint at `RequestPath` (when configured) using `MapMethods` with the configured method (default `POST`), attaches `WebSocketInstanceMetadata` for `IWebSocketUrlBuilder`, applies `RequireAuthorization`, and invokes the app's `configure` callback against the real `RouteHandlerBuilder`.

## Configuration

```json
{
  "Cirreum": {
    "Invocation": {
      "Providers": {
        "WebSocket": {
          "Instances": {

            "media": {
              "Enabled": true,
              "Path": "/twilio/media-stream/{callSid}",
              "RequestPath": "/twilio/incoming-call",
              "Scheme": "twilio",
              "DisconnectTimeoutSeconds": 60,
              "MaxMessageSizeBytes": 65536,
              "KeepAliveInterval": "00:00:30",
              "KeepAliveTimeout": "00:00:10"
            },

            "telemetry": {
              "Enabled": true,
              "Path": "/ws/telemetry",
              "Scheme": "oidc_primary",
              "MaxMessageSizeBytes": 524288
            }

          }
        }
      }
    }
  }
}
```

| Field | Default | Hard cap | Purpose |
|---|---|---|---|
| `Enabled` | `false` | — | Per-instance gate |
| `Path` | (required) | — | WebSocket endpoint route template (supports `{name}` placeholders) |
| `RequestPath` | null | — | Optional companion HTTP endpoint that initiates the WebSocket flow |
| `Scheme` | null | — | References a configured Authorization instance; applies `RequireAuthorization` to both endpoints |
| `DisconnectTimeoutSeconds` | 30 | 300 | Cleanup budget for `OnDisconnectedAsync` hooks |
| `MaxMessageSizeBytes` | 64 KB | 8 MB | Max bytes per complete message; oversize → `MessageTooBig` close |
| `ReceiveBufferSizeBytes` | 4 KB | 64 KB | Initial pooled receive buffer per connection |
| `KeepAliveInterval` | null | — | Override `WebSocketOptions.KeepAliveInterval` (default 2 min) |
| `KeepAliveTimeout` | null | — | Override `WebSocketOptions.KeepAliveTimeout` (default 30 s) |

`Scheme` references a configured Authorization instance under `Cirreum:Authorization:Providers:*:Instances:{Scheme}`. Optional — leave unset for unauthenticated endpoints (rare).

## Two-phase connection model

Apps that need an HTTP request/response exchange before the WebSocket connection (Twilio webhooks, session creation, token issuance) configure `RequestPath` alongside `Path`. The framework maps both endpoints — `RequestPath` routes to a minimal API delegate provided via the L5 `request:` builder (defaults to `POST`), `Path` routes to the orchestrator which drives the handler lifecycle.

**Naming** — two distinct words for two distinct concepts:

- **Request** — the inbound HTTP request that *initiates* the WebSocket session (companion endpoint).
- **Upgrade** — the actual HTTP→WS protocol upgrade that happens at the WebSocket endpoint when `AcceptWebSocketAsync` runs.

## Handler lifecycle

```csharp
public abstract class WebSocketHandler {

    // Non-nullable; backed by a throwing NotEstablished sentinel during OnAcceptAsync /
    // OnSelectSubProtocolAsync, then replaced with the real connection before
    // OnConnectedAsync runs. Typed as IWebSocketConnection so handler code can call
    // SendBytesAsync directly without a cast.
    public IWebSocketConnection Connection { get; internal set; }
    public string? SubProtocol { get; internal set; }
    protected internal IDictionary<object, object?> UpgradeItems { get; }

    // Lifecycle hooks
    public virtual Task<bool> OnAcceptAsync(HttpContext context);
    public virtual Task<string?> OnSelectSubProtocolAsync(HttpContext context);
    public virtual Task OnConnectedAsync(CancellationToken cancellationToken);
    public abstract Task OnMessageAsync(IInvocationContext context, ReadOnlyMemory<byte> message, WebSocketMessageType messageType);
    public virtual Task OnDisconnectedAsync(DisconnectInfo info, CancellationToken cancellationToken);

    // Handler-bound typed push — thin forwarders to Connection.SendAsync (which uses
    // the captured SerializerOptions). Both send `Text` frames. For raw frame writes,
    // call this.Connection.SendBytesAsync directly (no cast needed).
    protected ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default);
    protected ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default);

    // JSON serializer options for the typed SendAsync overloads. Read once at upgrade
    // time and captured onto the connection — cross-cutting code reaching the connection
    // through IInvocationContextAccessor uses these options too. Override to wire in a
    // source-generated JsonTypeInfoResolver for AOT/trim-friendly apps. Cache in a
    // static readonly field.
    protected internal virtual JsonSerializerOptions SerializerOptions { get; }

}
```

**Important — `OnMessageAsync(message)` borrow semantics.** The `message` bytes are borrowed from a per-connection pooled buffer and valid only for the duration of the returned task. Synchronous use (including `await`-points inside the method) is safe. Do **not** capture `message` into `Task.Run`, fire-and-forget continuations, queues, or long-lived state — copy via `message.ToArray()` first if you need to retain the bytes. Most parsers consume the span synchronously and produce their own owned representations — no copy needed for the typical case.

**Recommended deserialization patterns:**

```csharp
// Best — typed source-generated context (AOT-safe, reflection-free, ~2-3× faster):
[JsonSerializable(typeof(TwilioEvent))]
public sealed partial class TwilioInboundJsonContext : JsonSerializerContext { }

var evt = JsonSerializer.Deserialize(message.Span, TwilioInboundJsonContext.Default.TwilioEvent);

// Good — JsonDocument scoped inside OnMessageAsync, zero buffer copies:
using var doc = JsonDocument.Parse(message);
var root = doc.RootElement;
// (use `root` synchronously here; doc is disposed when method returns)

// Also fine — typed reflection-based deserialization:
var evt = JsonSerializer.Deserialize<TwilioEvent>(message.Span, _options);

// Avoid — copying before parsing (the IVA's pre-borrow pattern); the .ToArray()
// is now redundant since JsonDocument.Parse accepts ReadOnlySpan<byte> directly:
using var doc = JsonDocument.Parse(message.ToArray());   // ← unnecessary copy
```

| Hook | When | What's available |
|---|---|---|
| `OnAcceptAsync` | Pre-accept gate at the WebSocket endpoint | `HttpContext` (return `false` to reject); populate `UpgradeItems` to flow state into `Connection.Items` |
| `OnSelectSubProtocolAsync` | After accept gate, before WebSocket accept | `HttpContext.WebSockets.WebSocketRequestedProtocols`; return one of those values or `null` |
| `OnConnectedAsync` | After accept, before the frame loop | `Connection`, `SubProtocol`; ambient `IInvocationContext` |
| `OnMessageAsync` | Per complete WebSocket message | `IInvocationContext` (per-message Items/Services/Aborted), raw bytes, `WebSocketMessageType`, per-message DI scope |
| `OnDisconnectedAsync` | After close/abort, before disposal | `DisconnectInfo` (graceful vs error), bounded cleanup `CancellationToken` (timeout or host shutdown), ambient `IInvocationContext` |

## Server-initiated push

Three flavors of push, all routing to the same calling client. Pick by audience and payload shape.

### `this.SendAsync(...)` — handler-bound, typed JSON

Two protected overloads on `WebSocketHandler`:

```csharp
protected ValueTask SendAsync<T>(T payload, CancellationToken ct = default);
protected ValueTask SendAsync<T>(string method, T payload, CancellationToken ct = default);
```

Thin forwarders to `Connection.SendAsync(...)`. Both send `WebSocketMessageType.Text` frames. JSON-serialization uses `SerializerOptions` (captured onto the connection at upgrade — override `SerializerOptions` to wire in a source-gen `JsonTypeInfoResolver`). Works from any calling context: lifecycle hooks AND handler-managed background tasks (outbound socket receive loops, timers, fire-and-forget continuations) — because it dispatches directly through the captured `Connection` rather than through the ambient `IInvocationContextAccessor`.

```csharp
public sealed class TelemetryHandler : WebSocketHandler {

    public override async Task OnMessageAsync(
        IInvocationContext context,
        ReadOnlyMemory<byte> message,
        WebSocketMessageType messageType) {

        var ack = ComputeAck(message);
        await SendAsync("Ack", ack, context.Aborted);   // ← handler-bound, no DI
    }

}
```

Throws `InvalidOperationException` when called before `Connection` is set (i.e. during `OnAcceptAsync` / `OnSelectSubProtocolAsync` — pre-accept, no socket yet).

### `IInvocationConnection.SendAsync<T>` — ambient, typed JSON (for cross-cutting code)

From cross-cutting code that doesn't know what transport invoked it — Conductor command/query handlers, validators, transport-agnostic services — reach the connection through the ambient `IInvocationContextAccessor`:

```csharp
public sealed class GenerateReportHandler(IInvocationContextAccessor accessor)
    : ICommandHandler<GenerateReportCommand> {

    public async ValueTask<Result> Handle(GenerateReportCommand cmd, CancellationToken ct) {
        var connection = accessor.Current?.Connection;
        if (connection is not null) {
            await connection.SendAsync("Progress", new { Percent = 0 }, ct);
            // ... work ...
            await connection.SendAsync("Progress", new { Percent = 100 }, ct);
        }
        return Result.Success(/* ... */);
    }

}
```

Same wire bytes as `this.SendAsync(...)` — the connection holds the captured `SerializerOptions`, so cross-cutting code automatically picks up any source-generated `JsonTypeInfoResolver` the handler configured.

### `IWebSocketConnection.SendBytesAsync` — raw frame writes (binary protocols, audio)

Call directly on `this.Connection` from inside a handler — `WebSocketHandler.Connection` is typed as `IWebSocketConnection`. Required for binary wire formats (MessagePack, Protobuf, audio/video chunks, pre-serialized payloads):

```csharp
public sealed class TwilioMediaHandler : WebSocketHandler {

    public override async Task OnMessageAsync(
        IInvocationContext context,
        ReadOnlyMemory<byte> message,
        WebSocketMessageType messageType) {

        var (audioChunk, mark) = TwilioMessage.Parse(message);

        // Typed JSON acknowledgment via the handler shortcut
        await SendAsync(mark, context.Aborted);

        // Raw bytes — straight on Connection, no cast (Connection is IWebSocketConnection)
        await this.Connection.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, context.Aborted);
    }

}
```

From cross-cutting WebSocket-aware code (which holds only the L2 `IInvocationConnection?` view), the cast IS the explicit "I'm using WebSocket-specific behavior" acknowledgment — code that goes through it cannot transport-substitute later:

```csharp
if (accessor.Current?.Connection is IWebSocketConnection ws) {
    await ws.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ct);
}
```

Transport-agnostic code stays on the L2 typed `SendAsync<T>` overloads.

### When to use which

| You're writing | Use |
|---|---|
| Typed JSON push from inside a `WebSocketHandler` (any lifecycle hook or background task) | **`this.SendAsync(payload, ct)`** / **`this.SendAsync(method, payload, ct)`** — handler shortcut |
| Typed JSON push from cross-cutting code (Conductor handlers, validators, transport-agnostic services) | **`accessor.Current?.Connection?.SendAsync(...)`** — ambient connection, transport-agnostic |
| Raw frame writes (binary protocols, audio, pre-serialized payloads) — handler internal | **`this.Connection.SendBytesAsync(...)`** — `Connection` is typed as `IWebSocketConnection`, no cast |
| Raw frame writes from cross-cutting WebSocket-aware code | **`(accessor.Current?.Connection as IWebSocketConnection)?.SendBytesAsync(...)`** — explicit downcast acknowledges WebSocket-specific behavior |
| A `BackgroundService`, hosted timer, or queue worker that pushes to specific connections | Neither — see `BACKLOG.md` "Connection registry / fan-out push" |

All three APIs push only to the *currently-executing* connection. Broadcast / group / target-by-id push for raw WebSocket is a future addition (see `BACKLOG.md`); for those patterns today, use SignalR (`Cirreum.Runtime.Invocation.SignalR`) which has them natively via `IHubContext<THub>`.

## Connection lifecycle (cross-cutting)

Implement `IConnectionLifecycle` (from `Cirreum.Invocation.Connections`) and register it in DI to receive cross-cutting `OnConnectedAsync` / `OnDisconnectedAsync` callbacks across all WebSocket connections (and other long-lived sources). The orchestrator dispatches both under a synthetic invocation scope so consumers like `IUserStateAccessor` work normally inside the callbacks. The `DisconnectInfo` parameter on `OnDisconnectedAsync` carries the disconnect circumstances populated by this adapter from the WebSocket close status:

```csharp
internal sealed class AuditConnectionLifecycle(ILogger<AuditConnectionLifecycle> logger)
    : IConnectionLifecycle {

    public ValueTask<bool> OnConnectedAsync(IInvocationConnection connection, CancellationToken ct) {
        return ValueTask.FromResult(true);
    }

    public ValueTask OnDisconnectedAsync(
        IInvocationConnection connection,
        DisconnectInfo info,
        CancellationToken ct) {

        if (info.WasGraceful) {
            logger.LogInformation("Connection {Id} closed cleanly", connection.ConnectionId);
        } else if (info.Exception is not null) {
            logger.LogWarning(info.Exception,
                "Connection {Id} aborted: {Reason}", connection.ConnectionId, info.Reason);
        }

        return ValueTask.CompletedTask;
    }

}
```

Per-transport mapping for `DisconnectInfo`: `WasGraceful = closeStatus == WebSocketCloseStatus.NormalClosure`, `Reason = closeStatusDescription`, `Exception` populated when the loop exited due to a thrown exception.

The `cancellationToken` on `OnDisconnectedAsync` is a **bounded cleanup budget** — fires on either the configured `DisconnectTimeoutSeconds` (default 30 s) or `IHostApplicationLifetime.ApplicationStopping`, whichever comes first. Pass it directly into cancellable cleanup calls (close downstream sockets, flush metrics, persist final state).

## Logging

Source-generated `[LoggerMessage]` framework logs:

| Source | Level | Event |
|---|---|---|
| `WebSocketOrchestrator` | Warning | Frame loop exception |
| `WebSocketOrchestrator` | Warning | Message size exceeded (client sent > `MaxMessageSizeBytes`) |
| `WebSocketOrchestrator` | Warning | Disconnect cleanup budget exceeded (when not host-shutdown) |
| `WebSocketOrchestrator` | Debug | Connection accepted (with handler type, remote address) |
| `WebSocketOrchestrator` | Debug | Pre-accept gate rejected (`OnAcceptAsync` returned false) |
| `WebSocketOrchestrator` | Debug | `IConnectionLifecycle` rejected upgrade |
| `WebSocketOrchestrator` | Debug | Connection closed (with graceful flag, duration) |
| `WebSocketConnection` | Warning | Graceful close failed (peer gone, faulted socket, close-handshake timeout) |

## Dependencies

- **Cirreum.InvocationProvider** `1.3.0+` — L2 abstractions (`InvocationProviderRegistrar`, `IInvocationContext`, `IInvocationContextAccessor`, `IInvocationConnection` (with typed `SendAsync<T>` and `Abort()`), `IConnectionLifecycle`, `DisconnectInfo`, etc.)
- **Microsoft.AspNetCore.App** (framework reference) — WebSocket (`Microsoft.AspNetCore.WebSockets`), endpoint routing, hosting

## Versioning

Follows [Semantic Versioning](https://semver.org/). Foundational library — major bumps are coordinated with `Cirreum.InvocationProvider` releases.

## License

MIT — see [LICENSE](LICENSE).

---

**Cirreum Foundation Framework**  
*Layered simplicity for modern .NET*

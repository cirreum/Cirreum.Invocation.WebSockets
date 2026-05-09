# Cirreum.Invocation.WebSockets 1.0.0 — raw WebSocket as a first-class invocation source

Initial release of the WebSocket invocation source for the Cirreum framework — the second L3 source adapter after `Cirreum.Invocation.SignalR`. Apps can now configure raw WebSocket endpoints under `Cirreum:Invocation:Providers:WebSocket` and receive per-frame `IInvocationContext` publication, per-connection `IInvocationConnection` materialization, `IConnectionLifecycle` callback dispatch, and `IConnectionSender` server-push — through the same unified seam SignalR uses.

Built against `Cirreum.InvocationProvider 1.2.0` (`IInvocationConnection.Abort()` contract). Pairs with `Cirreum.Runtime.Invocation.WebSockets 1.0.0` for the app-facing registration extensions.

---

## Why this release exists

`Cirreum.Invocation.SignalR 1.0.0` shipped as the first L3 source adapter, validating that a long-lived transport could be surfaced uniformly through the Invocation seam. SignalR brings a lot of machinery — protocol negotiation, transport fallback, connection groups, per-method routing — most of which is overkill for transports that just want to push and receive frames.

Raw WebSocket is the minimum-viable long-lived transport: a single bidirectional message stream, no built-in routing, no protocol negotiation. It's the right fit for:

- **Voice/audio streaming** (Twilio Media Streams, AWS Connect, browser WebRTC bridges)
- **Telemetry and metrics ingestion** (high-frequency binary frames)
- **Custom protocols** (apps that wrap their own message envelope on top)
- **Third-party webhooks that upgrade to WebSocket** (the IVA Twilio reference codebase pattern)

This release adds raw WebSocket as a peer of SignalR — same `IInvocationContext` semantics, same `IConnectionLifecycle` hooks, same `IConnectionSender` push, same registrar pattern.

---

## What's new

### Configuration shape

Mirrors SignalR's instance pattern, with one WebSocket-specific addition (`UpgradePath`):

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
              "Scheme": "twilio"
            }
          }
        }
      }
    }
  }
}
```

| Field | Role | Default |
|---|---|---|
| `Path` | WebSocket endpoint route template (supports `{name}` placeholders). | (required) |
| `RequestPath` | Optional companion HTTP endpoint that initiates the WebSocket flow (e.g. Twilio's incoming-call webhook). When set, must be paired with a `request:` builder at the L5 call site. | null |
| `Scheme` | References a configured Authorization instance — applies `RequireAuthorization` to both endpoints. | null |
| `Enabled` | Per-instance gate. | false |
| `DisconnectTimeoutSeconds` | Cleanup budget for `OnDisconnectedAsync` hooks. Hard cap: 300. | 30 |
| `MaxMessageSizeBytes` | Max bytes per complete message; oversize messages get a `MessageTooBig` close. Hard cap: 8 MB. | 64 KB |
| `ReceiveBufferSizeBytes` | Initial pooled receive buffer per connection. Hard cap: 64 KB. | 4 KB |
| `KeepAliveInterval` | Override the global `WebSocketOptions.KeepAliveInterval` — frequency of unsolicited server pings. | null (inherit) |
| `KeepAliveTimeout` | Override the global `WebSocketOptions.KeepAliveTimeout` — how long to wait for a pong before aborting the connection. | null (inherit) |

### `WebSocketHandler` — the app-defined handler base class

Apps subclass `WebSocketHandler` to receive frames. One instance is created per connection (resolved as scoped):

```csharp
public abstract class WebSocketHandler {
    public IInvocationConnection? Connection { get; internal set; }
    public string? SubProtocol { get; internal set; }
    protected internal IDictionary<object, object?> UpgradeItems { get; }

    public virtual Task<bool> OnAcceptAsync(HttpContext context);
    public virtual Task<string?> OnSelectSubProtocolAsync(HttpContext context);
    public virtual Task OnConnectedAsync(CancellationToken cancellationToken);
    public abstract Task OnMessageAsync(
        IInvocationContext context,
        ReadOnlyMemory<byte> message,
        WebSocketMessageType messageType);
    public virtual Task OnDisconnectedAsync(DisconnectInfo info, CancellationToken cancellationToken);
}
```

| Hook | When | What's available |
|---|---|---|
| `OnAcceptAsync` | Pre-accept gate on the WebSocket endpoint | `HttpContext` (return `false` to reject before accept); populate `UpgradeItems` to flow state into `Connection.Items` |
| `OnSelectSubProtocolAsync` | Subprotocol negotiation after accept gate, before accept | `HttpContext.WebSockets.WebSocketRequestedProtocols`; return one of those values or `null` |
| `OnConnectedAsync` | After accept, before the frame loop | `Connection`, `SubProtocol`; ambient `IInvocationContext` |
| `OnMessageAsync` | Per complete WebSocket message | `IInvocationContext` (per-message Items/Services/Aborted), raw bytes, `WebSocketMessageType`, per-message DI scope |
| `OnDisconnectedAsync` | After close/abort, before disposal | `DisconnectInfo` (graceful vs error), bounded cleanup `CancellationToken` (timeout or host shutdown), ambient `IInvocationContext` |

### Two-phase connection model

Apps that need an HTTP request/response exchange before the WebSocket connection (e.g. Twilio's incoming-call webhook returning TwiML) configure `RequestPath` alongside `Path`. The framework maps both endpoints — `RequestPath` routes to a minimal API delegate provided via the `request:` builder, `Path` routes to the WebSocket handler.

```csharp
builder.AddWebSocketInvocation(b => b
    .AddWebSocket<TwilioMediaHandler>("media", request: r => r
        .Map(TwilioApi.HandleRequest, m => m
            .WithName("twilio-incoming-call")
            .WithTags("twilio")
            .Produces<string>(200, "text/xml"))));
```

The `request:` builder is a slim, framework-specific minimal API surface. Two `Map` overloads:

| Overload | Default | Use |
|---|---|---|
| `Map(Delegate, configure?)` | POST | Webhook-style endpoints (Twilio, Stripe, GitHub). |
| `Map(string method, Delegate, configure?)` | (explicit) | GET-style negotiation or other rare methods. |

The optional `configure` callback hooks into the real `RouteHandlerBuilder` at endpoints-phase time, so apps can chain `WithName`, `WithTags`, `Produces<T>`, `WithOpenApi`, `RequireAuthorization`, etc. — full minimal API customization.

**Naming** — the framework uses two distinct words for two distinct concepts:
- **Request** — the inbound HTTP request that *initiates* the WebSocket session (companion endpoint).
- **Upgrade** — the actual HTTP→WS protocol upgrade that happens at the WebSocket endpoint when `AcceptWebSocketAsync` runs.

### `IWebSocketUrlBuilder` — instance-aware URL construction

For apps that need to embed the WebSocket URL in their request response (TwiML, JSON, etc.):

```csharp
public static IResult HandleRequest(
    HttpContext context,
    IWebSocketUrlBuilder urls,
    ITwilioRequestValidator validator) {

    if (!validator.ValidateRequest(context)) return Results.Unauthorized();

    // {callSid} auto-extracted from form data (case-insensitive)
    var streamUrl = urls.Build(context);

    return Results.Content($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Response>
            <Connect><Stream url="{streamUrl}" /></Connect>
        </Response>
        """, "text/xml");
}
```

Resolves `{name}` template placeholders in priority order: explicit `routeValues` → `Request.RouteValues` → `Request.Query` → `Request.Form` (when content-type is form-encoded). Names match case-insensitively. `https`→`wss`, `http`→`ws` automatic. Instance is implicit when called inside the request endpoint (resolved from `WebSocketInstanceMetadata`); explicit `Build(instanceKey, ...)` overload available for cross-context use.

### `IConnectionSender` for WebSocket — server-initiated push

Apps inject `IConnectionSender` from inside `OnMessageAsync` / `OnConnectedAsync` to push frames to the connected client. Two overloads:

- `SendAsync<T>(T payload, CancellationToken)` — JSON-serializes and sends as a Text frame
- `SendAsync<T>(string method, T payload, CancellationToken)` — wraps the payload in a `{ "method": "...", "payload": ... }` envelope for apps implementing their own method-dispatch protocol

### `WebSocketConnection.Abort()`

Implements the new `IInvocationConnection.Abort()` contract from `Cirreum.InvocationProvider 1.2.0`. Cancels the linked `CancellationTokenSource` backing `Aborted`; the frame loop's `ReceiveAsync(connection.Aborted)` throws `OperationCanceledException`, the loop exits, `OnDisconnectedAsync` runs as if the close was orderly. Critical for handlers orchestrating multiple sockets — when an outbound dependency drops, the handler can call `Connection.Abort()` to terminate the inbound transport.

### Frame loop guarantees

Per ADR-0002 transport-adapter invariants:

- **Per-message DI scope** — each complete message gets a fresh `IServiceScope` via `IServiceScopeFactory.CreateAsyncScope()`. `IInvocationContext.Services` resolves correctly per-message.
- **`IInvocationContext` ambient publication** — `IInvocationContextAccessor.Current` is set for the duration of `OnMessageAsync`, cleared after.
- **Synthetic invocation scope around lifecycle hooks** — `OnAcceptAsync`, `OnConnectedAsync`, `OnDisconnectedAsync` all run with an ambient invocation set so consumers like `IUserStateAccessor` work normally.
- **Per-connection vs per-invocation `Items`** — `Connection.Items` is a fresh dictionary distinct from `HttpContext.Items`; per-message `IInvocationContext.Items` is freshly allocated per message.
- **Disposal ordering** — abort CTS canceled first, then `WebSocket.CloseOutputAsync` with 5s timeout, then `WebSocket.Dispose()`. Close failures are logged at `Warning` (not silent).

### Buffer management

- 4 KB initial pooled receive buffer (`ArrayPool<byte>.Shared`) — single allocation per connection
- 1 MB max message size guard — peers exceeding it get a `MessageTooBig` close frame
- Multi-frame messages accumulated into a `MemoryStream` until `EndOfMessage = true`, then dispatched as a single `ReadOnlyMemory<byte>`

---

## Coordinated downstream work

Pairs with:

- **`Cirreum.Runtime.Invocation.WebSockets 1.0.0`** — app-facing `AddWebSocketInvocation()` / `AddWebSocket<THandler>()` / `MapWebSocketInvocation()` extensions
- **`Cirreum.InvocationProvider 1.2.0`** — adds the `IInvocationConnection.Abort()` contract this package implements

---

## Compatibility

- Built against `Cirreum.InvocationProvider 1.2.0`
- Targets `net10.0` / `Microsoft.AspNetCore.App`
- No prior versions — initial release

---

## See also

- `CHANGELOG.md` — condensed change list for 1.0.0.
- [`Cirreum.Runtime.Invocation.WebSockets`](https://www.nuget.org/packages/Cirreum.Runtime.Invocation.WebSockets) — companion L5 Runtime Extensions package; this is what apps reference directly.
- [`Cirreum.Invocation.SignalR`](https://www.nuget.org/packages/Cirreum.Invocation.SignalR) — peer L3 source adapter; same `IInvocationContext` semantics through a different transport.
- [ADR-0002](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md) — the foundational design decision for the unified Invocation seam.

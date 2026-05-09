# Cirreum.Invocation.WebSockets Backlog

Deferred work for the WebSocket invocation source. Each item carries SemVer impact, a readiness condition, and the date it was noted.

---

### Pluggable serialization providers (MessagePack, Protobuf)

**SemVer:** Minor
**Trigger:** First app needs binary-efficient on-wire format on top of WebSocket; or `IConnectionSender` is shown to be a bottleneck for high-frequency JSON traffic.
**Noted:** 2026-05-09

The current `WebSocketConnectionSender` always serializes payloads as JSON via `System.Text.Json`. Apps that need binary-efficient formats (MessagePack, Protobuf, CBOR) have to bypass `IConnectionSender` and write to the raw WebSocket.

Proposed shape:

```csharp
public interface IConnectionSerializer {
    WebSocketMessageType MessageType { get; }   // Text or Binary
    void Serialize<T>(T payload, IBufferWriter<byte> writer);
    void SerializeWithMethod<T>(string method, T payload, IBufferWriter<byte> writer);
}

// Default: JsonConnectionSerializer (current behavior)
// Add-on packages:
//   Cirreum.Invocation.WebSockets.Serialization.MessagePack → MessagePackConnectionSerializer
//   Cirreum.Invocation.WebSockets.Serialization.Protobuf   → ProtobufConnectionSerializer
```

`WebSocketConnectionSender` resolves `IConnectionSerializer` from DI; default registration uses `JsonConnectionSerializer`. Per-instance serializer choice via instance settings (`Serializer: "MessagePack"` etc.) or via subprotocol negotiation (e.g. client requests `cirreum-msgpack-v1`, handler returns it from `OnSelectSubProtocolAsync`, framework wires the matching serializer).

Reference: [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp).

---

### Multi-subprotocol negotiation list in instance settings

**SemVer:** Minor
**Trigger:** App needs declarative subprotocol negotiation across versions without writing `OnSelectSubProtocolAsync` logic.
**Noted:** 2026-05-09

Today, subprotocol negotiation lives in `WebSocketHandler.OnSelectSubProtocolAsync` — the handler reads `WebSocketRequestedProtocols` and picks. For the common "advertise these protocols in preference order" case, a config-driven shortcut would be cleaner:

```json
"chat": {
  "Path": "/ws/chat",
  "SubProtocols": ["cirreum-v2", "cirreum-v1"]   // first-match wins
}
```

Framework picks the first config entry that's also in the client's requested list. Falls back to `OnSelectSubProtocolAsync` if no config entry matches.

---

### Per-instance compression (permessage-deflate)

**SemVer:** Minor
**Trigger:** App reports bandwidth pressure on text-heavy WebSocket traffic (chat, dashboards).
**Noted:** 2026-05-09

ASP.NET's `WebSocketAcceptContext.DangerousEnableCompression` enables permessage-deflate. The "Dangerous" prefix flags real concerns (CRIME-style attacks against compressed-then-encrypted streams). Worth offering per-instance when an app explicitly opts in, with documentation around the security trade-offs.

---

### Provider-level `WebSocketOptions` configuration

**SemVer:** Minor
**Trigger:** App needs to set global WebSocket options (default `KeepAliveInterval`, `KeepAliveTimeout`, `AllowedOrigins`) at the framework layer rather than calling `app.UseWebSockets(options)` themselves.
**Noted:** 2026-05-09

`MapWebSocketInvocation()` currently calls `app.UseWebSockets()` with no arguments. Apps that want to set global `WebSocketOptions` have to call `UseWebSockets(...)` themselves *before* `MapWebSocketInvocation()`. Works, but feels awkward.

Proposed: provider-level config section binding `WebSocketOptions`:

```json
"Cirreum:Invocation:Providers:WebSocket:WebSocketOptions": {
  "KeepAliveInterval": "00:02:00",
  "KeepAliveTimeout": "00:00:30",
  "AllowedOrigins": ["https://example.com"]
}
```

`MapWebSocketInvocation()` reads this and passes to `UseWebSockets()`. Per-instance `KeepAliveInterval` / `KeepAliveTimeout` still override via `WebSocketAcceptContext`.

**On `AllowedOrigins` specifically:** the Origin header is a defense against browser-based CSWSH (Cross-Site WebSocket Hijacking) when the auth model relies on automatically-attached browser credentials (cookies). Cirreum's API-first, stateless, sessionless, cookieless design eliminates this attack surface — token-bearer auth (header or query string) doesn't get auto-attached cross-origin, so a malicious site's JavaScript can't ride a victim's authenticated session. `AllowedOrigins` is therefore a **low-priority** addition for Cirreum apps; included here only for completeness alongside the other `WebSocketOptions` fields. Apps mixing cookie auth and WebSocket should still wire it (via the existing `app.UseWebSockets(...)` escape hatch).

### Connection registry / fan-out push

**SemVer:** Minor
**Trigger:** First app needs to broadcast/group-send across active WebSocket connections (e.g. push notifications, presence updates).
**Noted:** 2026-05-09

`IConnectionSender` only addresses the calling client. Apps that want to send to *other* connections (broadcast, target by user ID, target by group) need a connection registry. SignalR provides this natively (`Clients.All`, `Clients.User`, `Clients.Group`). Raw WebSocket has no equivalent — apps build it themselves today.

Proposed: `IWebSocketConnectionRegistry` with `GetConnections(predicate)` / `SendToAsync(...)`. Maintained by `IConnectionLifecycle.OnConnectedAsync` / `OnDisconnectedAsync` framework hooks. Memory-only by default; pluggable via interface for distributed scenarios (Redis, etc.).

If apps need this often, it warrants framework support. If they don't, leaves it to apps to roll their own without paying for it.

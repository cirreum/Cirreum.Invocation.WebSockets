# Cirreum.Invocation.WebSockets Backlog

Deferred work for the WebSocket invocation source. Each item carries SemVer impact, a readiness condition, and the date it was noted.

---

### Pluggable serialization providers (MessagePack, Protobuf)

- **SemVer:** Minor
- **Trigger:** First concrete app need for typed binary-format push (MessagePack, Protobuf, CBOR) over the framework's typed `SendAsync<T>` path.
- **Noted:** 2026-05-09; reaffirmed 2026-05-09 during `IConnectionSender` consolidation.

The framework's typed `SendAsync<T>` overloads on `IInvocationConnection` (and the handler shortcut) always JSON-serialize via `System.Text.Json` using the handler's captured `SerializerOptions`. Apps that need binary-efficient formats today have an escape hatch: cast to `IWebSocketConnection` and call `SendBytesAsync(myBytes, WebSocketMessageType.Binary, ct)` after serializing themselves with their library of choice (MessagePack-CSharp, protobuf-net, etc.). That works for app-internal code today; what's missing is a *typed* binary path that cross-cutting code (Conductor handlers running under a transport-agnostic abstraction) can use.

Resisting now because:
- The escape hatch covers app-internal binary code (the common case).
- A well-shaped abstraction needs a concrete consumer to drive it. Speculating on `IPayloadSerializer` shape (Text vs Binary frame, envelope handling, AOT/source-gen integration) without a real workload risks getting it wrong.
- gRPC streaming (when added) brings its own typed binary story via Protobuf message types; the abstraction may want to span both transports rather than being WebSocket-specific.

Reaffirm and design when the first real binary-typed-push-from-cross-cutting-code use case shows up. Reference: [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp).

---

### Multi-subprotocol negotiation list in instance settings

- **SemVer:** Minor
- **Trigger:** App needs declarative subprotocol negotiation across versions without writing `OnSelectSubProtocolAsync` logic.
- **Noted:** 2026-05-09

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

- **SemVer:** Minor
- **Trigger:** App reports bandwidth pressure on text-heavy WebSocket traffic (chat, dashboards).
- **Noted:** 2026-05-09

ASP.NET's `WebSocketAcceptContext.DangerousEnableCompression` enables permessage-deflate. The "Dangerous" prefix flags real concerns (CRIME-style attacks against compressed-then-encrypted streams). Worth offering per-instance when an app explicitly opts in, with documentation around the security trade-offs.

---

### Connection registry / fan-out push

- **SemVer:** Minor
- **Trigger:** First app needs to broadcast/group-send across active WebSocket connections (e.g. push notifications, presence updates).
- **Noted:** 2026-05-09

`IInvocationConnection.SendAsync` (and `IWebSocketConnection.SendBytesAsync`) only address the calling client. Apps that want to send to *other* connections (broadcast, target by user ID, target by group) need a connection registry. SignalR provides this natively (`Clients.All`, `Clients.User`, `Clients.Group`). Raw WebSocket has no equivalent — apps build it themselves today.

Proposed: `IWebSocketConnectionRegistry` with `GetConnections(predicate)` / `SendToAsync(...)`. Maintained by `IConnectionLifecycle.OnConnectedAsync` / `OnDisconnectedAsync` framework hooks. Memory-only by default; pluggable via interface for distributed scenarios (Redis, etc.).

If apps need this often, it warrants framework support. If they don't, leaves it to apps to roll their own without paying for it.

# Cirreum.Invocation.WebSockets Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`IWebSocketConnection`** (public, `Cirreum.Invocation.WebSockets`) — WebSocket-specific extension of `IInvocationConnection` that exposes the frame-level send primitive `SendBytesAsync(ReadOnlyMemory<byte>, WebSocketMessageType, CT)`. Implemented by the framework's internal `WebSocketConnection`. Inside a handler, no cast is needed — `WebSocketHandler.Connection` is typed as `IWebSocketConnection` directly, so handlers call `this.Connection.SendBytesAsync(...)` straight away. Cross-cutting code reaching the connection through the L2 `IInvocationContextAccessor` does need to downcast (`accessor.Current?.Connection is IWebSocketConnection ws`) — the L2 accessor is transport-neutral by design, so the downcast is the explicit "I am committed to WebSocket-specific behavior" acknowledgment. Use for raw frame writes — binary protocols, audio/video chunks, pre-serialized payloads — that bypass JSON serialization entirely. Transport-substitutable code stays on the base `IInvocationConnection.SendAsync` overloads.

- **`WebSocketConnection.SendAsync<T>` overloads (2)** — implementations of the new `IInvocationConnection.SendAsync<T>` members from the upcoming `Cirreum.InvocationProvider` release. JSON-serialize the payload using the active `WebSocketHandler.SerializerOptions` (captured at connection construction), sent as a Text frame. The keyed overload wraps in a `{ method, payload }` envelope. Cross-cutting code reaching the connection through `IInvocationContextAccessor.Current.Connection` now produces identical wire bytes to handler-internal `this.SendAsync(...)` calls — including any source-generated `JsonTypeInfoResolver` the app configured.

- **`WebSocketConnection.SendBytesAsync`** — implementation of the new `IWebSocketConnection.SendBytesAsync` member. Writes raw bytes to the wire as a single complete frame; defaults to `WebSocketMessageType.Binary`. Replaces the handler-only raw-bytes `SendAsync` overload (now consolidated into this primitive — see Changed).

### Changed

- **`WebSocketConnection`** now implements `IWebSocketConnection` (which itself extends `IInvocationConnection`). Constructor takes a new `JsonSerializerOptions serializerOptions` parameter — `WebSocketOrchestrator` reads `handler.SerializerOptions` at upgrade time and passes it through. The handler is materialized first (always was), so capturing its `SerializerOptions` for the connection's lifetime is a natural ordering.

- **`WebSocketHandler.SerializerOptions`** widened from `protected virtual` to `protected internal virtual` so `WebSocketOrchestrator` (same assembly) can read it at upgrade time. App overrides (always `protected`) are unaffected. Read once at upgrade and captured on the connection — overriding the property to return different options after the connection is established has no effect (apps were already advised to cache in a `static readonly` field, so this is not a behavior change in practice).

- **`WebSocketHandler.SendAsync<T>` typed overloads (2)** are now thin forwarders to `this.Connection.SendAsync(...)`. Same wire bytes, same serializer (because the connection holds the captured `SerializerOptions`). The handler-bound shortcut form remains because it's discoverable from the base class and ergonomic from inside lifecycle hooks.

- **`WebSocketHandler.SendAsync(ReadOnlyMemory<byte>, WebSocketMessageType, CT)` consolidated into `IWebSocketConnection.SendBytesAsync`** — the handler-only raw-bytes overload is gone; the same operation lives on the new public `IWebSocketConnection` interface (see Added). Handler code previously using `this.SendAsync(bytes, WebSocketMessageType.Binary, ct)` now does:
  ```csharp
  await this.Connection.SendBytesAsync(bytes, WebSocketMessageType.Binary, ct);
  ```
  No cast needed — `Connection` is typed as `IWebSocketConnection` (see "Doc: Connection type" below). The handler's typed `SendAsync<T>` shortcuts (which forward to the L2 `IInvocationConnection.SendAsync<T>`) remain. Rationale: handlers are by definition WebSocket-specific (you inherit from `WebSocketHandler`); typing the property as the more specific interface eliminates a friction-cast at every binary-frame call site without losing anything (L2 sends still flow through naturally via interface inheritance). Captured as `### Changed` (not `### Removed`) under the same window-of-no-consumers, framework-owned-implementer-set precedent as the L2 1.1.0 / 1.2.0 cascades — this is a v1.x pre-adoption surface; the consolidation is a Minor.

- **`WebSocketConnectionSender` consolidated into `WebSocketConnection.SendAsync`** — the standalone scoped service and its DI registration in `WebSocketInvocationRegistrar.RegisterSource` are gone; the same operation now lives on `WebSocketConnection`, satisfying the new `IInvocationConnection.SendAsync<T>` contract. See the upcoming `Cirreum.InvocationProvider` release notes for the L2 rationale. Apps that previously injected `IConnectionSender` now read the connection from the ambient `IInvocationContextAccessor` and call `SendAsync` directly — same wire bytes; cross-cutting code now also picks up the handler's `SerializerOptions` automatically (including source-gen JSON resolvers).

- **`WebSocketHandler.Connection` is now typed as `IWebSocketConnection`** (was `IInvocationConnection`). Handler-internal code can now call `this.Connection.SendBytesAsync(...)` and the L2 typed `SendAsync<T>` overloads without a cast — `IWebSocketConnection` extends `IInvocationConnection`, so all L2 members are still reachable through the same property. Source-compatible for handler code that consumed the property as the L2 type only via `this.Connection.SendAsync<T>(...)`, `this.Connection.Items`, etc. (the more specific type is implicitly assignable). Source-incompatible for handler code that explicitly captured `IInvocationConnection conn = this.Connection;` (rare; would need to widen to `IWebSocketConnection`). Cross-cutting code reaching the connection through `IInvocationContextAccessor.Current.Connection` is unchanged — the L2 accessor still returns `IInvocationConnection?`.

- **`WebSocketHandler.Connection` is now non-nullable**, backed by a throwing `NotEstablished` sentinel (singleton) during the `OnAcceptAsync` / `OnSelectSubProtocolAsync` window when the real connection doesn't yet exist. Calls to any sentinel member surface a clear `InvalidOperationException` ("Connection has not been established yet…") instead of a `NullReferenceException`. Once `OnConnectedAsync` runs through `OnDisconnectedAsync`, the sentinel is replaced with the real `WebSocketConnection`. Source-compatible for handler code that just calls members on `this.Connection` (the property type changed from nullable to non-nullable, removing the need for `!` null-forgiving operators in handler code).

- Bumped `Cirreum.InvocationProvider` dependency to consume the `IInvocationConnection.SendAsync` interface widening and the corresponding consolidation of `IConnectionSender`.

### Migration

**App-side cross-cutting code** previously injecting `IConnectionSender`: see the migration block in the upcoming `Cirreum.InvocationProvider` release notes — replace with `accessor.Current?.Connection?.SendAsync(...)`. The wire bytes are identical (cross-cutting code now picks up the handler's `SerializerOptions` automatically, including source-gen JSON resolvers).

**App-side handler code** previously calling `this.SendAsync(bytes, WebSocketMessageType.Binary, ct)`:

```diff
  public override async Task OnMessageAsync(IInvocationContext ctx, ReadOnlyMemory<byte> msg, WebSocketMessageType type) {
-     await this.SendAsync(audioChunk, WebSocketMessageType.Binary, ctx.Aborted);
+     await this.Connection.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ctx.Aborted);
  }
```

Handler code calling the typed `this.SendAsync(payload, ct)` / `this.SendAsync(method, payload, ct)` overloads is unchanged — those overloads still exist as shortcuts; they now forward to the connection.

## [1.1.0] - 2026-05-09

### Added

- **`WebSocketHandler.SendAsync` overloads (3)** — handler-bound push primitives that resolve the underlying `WebSocket` directly from `this.Connection`, bypassing the ambient `IInvocationContextAccessor` lookup. Lets handlers send to the inbound (framework-owned) socket from *any* calling context — lifecycle hooks AND handler-managed background tasks — symmetric with how they already send to handler-owned outbound sockets. Closes a fragility gap in the IVA-style voice/AI bridge pattern where `IConnectionSender` could fail in background tasks whose `ExecutionContext` capture didn't include the synthetic invocation. Three overloads:
  - `SendAsync<T>(T payload, CancellationToken)` — JSON-serialize as a `Text` frame
  - `SendAsync<T>(string method, T payload, CancellationToken)` — `{ method, payload }` envelope for method-dispatch protocols, sent as `Text`
  - `SendAsync(ReadOnlyMemory<byte>, WebSocketMessageType, CancellationToken)` — raw bytes (`Binary` by default), bypasses JSON

  The two typed overloads send `WebSocketMessageType.Text`. `IConnectionSender` is unchanged and remains the right tool for cross-cutting code (Conductor command/query handlers, validators, transport-agnostic services that may run on HTTP, SignalR, or WebSocket). Choose `this.SendAsync` for handler-internal code and `IConnectionSender` for cross-cutting.

- **`WebSocketHandler.SerializerOptions` virtual property** — JSON serializer options used by the typed `SendAsync` overloads. Defaults to reflection-based `JsonSerializerDefaults.Web`. Override to wire in a source-generated `JsonTypeInfoResolver` for AOT/trim-friendly apps and reflection-free runtime serialization. Cache the override in a `static readonly` field — the property is read on every send.

### Changed

- **Inbound frame-loop buffering switched from `MemoryStream` to `ArrayBufferWriter<byte>`** — purpose-built for write-only buffer accumulation, fewer fields touched per `Write`, no per-message `byte[]` allocation. Internal change; not observable from outside the package.

- **`OnMessageAsync(message)` lifetime is now borrow semantics** — the bytes are valid only for the duration of the returned task. After the task completes, the framework reuses the buffer for the next inbound message. Eliminates a per-message `byte[]` allocation in the framework's hot path. Source- and binary-compatible (signature unchanged); semantic change for handlers that captured the memory into `Task.Run`, fire-and-forget continuations, queues, or long-lived state — those must now copy explicitly via `message.ToArray()`. Most parsers (`JsonDocument.Parse`, `MessagePackSerializer.Deserialize`, etc.) consume the span synchronously and need no copy.

### Migration from 1.0.0

If `OnMessageAsync` captures `message` past the returned task's completion, copy explicitly:

```diff
  public override async Task OnMessageAsync(IInvocationContext context, ReadOnlyMemory<byte> message, WebSocketMessageType type) {
-     _ = Task.Run(() => SaveAsync(message, context.Aborted));
+     var owned = message.ToArray();
+     _ = Task.Run(() => SaveAsync(owned, context.Aborted));
  }
```

Synchronous parsing, awaited dispatch, and direct forwarding over an outbound socket all keep `message` alive long enough — no change needed.

## [1.0.0] - 2026-05-09

### Added

- WebSocket invocation source for the Cirreum framework
- `WebSocketHandler` abstract base class for application-defined message handlers, with `OnAcceptAsync` (pre-accept gate), `OnSelectSubProtocolAsync`, `OnConnectedAsync`, `OnMessageAsync(IInvocationContext, ...)`, and `OnDisconnectedAsync(DisconnectInfo, CancellationToken)` lifecycle methods
- Two-phase connection model — optional `RequestPath` HTTP endpoint that initiates the WebSocket flow (e.g. Twilio incoming-call webhook), paired with a `request:` builder at the `AddWebSocket<THandler>` call site, mapped alongside the WebSocket endpoint at `Path`
- `IWebSocketUrlBuilder` — instance-aware URL builder with auto-extraction of template values from route, query, and form (case-insensitive); explicit-instance overload for cross-context use
- `WebSocketConnection.Abort()` — implementation of the new `IInvocationConnection.Abort()` contract from `Cirreum.InvocationProvider` 1.2.0
- `WebSocketOrchestrator` — per-connection driver: runs the handler lifecycle, manages the frame loop, per-message DI scopes, and the bounded disconnect-cleanup CTS
- `WebSocketConnection` — `IInvocationConnection` implementation for WebSocket connections, with logged graceful-close failures
- `WebSocketInvocationContext` — per-message `IInvocationContext` with fresh `Items` dictionary; disconnect-path overload exposes the bounded cleanup CTS as `Aborted`
- `WebSocketConnectionSender` — `IConnectionSender` implementation for server-initiated push over WebSocket
- `WebSocketInvocationRegistrar` — config-driven registrar for the WebSocket provider, validates `RequestPath` ↔ `request:` builder pairing, defaults the request endpoint to POST, excludes the WebSocket endpoint from OpenAPI discovery
- `WebSocketHandlerMapping` — DI-stashed mapping record carrying handler type, optional request handler/method/configurator
- `WebSocketInstanceMetadata` — endpoint metadata enabling implicit instance resolution by `IWebSocketUrlBuilder`
- `WebSocketInvocationSettings` / `WebSocketInvocationInstanceSettings` — configuration classes (`Path`, `RequestPath`, `Scheme`, `Enabled`, `DisconnectTimeoutSeconds`, `MaxMessageSizeBytes`, `ReceiveBufferSizeBytes`, `KeepAliveInterval`, `KeepAliveTimeout`)
- Source-generated `[LoggerMessage]` framework logging on the orchestrator (frame loop exceptions, message-size violations, cleanup-budget exhaustion at Warning; connection accepted, pre-accept rejected, lifecycle-rejected upgrade, connection closed at Debug) and the connection (graceful-close failures at Warning)

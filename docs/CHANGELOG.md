# Cirreum.Invocation.WebSockets Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

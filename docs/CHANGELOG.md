# Cirreum.Invocation.WebSockets Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

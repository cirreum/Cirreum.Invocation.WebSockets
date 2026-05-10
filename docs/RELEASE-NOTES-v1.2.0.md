# Cirreum.Invocation.WebSockets 1.2.0 — `SendAsync` on the connection + `IWebSocketConnection`

Implements the new `IInvocationConnection.SendAsync<T>` overloads from `Cirreum.InvocationProvider 1.3.0` directly on `WebSocketConnection`, deletes the standalone `WebSocketConnectionSender`, and introduces a new public `IWebSocketConnection` interface that exposes the WebSocket-specific frame-level send primitive (`SendBytesAsync`).

The `JsonSerializerOptions` story finally lines up: the connection captures the active handler's `SerializerOptions` at upgrade time, so cross-cutting code reaching the connection through `IInvocationContextAccessor.Current.Connection` produces identical wire bytes to handler-internal `this.SendAsync(...)` calls — including any source-generated `JsonTypeInfoResolver` the app configured. This was a real gap in the previous shape: cross-cutting code could only get the framework's default web JSON, even when the handler was wired up with source-gen.

---

## Why this release exists

`WebSocketConnectionSender` was a parallel scoped service to its SignalR sibling — same shape, same complaint:

```csharp
var connection = accessor.Current?.Connection as WebSocketConnection
    ?? throw new InvalidOperationException("...");
var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
await connection.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
```

Every consumer was already in (or directly under) a WebSocket handler. The L2 consolidation (see `Cirreum.InvocationProvider 1.3.0`) puts `SendAsync<T>` on the connection itself; the wrapper has no purpose.

But this release does more than just delete the wrapper — it also fixes a long-standing serialization-options gap, and reshapes the handler's surface to match the L2 contract.

---

## What's new

### `IWebSocketConnection` (public)

WebSocket-specific extension of `IInvocationConnection` that exposes the frame-level send primitive:

```csharp
namespace Cirreum.Invocation.WebSockets;

public interface IWebSocketConnection : IInvocationConnection {
    ValueTask SendBytesAsync(
        ReadOnlyMemory<byte> bytes,
        WebSocketMessageType messageType = WebSocketMessageType.Binary,
        CancellationToken cancellationToken = default);
}
```

Implemented by the framework's internal `WebSocketConnection`.

**From inside a handler — no cast needed.** `WebSocketHandler.Connection` is typed as `IWebSocketConnection` directly (see "What's changed" below), so handlers reach the new primitive straight away:

```csharp
await this.Connection.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ct);
```

**From cross-cutting code — explicit downcast.** Code reaching the connection through the L2 `IInvocationContextAccessor` works against the transport-neutral `IInvocationConnection?` view; downcast when WebSocket specifics are needed:

```csharp
if (accessor.Current?.Connection is IWebSocketConnection ws) {
    await ws.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ct);
}
```

The downcast is the explicit "I am committed to WebSocket-specific behavior" acknowledgment — code that goes through it cannot transport-substitute later. Transport-agnostic code stays on the L2 `IInvocationConnection.SendAsync<T>` overloads.

### `WebSocketConnection.SendAsync<T>` — two overloads

Implementations of the new L2 contract:

```csharp
public ValueTask SendAsync<T>(T payload, CancellationToken ct = default) {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, this._serializerOptions);
    return this.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}

public ValueTask SendAsync<T>(string method, T payload, CancellationToken ct = default) {
    var envelope = new { method, payload };
    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, this._serializerOptions);
    return this.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}
```

| Behavior | Detail |
|---|---|
| Frame type | Text (UTF-8 JSON) — the convention for JSON-over-WebSocket. |
| Serializer | Connection-captured `JsonSerializerOptions` (sourced from handler's `SerializerOptions` at upgrade). |
| Method routing (keyed overload) | Wraps payload in a `{ method, payload }` envelope for apps implementing their own method-dispatch protocol. |
| Method routing (no-method overload) | None — payload is sent as-is. |

### Connection-captured `SerializerOptions`

`WebSocketConnection`'s constructor takes a new `JsonSerializerOptions serializerOptions` parameter. `WebSocketOrchestrator` reads `handler.SerializerOptions` at upgrade time and passes it through. The handler is materialized first (always was — it's the per-connection scoped service whose lifecycle hooks the orchestrator drives), so capturing its `SerializerOptions` for the connection's lifetime is a natural ordering.

This means: **cross-cutting code automatically gets the handler's source-gen JSON.**

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TwilioMediaMessage))]
public sealed partial class TwilioJsonContext : JsonSerializerContext { }

public sealed class TwilioMediaHandler : WebSocketHandler {
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = TwilioJsonContext.Default
    };
    protected override JsonSerializerOptions SerializerOptions => _options;
    // ...
}

// Cross-cutting Conductor command handler — no knowledge of TwilioJsonContext,
// but accessor.Current.Connection.SendAsync(...) uses TwilioJsonContext.Default
// because the connection captured TwilioMediaHandler's SerializerOptions at upgrade.
```

`WebSocketHandler.SerializerOptions` is widened from `protected virtual` to `protected internal virtual` so the orchestrator (same assembly) can read it. App overrides remain `protected`.

---

## What's changed

### `WebSocketHandler.Connection` typed as `IWebSocketConnection` (and non-nullable)

The handler's `Connection` property used to be `IInvocationConnection?`. It's now `IWebSocketConnection` (which extends `IInvocationConnection`), and non-nullable — backed by a throwing `NotEstablished` sentinel singleton during the `OnAcceptAsync` / `OnSelectSubProtocolAsync` window when the real connection doesn't yet exist.

Two simplifications fall out:

```csharp
// Was:                                                    // Now:
var ws = (IWebSocketConnection)this.Connection!;           await this.Connection.SendBytesAsync(bytes, ..., ct);
await ws.SendBytesAsync(bytes, ..., ct);

await this.Connection!.SendAsync(payload, ct);             await this.Connection.SendAsync(payload, ct);
```

No casts inside the handler. No `!` null-forgiving operators. Calls before `OnConnectedAsync` runs hit the sentinel and throw `InvalidOperationException` with a clear "Connection has not been established yet…" message — strictly better than the previous `NullReferenceException` you'd get from a missed null-check.

Rationale: handlers are by definition WebSocket-specific (you inherit from `WebSocketHandler`); typing the property as the more specific interface eliminates a friction-cast at every binary-frame call site. Cross-cutting code is unaffected — the L2 `IInvocationContextAccessor.Current.Connection` is still typed as `IInvocationConnection?`, downcast required when binary frames matter (the right place for the explicit acknowledgment).

### `WebSocketHandler.SendAsync<T>` typed overloads — now thin forwarders

```csharp
protected ValueTask SendAsync<T>(T payload, CancellationToken ct = default)
    => this.Connection.SendAsync(payload, ct);

protected ValueTask SendAsync<T>(string method, T payload, CancellationToken ct = default)
    => this.Connection.SendAsync(method, payload, ct);
```

Same wire bytes, same serializer (because the connection holds the captured `SerializerOptions`). The handler-bound shortcut form remains because it's discoverable from the base class and ergonomic from inside lifecycle hooks.

### `WebSocketHandler.SendAsync(ReadOnlyMemory<byte>, WebSocketMessageType, CT)` consolidated into `IWebSocketConnection.SendBytesAsync`

The handler-only raw-bytes overload is gone; the same operation lives on the new public `IWebSocketConnection` interface (see "What's new"). Handler code previously using `this.SendAsync(bytes, WebSocketMessageType.Binary, ct)` now calls it directly on the connection — no cast, no helper property:

```diff
  public override async Task OnMessageAsync(IInvocationContext ctx, ReadOnlyMemory<byte> msg, WebSocketMessageType type) {
-     await this.SendAsync(audioChunk, WebSocketMessageType.Binary, ctx.Aborted);
+     await this.Connection.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ctx.Aborted);
  }
```

Captured here as a reshape rather than a removal: the same operation lives on, on a more natural interface. The handler's surface now mirrors the L2 contract exactly — every shortcut on the handler is something a cross-cutting code path could also do via `IInvocationConnection`, so there are no surprises about which methods generalize. Treated as Minor under the same window-of-no-consumers, framework-owned-implementer-set precedent as the L2 1.1.0 / 1.2.0 cascades — this is a v1.x pre-adoption surface.

### `WebSocketConnectionSender` consolidated into `WebSocketConnection.SendAsync`

The standalone scoped service and its DI registration in `WebSocketInvocationRegistrar.RegisterSource` are gone; the same operation now lives on `WebSocketConnection`, satisfying the new `IInvocationConnection.SendAsync<T>` contract. Cross-cutting code injecting `IConnectionSender` switches to ambient-accessor + connection — see the migration block in `Cirreum.InvocationProvider 1.3.0` release notes.

---

## Coordinated upstream work

Requires `Cirreum.InvocationProvider 1.3.0` (the L2 contract change). Ships in lockstep with `Cirreum.Invocation.SignalR 1.2.0` (parallel adapter update) and `Cirreum.Runtime.Invocation.WebSockets 1.1.0` (flow-through dep bump).

---

## Compatibility

- **Source-incompatible** for app code injecting `IConnectionSender` (~1 line per call site; see L2 release notes).
- **Source-incompatible** for handler code calling `this.SendAsync(bytes, WebSocketMessageType.Binary, ct)` (one-line migration to `this.Connection.SendBytesAsync(...)` above).
- **Source-incompatible** for handler code that explicitly captured `IInvocationConnection conn = this.Connection;` (rare; the property type widened to `IWebSocketConnection`. Either change the local type to `IWebSocketConnection` or accept the implicit upcast). Call-site code that just invoked members on `this.Connection` is source-compatible.
- **Source- and binary-compatible** for handler code calling the typed `this.SendAsync(payload, ct)` / `this.SendAsync(method, payload, ct)` overloads — those still exist; signatures unchanged.
- **Source- and binary-compatible** for handler code overriding `SerializerOptions` — the access modifier widening (`protected virtual` → `protected internal virtual`) is permissive for apps that override it; subclass overrides need no change.
- All other surface (`WebSocketOrchestrator`, `WebSocketHandlerMapping`, `WebSocketInvocationSettings`, the registrar, lifecycle hooks, `OnMessageAsync` borrow semantics) is unchanged.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.2.0`.
- [`Cirreum.InvocationProvider 1.3.0`](https://www.nuget.org/packages/Cirreum.InvocationProvider) — the L2 consolidation that motivated this release.
- [`Cirreum.Invocation.SignalR 1.2.0`](https://www.nuget.org/packages/Cirreum.Invocation.SignalR) — parallel adapter update.
- `RELEASE-NOTES-v1.1.0.md` — handler-bound `SendAsync` overloads + `SerializerOptions` virtual property (introduced this release's foundation).
- `RELEASE-NOTES-v1.0.0.md` — initial WebSocket invocation source release.

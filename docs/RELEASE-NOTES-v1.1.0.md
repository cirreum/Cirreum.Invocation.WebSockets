# Cirreum.Invocation.WebSockets 1.1.0 — handler-bound `SendAsync`, borrow semantics, source-gen JSON

Three improvements to the WebSocket handler surface:

1. **`WebSocketHandler.SendAsync` overloads (3)** — handler-bound push that doesn't depend on the ambient `IInvocationContextAccessor`. Works from lifecycle hooks AND handler-managed background tasks (AI receive loops, timers, fire-and-forget continuations). Symmetric with how handlers already send to outbound sockets they own.
2. **Borrow semantics for `OnMessageAsync(message)`** — the bytes are now borrowed from a per-connection pooled `ArrayBufferWriter<byte>` and valid only for the duration of the returned task. Eliminates a per-message `byte[]` allocation in the framework's hot path. Important for high-frequency workloads (voice/realtime, telemetry).
3. **`SerializerOptions` virtual property** — override to wire in a source-generated `JsonTypeInfoResolver` for AOT/trim-friendly apps and reflection-free serialization on the typed `SendAsync` overloads.

Existing `IConnectionSender` usage continues to work and remains the right tool for cross-cutting code (Conductor command/query handlers, transport-agnostic services).

⚠️ **Behavior change:** `OnMessageAsync` `message` lifetime is now bounded by the returned task. Handlers that captured the bytes into `Task.Run`, fire-and-forget continuations, or long-lived state must now copy explicitly. See "Borrow semantics" below.

---

## Why this release exists

`Cirreum.Invocation.WebSockets 1.0.0` shipped with `IConnectionSender` as the only push primitive. It works beautifully for cross-cutting code that runs inside the invocation pipeline: a Conductor command handler invoked from `OnMessageAsync` resolves `IConnectionSender` via DI, the sender reads `IInvocationContextAccessor.Current` to find the active connection, and the push lands on the right client. The handler's transport (HTTP, SignalR, WebSocket) is invisible to the cross-cutting code — exactly the unification the framework promises.

That story breaks down for **handler-managed background tasks**. The IVA-style voice/AI bridge pattern looks like:

```csharp
public override async Task OnConnectedAsync(CancellationToken ct) {
    _aiSocket = await ConnectToAiAsync(ct);

    _aiTask = Task.Run(async () => {
        await foreach (var aiEvent in ReadAiEventsAsync(_aiSocket, ct)) {
            // Audio response from AI — needs to flow back to the calling client
            await sender.SendAsync(twilioMediaMessage, ct);   // ← does this work?
        }
    }, ct);
}
```

It works *most of the time*, by coincidence. `Task.Run` captures the current `ExecutionContext` — including the AsyncLocal-backed `IInvocationContextAccessor.Current` value — at start time. If the loop is launched from inside a hook where the framework has set the synthetic invocation, the captured ExecutionContext keeps the accessor populated for the loop's lifetime, and `IConnectionSender` resolves correctly.

But the pattern is fragile. Several legitimate scenarios break the AsyncLocal flow:

- AI loop launched from `OnAcceptAsync` (before the synthetic invocation is set)
- Background work scheduled from a `BackgroundService` or hosted `Timer` after `OnConnectedAsync` returns
- `_ = SomeAsyncMethod(...)` fire-and-forget continuations started outside any framework hook
- Custom `TaskScheduler` implementations that don't propagate `ExecutionContext`
- Refactor that moves the loop start into a helper called from a different lifecycle phase

In all of these, `accessor.Current` is null and `IConnectionSender.SendAsync` throws with "no active invocation." The handler has the connection — it's stored on `this.Connection` — but the sender can't reach it because it's looking through the wrong door.

Comparing to the IVA reference codebase clarified the asymmetry:

```csharp
// IVA — handler owns BOTH sockets, sends directly on each:
await SendToTwilioAsync(twilioWebSocket, message, ct);    // inbound (called)
await SendToAIAsync(aiWebSocket, message, ct);            // outbound (handler owns)
```

```csharp
// Cirreum 1.0.0 — handler owns the AI socket directly,
// but must go through the framework's accessor for the inbound:
await _aiSocket.SendAsync(...);                           // outbound — direct, fine
await sender.SendAsync(message, ct);                      // inbound — accessor-dependent
```

The asymmetry was the leak. The handler-owned socket has direct access by design (handler created it). The framework-owned socket should have a similarly direct path *from inside the handler* — because the handler's identity, authorization, and connection state are already known to the framework at handler construction. Re-resolving them from the accessor every push isn't necessary and isn't always available.

---

## What's new

### `WebSocketHandler.SendAsync` — three overloads

```csharp
public abstract class WebSocketHandler {

    /// JSON-serialize and send as a Text frame. The most common case.
    protected ValueTask SendAsync<T>(
        T payload,
        CancellationToken cancellationToken = default);

    /// Send wrapped in a {method, payload} envelope for apps implementing
    /// their own method-dispatch protocol on top of WebSocket.
    protected ValueTask SendAsync<T>(
        string method,
        T payload,
        CancellationToken cancellationToken = default);

    /// Send raw bytes (Binary or Text) — bypasses JSON serialization entirely.
    /// For binary protocols, pre-serialized payloads, or audio frames.
    protected ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType = WebSocketMessageType.Binary,
        CancellationToken cancellationToken = default);

}
```

All three:

- Resolve the underlying `WebSocket` from `this.Connection` (cast to `WebSocketConnection`) — **no accessor lookup, no AsyncLocal dependency**.
- Work from any calling context: lifecycle hooks (`OnConnectedAsync`, `OnMessageAsync`, `OnDisconnectedAsync`) AND handler-managed background tasks (AI receive loops, timers, fire-and-forget continuations, work captured into `BackgroundService`s that hold a handler reference).
- Throw `InvalidOperationException` with a clear, actionable message if called when `Connection` is null (i.e. during `OnAcceptAsync` or `OnSelectSubProtocolAsync`, before the WebSocket is accepted).

### Frame is sent as `WebSocketMessageType.Text`

Both typed overloads send their JSON-serialized payload as a `Text` frame. Use the raw-bytes overload for `Binary` (or any other type explicitly):

```csharp
await SendAsync(payload, ct);                      // Text (JSON)
await SendAsync("Method", payload, ct);            // Text (JSON envelope)
await SendAsync(rawBytes, WebSocketMessageType.Binary, ct);   // Binary (no serialization)
```

### `SerializerOptions` — virtual hook for source-generated JSON

The two typed overloads serialize via a virtual `SerializerOptions` property. Default is `new JsonSerializerOptions(JsonSerializerDefaults.Web)` — reflection-based, camelCase, web-compatible. Override to wire in a source-generated `JsonTypeInfoResolver` for AOT/trim-friendly apps and reflection-free runtime performance:

```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TwilioMediaMessage))]
[JsonSerializable(typeof(TwilioMarkMessage))]
[JsonSerializable(typeof(TwilioClearMessage))]
public sealed partial class TwilioJsonContext : JsonSerializerContext { }

public sealed class TwilioMediaHandler : WebSocketHandler {
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = TwilioJsonContext.Default
    };

    protected override JsonSerializerOptions SerializerOptions => _options;

    // ... lifecycle hooks ...
}
```

The default for handlers that don't override is unchanged. Cache the override in a `static readonly` field — the property is read on every `SendAsync` call.

### Borrow semantics for `OnMessageAsync(message)`

In v1.0.0, the framework allocated a fresh `byte[]` for each complete message before dispatching to `OnMessageAsync`. v1.1.0 switches the inbound buffering to `ArrayBufferWriter<byte>` and hands `WrittenMemory` directly to the handler — the bytes are **borrowed** from a per-connection pooled buffer and valid only for the duration of the returned task.

```csharp
public override async Task OnMessageAsync(
    IInvocationContext context,
    ReadOnlyMemory<byte> message,
    WebSocketMessageType messageType) {

    // ✅ Synchronous use, including awaits — safe.
    var twilioEvent = JsonSerializer.Deserialize<TwilioEvent>(message.Span);
    await ProcessAsync(twilioEvent, context.Aborted);

    // ❌ DO NOT capture into Task.Run, fire-and-forget, queues, or long-lived state.
    // The bytes will be reused for the next message after this method returns.
    _ = Task.Run(() => SaveAsync(message));   // BUG — message will be reused!

    // ✅ If you must retain, copy:
    var owned = message.ToArray();
    _ = Task.Run(() => SaveAsync(owned));
}
```

Why this matters: at voice/realtime rates (50–100 messages/sec/connection × hundreds of concurrent connections), eliminating one allocation per message removes thousands of gen-0 allocations per second from the framework's path. Most parsers (`JsonDocument.Parse`, `MessagePackSerializer.Deserialize`, etc.) consume the span synchronously and produce their own owned representations — so apps that immediately deserialize don't need to copy at all.

This is the same pattern used by Kestrel and System.IO.Pipelines — performance-first .NET frameworks all converged on borrow semantics for their hot paths. The `OnMessageAsync` XML doc carries the contract; failing to honor it is a runtime hazard, not a compile error.

### When to use which sender

The framework now has two complementary push APIs:

| API | Audience | Resolution path | Works when |
|---|---|---|---|
| **`this.SendAsync(...)`** | Handler-internal code — lifecycle hooks, handler background tasks | Direct via captured `Connection` | **Always** (after `OnConnectedAsync`, before disposal) |
| **`IConnectionSender`** | Cross-cutting code — Conductor handlers, validators, transport-agnostic services | Ambient via `IInvocationContextAccessor.Current` | Inside an active invocation pipeline |

Both send the same wire format and route to the same client. The choice is "do I have the handler's `this`, or am I cross-cutting code that doesn't know what transport invoked me?"

### Practical effect on the IVA-style voice bridge

The voice handler now reads the way you'd expect — symmetric on both directions:

```csharp
public sealed class TwilioMediaHandler(IAiClient ai) : WebSocketHandler {

    private ClientWebSocket? _aiSocket;

    public override async Task OnConnectedAsync(CancellationToken ct) {
        _aiSocket = await ai.ConnectAsync(ct);

        _ = Task.Run(async () => {
            try {
                await foreach (var aiEvent in ReadAiEventsAsync(_aiSocket, ct)) {
                    if (TryExtractAudio(aiEvent, out var audio)) {
                        var twilioMsg = TwilioMediaMessage.Create(streamSid, audio);
                        await SendAsync(twilioMsg, ct);   // ← inbound, handler-bound
                    }
                }
            } finally {
                Connection!.Abort();
            }
        }, ct);
    }

    public override async Task OnMessageAsync(
        IInvocationContext context,
        ReadOnlyMemory<byte> message,
        WebSocketMessageType type) {

        // Forward Twilio audio to AI — outbound is direct on the handler-owned socket.
        await _aiSocket!.SendAsync(message, type, true, context.Aborted);
    }

}
```

No `IConnectionSender` injection needed for the voice path. The handler owns its state, calls `this.SendAsync` for the inbound, calls `_aiSocket.SendAsync` for the outbound, and `IConnectionSender` is reserved for the AI tool-call → Conductor → progress-stream-back scenarios where the cross-cutting abstraction earns its keep.

---

## Why this is 1.1.0 and not 1.0.1 or 2.0.0

The signature of `OnMessageAsync` is unchanged, so the change is **source- and binary-compatible** in the strict mechanical sense. Existing handlers compile against 1.1.0 without modification. The contract change for `message` lifetime is a **semantic** change — apps that captured the bytes into out-of-band state (rare; usually a code smell) will see the captured memory get reused for the next message.

Per the framework's release-gate rules, `### Added` content fails the patch gate (which only allows `### Fixed` and `### Security`) and passes the minor gate. Same window-of-no-consumers reasoning that motivated `1.1.0`'s `IConnectionLifecycle.OnDisconnectedAsync` signature change in `Cirreum.InvocationProvider` (and `1.0.1`'s `IConnection` rename before that): zero downstream consumers exist for v1.0.0's `OnMessageAsync` contract — it shipped hours ago and the only known integrations are pre-deployment. Calling this 2.0.0 would overstate the impact for the actual population affected.

The narrower alternative — keeping copy semantics by default and adding a separate `OnFrameAsync(ReadOnlySpan<byte>)` opt-in — was considered and rejected: it doubles the API surface for what should be the one and only inbound dispatch path, and "make the default fast" is the right policy for performance-first frameworks.

---

## Compatibility

- **Source- and binary-compatible** with v1.0.0 for all existing API signatures.
- **Semantic change** — `OnMessageAsync(message)` lifetime is now bounded by the returned task. Compiles unchanged; runtime behavior differs if the handler captured the memory out-of-band.
- **Internal change** — frame-loop buffering switched from `MemoryStream` to `ArrayBufferWriter<byte>`. Not observable from outside the package.
- **No dependency changes** — still requires `Cirreum.InvocationProvider 1.2.0+`.
- The `IConnectionSender` registration, `WebSocketConnectionSender` implementation, and ambient-accessor resolution path are all unchanged.

### Migration from 1.0.0

Most handlers need no changes — synchronous parsers, awaited dispatches, and direct forwards over an outbound socket all keep `message` alive long enough to be valid.

If your handler captures `message` into a `Task.Run`, fire-and-forget continuation, queue, or long-lived state, copy explicitly before capturing:

```diff
  public override async Task OnMessageAsync(IInvocationContext context, ReadOnlyMemory<byte> message, WebSocketMessageType type) {
-     _ = Task.Run(() => SaveAsync(message, context.Aborted));
+     var owned = message.ToArray();
+     _ = Task.Run(() => SaveAsync(owned, context.Aborted));
  }
```

The framework's per-message buffer is reused after the returned task completes — capturing `message` past that point is a use-after-reuse hazard. Most apps don't do this; if you do, the change is one line per occurrence.

---

## See also

- `CHANGELOG.md` — condensed change list for 1.1.0.
- `RELEASE-NOTES-v1.0.0.md` — initial release of the WebSocket invocation source.
- [`Cirreum.Runtime.Invocation.WebSockets`](https://www.nuget.org/packages/Cirreum.Runtime.Invocation.WebSockets) — companion L5 package; its IVA-style example uses `this.SendAsync` from 1.0.0 onward (depends on this 1.1.0 release).

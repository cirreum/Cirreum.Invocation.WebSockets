# Cirreum.Invocation.WebSockets 1.2.1 — auth slots flow through to per-message invocations

Closes a real defect that turned every inbound WebSocket message into a fresh `IApplicationUserResolver` call for audience-auth long-lived connections — IdP hammered, every message, every connection. The auth slots that ASP.NET's auth pipeline + the Cirreum forward selector + the audience-auth claims-transformer wrote onto `HttpContext.Items` during the upgrade request were getting stranded there, never reaching `IInvocationContext.Items` where consumers like `UserStateAccessor` look.

Parallel to the fix shipping in `Cirreum.Invocation.SignalR 1.2.1`.

---

## Why this release exists

Trace the lifecycle of a WebSocket connection:

```
1. Client makes HTTP upgrade request to /ws/chat
2. ASP.NET pipeline runs:
   - UseAuthentication() → forward selector stamps HttpContext.Items[AuthenticatedScheme]
   - IClaimsTransformation (audience-auth only) → ApplicationUserRoleResolverAdapter writes
     HttpContext.Items[ApplicationUserCache] = appUser
   - UseAuthorization() → policy check
3. Endpoint executor calls WebSocketOrchestrator.HandleWebSocketAsync(httpContext, ...)
4. ► Orchestrator constructs WebSocketConnection (Items = new Dictionary<>())
5. Per-message: orchestrator constructs WebSocketInvocationContext (Items = new Dictionary<>())
6. Handler runs Conductor command → UserStateAccessor.GetUser()
   → reads invocation.Items[ApplicationUserCache] → MISS (per-invocation Items is fresh)
   → calls IApplicationUserResolver again → IdP hit
```

For HTTP, this works transparently because `HttpInvocationContext.Items` aliases `HttpContext.Items` directly — same dictionary, request lifetime = invocation lifetime. For WebSocket, the per-invocation `Items` was correctly fresh per [ADR-0002 invariant #6](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md), but the auth slots that should have been seeded from connection-lifetime state never made it onto `Connection.Items` — so even per-invocation seeding wouldn't have helped.

This release fixes it in two parts.

---

## What's fixed

### 1. Upgrade-time copy onto `Connection.Items`

`WebSocketOrchestrator.HandleWebSocketAsync` now copies the two well-known auth slots from the upgrade-request `httpContext.Items` onto the freshly-constructed `WebSocketConnection.Items` immediately after construction (sits next to the existing `UpgradeItems` copy):

```csharp
if (httpContext.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
    connection.Items[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
}
if (httpContext.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
    connection.Items[AuthenticationContextKeys.ApplicationUserCache] = appUser;
}
```

Honors [ADR-0002 transport-adapter invariant #2](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/01-DESIGN.md#2-upgrade-time-items-slot-copy) which calls for exactly this copy at long-lived adapter upgrade time.

### 2. Per-invocation seed from `Connection.Items`

`WebSocketInvocationContext`'s constructor (both standard and disconnect-path overloads) now seeds the fresh per-invocation `Items` bag with the same two slots from `Connection.Items` via a private `SeedAuthSlots` helper:

```csharp
private static Dictionary<object, object?> SeedAuthSlots(WebSocketConnection connection) {
    var dict = new Dictionary<object, object?>();
    if (connection.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
        dict[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
    }
    if (connection.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
        dict[AuthenticationContextKeys.ApplicationUserCache] = appUser;
    }
    return dict;
}
```

Snapshot copy — per-message writes do NOT propagate back to `Connection.Items`, preserving per-message isolation per ADR-0002 invariant #6. Per-invocation `Items` is still genuinely a fresh dictionary; it just starts with the connection-lifetime auth slots already in place so consumers reading `invocation.Items` hit naturally.

### 3. Local `AuthenticationContextKeys` consts

A new internal `Cirreum.Invocation.WebSockets.AuthenticationContextKeys` static class duplicates the two const values that live canonically in `Cirreum.Security.AuthenticationContextKeys` (Cirreum.Core L2). Cirreum.Core is intentionally NOT added as a PackageReference — preserves the L2-peers-don't-cross-reference rule, mirrors the same workaround pattern used by `AudienceProviderRoleClaimsTransformer` in `Cirreum.Runtime.AuthorizationProvider`. Const values must match Cirreum.Core's exactly; comments on both sides note the duplication.

---

## What this means per auth shape

| Auth shape on long-lived connection | Before this release | After this release |
|---|---|---|
| **Audience (MSAL/OIDC)** with matching `IApplicationUserResolver` | Cache miss every message → resolver re-runs → IdP hammered | Cache hit on every message (seeded from upgrade); resolver runs once at upgrade |
| **Header-auth (API key / signed request)** — no resolver matches the scheme | UserStateAccessor early-returns null every message (no resolver, no work) | Same — no behavior change. The seeded scheme slot lets defense-in-depth checks read it; no resolver fires, no DB hit. |
| **Anonymous** (e.g. unauthenticated public endpoints, or Twilio-shaped flows where the WebSocket itself accepts no auth) | Same no-op | Same no-op |
| **Twilio IVA / M2M long-lived flows** (current pattern: M2M signed-request auth at upgrade, no human user) | Same no-op | Same — `ApplicationUserCache` correctly stays empty (machine-track), per-message reads cleanly return null |
| **AI/LLM act-on-behalf-of (future Piece 2)** with a null-scheme resolver | Cache write would land on per-invocation only; subsequent messages re-resolve | Foundation in place; the parallel patch to `Cirreum.Services.Server`'s `UserStateAccessor` adds the connection-bag double-write so this scenario also caches correctly when it ships |

---

## Coordinated work

Ships in lockstep with:

- **`Cirreum.Invocation.SignalR 1.2.1`** — same fix for the SignalR adapter (`InvocationContextHubFilter.OnConnectedAsync` upgrade-time copy + `SignalRInvocationContext` per-invocation seed).
- **`Cirreum.Services.Server` patch** — `UserStateAccessor` double-writes the resolved `IApplicationUser` to both per-invocation `Items` AND `invocation.Connection?.Items` on lazy resolve. Future-proofs for the AI/LLM Piece 2 seam (null-scheme resolvers); dead-code today for current resolver registrations.

---

## Compatibility

- **Source- and binary-compatible** for all consumers — no public API change.
- **Behavior-compatible** for HTTP-sourced invocations (unaffected by the original defect).
- **Behavior-changing for WebSocket-sourced invocations**: per-message `IUserStateAccessor.GetUser()` now reliably hits the cache on audience-auth long-lived connections instead of re-invoking the resolver. The change is strictly a performance fix; same resolved user, fewer IdP hits.
- No package reference changes.

---

## See also

- `CHANGELOG.md` — condensed change list.
- [`Cirreum.Invocation.SignalR 1.2.1`](https://www.nuget.org/packages/Cirreum.Invocation.SignalR) — parallel adapter fix.
- [`Cirreum.Services.Server` patch](https://www.nuget.org/packages/Cirreum.Services.Server) — `UserStateAccessor` double-write.
- [ADR-0002](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md) — the foundational seam decision; transport-adapter invariants #2 and #6 are directly relevant here.

namespace Cirreum.Invocation.WebSockets;

using Microsoft.AspNetCore.Builder;

/// <summary>
/// DI-stashed mapping from configuration instance key to the <see cref="WebSocketHandler"/>
/// <see cref="Type"/> and the resolved request-endpoint configuration registered at the
/// L5 call site
/// (<c>builder.AddInvocation(b =&gt; b.AddWebSocket&lt;THandler&gt;("voice", request: r =&gt; r.Map(...)))</c>).
/// Resolved by <see cref="WebSocketInvocationRegistrar.MapSource"/> during the endpoints
/// phase to wire up the WebSocket endpoint and the optional request endpoint for each
/// instance.
/// </summary>
/// <remarks>
/// Lives at L3 because both producers (the L5 <c>AddWebSocket&lt;THandler&gt;</c> extension)
/// and consumers (this package's registrar) need to reference it. L5 references L3,
/// so this is the natural shared home.
/// </remarks>
/// <param name="InstanceKey">The configuration instance key.</param>
/// <param name="HandlerType">The concrete <see cref="WebSocketHandler"/> type.</param>
/// <param name="RequestHandler">
/// Optional minimal API delegate for the <c>RequestPath</c> HTTP endpoint. When provided
/// alongside a <c>RequestPath</c> in configuration, the registrar maps this delegate with
/// full minimal API parameter injection (DI, HttpContext, route parameters, etc.).
/// <see langword="null"/> when no request endpoint is needed.
/// </param>
/// <param name="RequestMethod">
/// HTTP method the request endpoint accepts. <see langword="null"/> resolves to
/// <c>"POST"</c> at map time — the natural default for webhook-style request flows
/// (Twilio, Stripe, GitHub all use POST). Apps override via the request builder's
/// explicit-method <c>Map</c> overload.
/// </param>
/// <param name="ConfigureRequestRoute">
/// Optional callback giving the app access to the request endpoint's
/// <see cref="RouteHandlerBuilder"/>. Used internally to chain <c>WithName()</c>,
/// <c>WithTags()</c>, <c>Produces&lt;T&gt;()</c>, <c>WithOpenApi()</c>, and any other
/// minimal API customizations — particularly useful for surfacing the request endpoint
/// in OpenAPI / Swagger documentation.
/// </param>
public sealed record WebSocketHandlerMapping(
	string InstanceKey,
	Type HandlerType,
	Delegate? RequestHandler = null,
	string? RequestMethod = null,
	Action<RouteHandlerBuilder>? ConfigureRequestRoute = null);

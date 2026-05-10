namespace Cirreum.Invocation.WebSockets;

/// <summary>
/// Local copy of the well-known authentication context keys defined canonically in
/// <c>Cirreum.Security.AuthenticationContextKeys</c> (Cirreum.Core L2). This package
/// does not reference Cirreum.Core to preserve the L2-peers-don't-cross-reference rule,
/// so the const values are duplicated here. Values must match Cirreum.Core's exactly.
/// </summary>
internal static class AuthenticationContextKeys {

	/// <summary>
	/// Mirror of <c>Cirreum.Security.AuthenticationContextKeys.AuthenticatedScheme</c>.
	/// Stamped on the upgrade-time <c>HttpContext.Items</c> by the dynamic-scheme forward
	/// selector for ALL auth shapes (audience, header, anonymous). Copied onto
	/// <c>WebSocketConnection.Items</c> at <c>WebSocketOrchestrator.HandleWebSocketAsync</c>
	/// and seeded onto per-invocation <c>IInvocationContext.Items</c> at
	/// <c>WebSocketInvocationContext</c> construction so consumers of the per-invocation bag
	/// (e.g. <c>UserStateAccessor</c>) read identically across HTTP and WebSocket.
	/// </summary>
	public const string AuthenticatedScheme = "__Cirreum_AuthenticatedScheme";

	/// <summary>
	/// Mirror of <c>Cirreum.Security.AuthenticationContextKeys.ApplicationUserCache</c>.
	/// Written on the upgrade-time <c>HttpContext.Items</c> by the audience-auth role
	/// claims transformer when an <c>IApplicationUserResolver</c> matches; absent for
	/// header-auth and anonymous flows (no resolver runs). Same upgrade-time copy + per-
	/// invocation seed pattern as <see cref="AuthenticatedScheme"/>.
	/// </summary>
	public const string ApplicationUserCache = "__Cirreum_ApplicationUser";

}

namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using System.Security.Claims;

/// <summary>
/// <see cref="IInvocationContext"/> for WebSocket-sourced invocations. Carries the
/// per-message snapshot of the authenticated principal, the per-invocation DI scope, the
/// invocation cancellation token, and the parent <see cref="IInvocationConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Items"/> is a fresh per-invocation dictionary — distinct from the
/// per-connection <see cref="IInvocationConnection.Items"/>. Consumers that need state
/// outliving a single WebSocket message should write to
/// <c>Connection.Items</c>, not here.
/// </para>
/// <para>
/// Used both for in-flight message invocations (via the WebSocket middleware's frame loop)
/// and for synthetic invocation scopes around connection lifecycle hooks
/// (<c>OnConnectedAsync</c> / <c>OnDisconnectedAsync</c>) so consumers like
/// <c>IUserStateAccessor</c> work normally inside <see cref="IConnectionLifecycle"/>
/// callbacks — see ADR-0002 transport-adapter invariant #7.
/// </para>
/// <para>
/// During disconnect, the framework constructs the context with an explicit cleanup-budget
/// token (via the internal constructor overload) so that <see cref="Aborted"/> reflects
/// the bounded cleanup window — matching what the handler's <c>OnDisconnectedAsync</c>
/// parameter receives. Services that resolve <c>IInvocationContextAccessor.Current.Aborted</c>
/// during cleanup get the same bounded budget rather than the connection's already-canceled
/// token.
/// </para>
/// </remarks>
internal sealed class WebSocketInvocationContext : IInvocationContext {

	/// <summary>
	/// Standard constructor — <see cref="Aborted"/> tracks the connection's cancellation.
	/// Used for in-flight messages and the connect synthetic scope.
	/// </summary>
	internal WebSocketInvocationContext(
		WebSocketConnection connection,
		IServiceProvider services)
		: this(connection, services, connection.Aborted) {
	}

	/// <summary>
	/// Disconnect-path constructor — <see cref="Aborted"/> reflects the explicit cleanup
	/// budget rather than the connection's (already-canceled) token. The framework uses
	/// this overload during the disconnect synthetic scope so ambient consumers get the
	/// same bounded cleanup window the handler's <c>OnDisconnectedAsync(DisconnectInfo, CancellationToken)</c>
	/// parameter receives.
	/// </summary>
	internal WebSocketInvocationContext(
		WebSocketConnection connection,
		IServiceProvider services,
		CancellationToken aborted) {

		this.User = connection.User;
		this.Services = services;
		this.Aborted = aborted;
		this.Connection = connection;
	}

	public ClaimsPrincipal User { get; }

	public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

	public IServiceProvider Services { get; }

	public CancellationToken Aborted { get; }

	public string InvocationSource => InvocationSources.WebSocket;

	public IInvocationConnection? Connection { get; }

}

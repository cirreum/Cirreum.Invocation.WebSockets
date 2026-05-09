namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;

/// <summary>
/// Base class for application-defined WebSocket message handlers. One instance is created
/// per connection (resolved from DI as a scoped service within the per-connection scope).
/// The framework calls <see cref="OnMessageAsync"/> for each complete WebSocket message,
/// with a per-message <see cref="IInvocationContext"/> published through
/// <see cref="IInvocationContextAccessor"/> for the duration of the call and passed
/// directly into the method.
/// </summary>
/// <remarks>
/// <para>
/// Supports a two-phase connection model: an optional HTTP upgrade endpoint (configured via
/// <c>UpgradePath</c> + a minimal API delegate at the <c>AddWebSocket</c> call site) for
/// pre-connection negotiation, followed by a WebSocket endpoint at <c>Path</c> where
/// <see cref="OnAcceptAsync"/> runs as a pre-accept gate before the connection is established.
/// </para>
/// <para>
/// Subclasses receive raw bytes and the WebSocket message type — the framework performs no
/// deserialization. This keeps the contract minimal and allows apps to use any wire format
/// (JSON, Protobuf, raw binary audio, etc.).
/// </para>
/// <para>
/// <see cref="OnConnectedAsync"/> and <see cref="OnDisconnectedAsync"/> run inside synthetic
/// invocation scopes so that <c>IUserStateAccessor</c> and other ambient consumers work
/// normally (ADR-0002 transport-adapter invariant #7).
/// </para>
/// </remarks>
public abstract class WebSocketHandler {

	/// <summary>
	/// The active connection for this handler instance. Set by the framework after the
	/// WebSocket is accepted, before <see cref="OnConnectedAsync"/> is called.
	/// <see langword="null"/> during <see cref="OnAcceptAsync"/> and
	/// <see cref="OnSelectSubProtocolAsync"/> (the connection doesn't exist yet).
	/// </summary>
	public IInvocationConnection? Connection { get; internal set; }

	/// <summary>
	/// The negotiated WebSocket subprotocol for this connection. Set by the framework
	/// after the WebSocket is accepted; reflects the value returned by
	/// <see cref="OnSelectSubProtocolAsync"/>, or <see langword="null"/> if no subprotocol
	/// was negotiated. <see langword="null"/> during <see cref="OnAcceptAsync"/> and
	/// <see cref="OnSelectSubProtocolAsync"/>.
	/// </summary>
	public string? SubProtocol { get; internal set; }

	/// <summary>
	/// Per-upgrade state bag populated by the handler during <see cref="OnAcceptAsync"/>.
	/// The framework copies these entries into <see cref="IInvocationConnection.Items"/>
	/// after the WebSocket is accepted, making them available for the connection's
	/// lifetime. Use this to bridge context from the HTTP upgrade request (query parameters,
	/// session tokens, etc.) into the WebSocket connection.
	/// </summary>
	protected internal IDictionary<object, object?> UpgradeItems { get; } = new Dictionary<object, object?>();

	/// <summary>
	/// Called on the WebSocket endpoint (<c>Path</c>) before the WebSocket is accepted.
	/// Inspect query parameters, validate session tokens, or reject bad clients.
	/// Populate <see cref="UpgradeItems"/> to flow state into the connection.
	/// </summary>
	/// <remarks>
	/// Return <see langword="false"/> to reject the connection. The handler should set an
	/// appropriate HTTP status code on <see cref="HttpContext.Response"/> before returning
	/// <see langword="false"/>; the framework defaults to 400 if no status is set.
	/// </remarks>
	/// <param name="context">The HTTP upgrade request context.</param>
	/// <returns>
	/// <see langword="true"/> to accept the WebSocket connection;
	/// <see langword="false"/> to reject it.
	/// </returns>
	public virtual Task<bool> OnAcceptAsync(HttpContext context) =>
		Task.FromResult(true);

	/// <summary>
	/// Optional override to negotiate a WebSocket subprotocol. Read the requested
	/// subprotocols from <c>context.WebSockets.WebSocketRequestedProtocols</c> and return
	/// the chosen one. Only called if <see cref="OnAcceptAsync"/> returned
	/// <see langword="true"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The returned value <strong>must</strong> be one of the values in
	/// <c>WebSocketRequestedProtocols</c> or <see langword="null"/> — returning a value
	/// the client did not request will cause <c>AcceptWebSocketAsync</c> to throw, failing
	/// the upgrade.
	/// </para>
	/// <para>
	/// After accept, the negotiated value is exposed via <see cref="SubProtocol"/> for the
	/// remainder of the connection's lifetime.
	/// </para>
	/// </remarks>
	/// <param name="context">The HTTP upgrade request context.</param>
	/// <returns>
	/// The chosen subprotocol (must appear in <c>WebSocketRequestedProtocols</c>), or
	/// <see langword="null"/> for no subprotocol negotiation.
	/// </returns>
	public virtual Task<string?> OnSelectSubProtocolAsync(HttpContext context) =>
		Task.FromResult<string?>(null);

	/// <summary>
	/// Called once after the WebSocket is accepted and the connection is established.
	/// Runs inside a synthetic invocation scope. Override to perform per-connection setup.
	/// <see cref="Connection"/> and <see cref="SubProtocol"/> are available at this point.
	/// </summary>
	/// <param name="cancellationToken">Fires when the connection is aborted.</param>
	public virtual Task OnConnectedAsync(CancellationToken cancellationToken) =>
		Task.CompletedTask;

	/// <summary>
	/// Called for each complete WebSocket message received on the connection.
	/// </summary>
	/// <param name="context">
	/// The per-message <see cref="IInvocationContext"/> — exposes <c>User</c>, <c>Items</c>
	/// (per-message bag), <c>Services</c> (per-message DI scope), and <c>Aborted</c> (the
	/// connection's cancellation token). Same instance the ambient
	/// <see cref="IInvocationContextAccessor.Current"/> resolves to during this call.
	/// </param>
	/// <param name="message">The raw message payload.</param>
	/// <param name="messageType">The WebSocket message type (Text or Binary).</param>
	public abstract Task OnMessageAsync(
		IInvocationContext context,
		ReadOnlyMemory<byte> message,
		WebSocketMessageType messageType);

	/// <summary>
	/// Called once after the WebSocket closes or aborts, before connection resources are
	/// disposed. Runs inside a synthetic invocation scope. Override to perform per-connection
	/// cleanup. Receives a <see cref="DisconnectInfo"/> describing whether the close was
	/// graceful, any reported exception, and a human-readable reason — use this to
	/// distinguish error vs. normal disconnects when computing dispositions, metrics, etc.
	/// </summary>
	/// <param name="info">
	/// Adapter-populated disconnect circumstances. <see cref="DisconnectInfo.WasGraceful"/>
	/// is <see langword="true"/> for clean, peer-initiated closes; <see langword="false"/>
	/// when the frame loop exited due to an exception, host shutdown, or abort.
	/// </param>
	/// <param name="cancellationToken">
	/// Bounded cleanup budget — <strong>not</strong> the connection's cancellation. Fires on
	/// either the configured disconnect timeout (default 30s) or host shutdown
	/// (<c>IHostApplicationLifetime.ApplicationStopping</c>). Pass directly into
	/// cancellable cleanup calls (close downstream sockets, flush metrics, persist final
	/// state) to ensure they don't hang the framework's connection teardown.
	/// </param>
	public virtual Task OnDisconnectedAsync(DisconnectInfo info, CancellationToken cancellationToken) =>
		Task.CompletedTask;

}

namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Text.Json;

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

	private static readonly JsonSerializerOptions _defaultSerializerOptions = new(JsonSerializerDefaults.Web);

	/// <summary>
	/// JSON serializer options used by the typed <see cref="SendAsync{T}(T, CancellationToken)"/>
	/// and <see cref="SendAsync{T}(string, T, CancellationToken)"/> overloads. Defaults to
	/// <c>new JsonSerializerOptions(JsonSerializerDefaults.Web)</c> — reflection-based,
	/// camelCase, web-compatible.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Override to provide a source-generated <c>JsonTypeInfoResolver</c> for AOT/trim-friendly
	/// apps, performance-critical paths (no reflection at runtime), or custom naming/ignore
	/// policies. Cache the override in a static field so the same instance is returned for
	/// the connection's lifetime.
	/// </para>
	/// <para>
	/// Example:
	/// <code>
	/// [JsonSourceGenerationOptions(
	///     PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	///     DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	/// [JsonSerializable(typeof(TwilioMediaMessage))]
	/// [JsonSerializable(typeof(TwilioMarkMessage))]
	/// public sealed partial class TwilioJsonContext : JsonSerializerContext { }
	///
	/// public sealed class TwilioMediaHandler : WebSocketHandler {
	///     private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) {
	///         TypeInfoResolver = TwilioJsonContext.Default
	///     };
	///     protected override JsonSerializerOptions SerializerOptions =&gt; _options;
	///     // ...
	/// }
	/// </code>
	/// </para>
	/// </remarks>
	protected virtual JsonSerializerOptions SerializerOptions => _defaultSerializerOptions;


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
	/// <remarks>
	/// <para>
	/// <strong>Lifetime contract for <paramref name="message"/>:</strong> the bytes are
	/// borrowed from the framework's per-connection pooled buffer. They are valid only
	/// for the duration of the returned <see cref="Task"/>'s execution. After the task
	/// completes, the framework reuses the buffer for the next inbound message.
	/// </para>
	/// <para>
	/// Practical implications:
	/// </para>
	/// <list type="bullet">
	///   <item>Synchronous use — including <c>await</c>-points inside this method — is
	///         safe. The framework awaits your returned task before clearing.</item>
	///   <item>Do <strong>not</strong> capture <paramref name="message"/> into
	///         <c>Task.Run(...)</c>, fire-and-forget continuations, queues, or
	///         long-lived state. The buffer will be reused.</item>
	///   <item>If you need to retain the bytes beyond this call (enqueue for later
	///         processing, etc.), copy them: <c>message.ToArray()</c> or
	///         <c>message.Span.CopyTo(...)</c>.</item>
	///   <item>Most parsers (<c>JsonDocument.Parse</c>, <c>System.Text.Json</c>,
	///         <c>MessagePackSerializer.Deserialize</c>) consume the span synchronously
	///         and produce their own owned representations — no copy needed.</item>
	/// </list>
	/// <para>
	/// This contract eliminates a per-message <see cref="byte"/>[] allocation in the
	/// framework's hot path — important for high-frequency workloads (voice/realtime,
	/// telemetry).
	/// </para>
	/// </remarks>
	/// <param name="context">
	/// The per-message <see cref="IInvocationContext"/> — exposes <c>User</c>, <c>Items</c>
	/// (per-message bag), <c>Services</c> (per-message DI scope), and <c>Aborted</c> (the
	/// connection's cancellation token). Same instance the ambient
	/// <see cref="IInvocationContextAccessor.Current"/> resolves to during this call.
	/// </param>
	/// <param name="message">
	/// The raw message payload. <strong>Borrowed</strong> from the framework's pooled
	/// buffer — valid only until the returned task completes. See remarks.
	/// </param>
	/// <param name="messageType">The WebSocket message type (Text or Binary).</param>
	public abstract Task OnMessageAsync(IInvocationContext context, ReadOnlyMemory<byte> message, WebSocketMessageType messageType);


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



	/// <summary>
	/// Push a payload to the calling client over the active WebSocket connection as a
	/// <see cref="WebSocketMessageType.Text"/> frame. JSON-serialization uses
	/// <see cref="SerializerOptions"/> — override that property to wire in a
	/// source-generated <c>JsonTypeInfoResolver</c> for AOT/trim-friendly apps.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Connection-bound: works from <strong>any</strong> calling context — handler
	/// lifecycle hooks (<see cref="OnConnectedAsync"/>, <see cref="OnMessageAsync"/>,
	/// <see cref="OnDisconnectedAsync"/>) AND handler-managed background tasks
	/// (outbound socket receive loops, timers, fire-and-forget continuations) — because
	/// it dispatches directly through the captured <see cref="Connection"/> rather than
	/// through the ambient <see cref="IInvocationContextAccessor"/>.
	/// </para>
	/// <para>
	/// Use this for handler-bound push (the typical case for raw WebSocket apps that
	/// own outbound state and forward responses to the inbound caller). For
	/// transport-agnostic push from cross-cutting code (Conductor command handlers,
	/// validators, services that may run under HTTP, SignalR, or WebSocket), inject
	/// <see cref="IConnectionSender"/> instead — it resolves the active connection
	/// from the ambient invocation context.
	/// </para>
	/// </remarks>
	/// <typeparam name="T">Payload type.</typeparam>
	/// <param name="payload">The payload to JSON-serialize and send.</param>
	/// <param name="cancellationToken">Cancellation token for the send.</param>
	/// <exception cref="InvalidOperationException">
	/// <see cref="Connection"/> is <see langword="null"/> (called before
	/// <see cref="OnConnectedAsync"/> or after <see cref="OnDisconnectedAsync"/>).
	/// </exception>
	protected ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
		var ws = this.ResolveWebSocket();
		var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, this.SerializerOptions);
		return ws.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	/// <summary>
	/// Push a payload addressed to a specific method/event name as a
	/// <see cref="WebSocketMessageType.Text"/> frame. Wraps the payload in a
	/// <c>{ "method": "...", "payload": ... }</c> envelope for apps implementing their
	/// own method-dispatch protocol over WebSocket. JSON-serialization uses
	/// <see cref="SerializerOptions"/>.
	/// </summary>
	/// <remarks>
	/// Same connection-bound semantics as <see cref="SendAsync{T}(T, CancellationToken)"/>
	/// — works from lifecycle hooks AND background tasks.
	/// </remarks>
	protected ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
		ArgumentException.ThrowIfNullOrWhiteSpace(method);
		var ws = this.ResolveWebSocket();
		var envelope = new { method, payload };
		var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, this.SerializerOptions);
		return ws.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	/// <summary>
	/// Push raw bytes as a Binary frame. Use for binary protocols (audio, telemetry,
	/// pre-serialized payloads). Bypasses JSON serialization entirely.
	/// </summary>
	/// <remarks>
	/// Same connection-bound semantics as the typed overloads.
	/// </remarks>
	protected ValueTask SendAsync(
		ReadOnlyMemory<byte> payload,
		WebSocketMessageType messageType = WebSocketMessageType.Binary,
		CancellationToken cancellationToken = default) {

		var ws = this.ResolveWebSocket();
		return ws.SendAsync(
			payload,
			messageType,
			endOfMessage: true,
			cancellationToken);
	}

	private WebSocket ResolveWebSocket() {
		if (this.Connection is not WebSocketConnection wsConnection) {
			throw new InvalidOperationException(
				$"WebSocketHandler.SendAsync called before Connection was established. " +
				$"Connection is null during OnAcceptAsync and OnSelectSubProtocolAsync; " +
				$"it is set before OnConnectedAsync runs and remains valid through OnDisconnectedAsync.");
		}
		return wsConnection.WebSocket;
	}

}

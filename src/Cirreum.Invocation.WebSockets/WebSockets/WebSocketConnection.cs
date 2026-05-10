namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation.Connections;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;

/// <summary>
/// <see cref="IWebSocketConnection"/> for a single long-lived WebSocket connection.
/// Wraps ASP.NET's <see cref="WebSocket"/> and the originating HTTP context
/// as the unified seam for per-connection state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="User"/> is snapshotted at upgrade time — immutable per the L2 contract.
/// WebSocket upgrade inherits the HTTP request's authenticated principal, so the
/// principal is effectively connection-scoped from the framework's perspective.
/// </para>
/// <para>
/// <see cref="IInvocationConnection.Items"/> is a fresh dictionary, distinct from the HTTP context's Items.
/// Per-connection state set by the handler or framework code flows through here.
/// </para>
/// <para>
/// The typed <c>SendAsync&lt;T&gt;</c> overloads serialize through a captured
/// <see cref="JsonSerializerOptions"/> instance — sourced from the active
/// <see cref="WebSocketHandler.SerializerOptions"/> at construction time. This means
/// cross-cutting code reaching this connection through
/// <see cref="IInvocationContextAccessor.Current"/> automatically uses the same
/// serializer the handler is configured with (including any source-generated
/// <c>JsonTypeInfoResolver</c> the app set up for AOT/trim-friendly apps).
/// </para>
/// </remarks>
internal sealed partial class WebSocketConnection : IWebSocketConnection, IAsyncDisposable {

	private readonly CancellationTokenSource _abortCts;
	private readonly ILogger<WebSocketConnection> _logger;
	private readonly JsonSerializerOptions _serializerOptions;

	internal WebSocketConnection(
		string connectionId,
		ClaimsPrincipal user,
		WebSocket webSocket,
		DateTimeOffset connectedAtUtc,
		JsonSerializerOptions serializerOptions,
		ILogger<WebSocketConnection> logger,
		CancellationToken requestAborted) {

		this.ConnectionId = connectionId;
		this.User = user;
		this.WebSocket = webSocket;
		this.ConnectedAtUtc = connectedAtUtc;
		this._serializerOptions = serializerOptions;
		this._logger = logger;
		this._abortCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
	}

	public string ConnectionId { get; }

	public ClaimsPrincipal User { get; }

	public DateTimeOffset ConnectedAtUtc { get; }

	public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

	public string InvocationSource => InvocationSources.WebSocket;

	public CancellationToken Aborted => this._abortCts.Token;

	public void Abort() {
		try {
			this._abortCts.Cancel();
		} catch (ObjectDisposedException) {
			// Already disposed — racing with DisposeAsync.
		}
	}

	public ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
		var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, this._serializerOptions);
		return this.WebSocket.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	public ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
		ArgumentException.ThrowIfNullOrWhiteSpace(method);
		var envelope = new { method, payload };
		var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, this._serializerOptions);
		return this.WebSocket.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	public ValueTask SendBytesAsync(
		ReadOnlyMemory<byte> bytes,
		WebSocketMessageType messageType = WebSocketMessageType.Binary,
		CancellationToken cancellationToken = default) {

		return this.WebSocket.SendAsync(
			bytes,
			messageType,
			endOfMessage: true,
			cancellationToken);
	}

	/// <summary>
	/// The underlying WebSocket. Internal — not exposed on
	/// <see cref="IWebSocketConnection"/>; use <see cref="SendBytesAsync"/> for raw
	/// frame writes.
	/// </summary>
	internal WebSocket WebSocket { get; }

	public async ValueTask DisposeAsync() {
		this._abortCts.Cancel();
		this._abortCts.Dispose();

		var state = this.WebSocket.State;
		if (state is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent) {
			try {
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await this.WebSocket.CloseOutputAsync(
					WebSocketCloseStatus.NormalClosure,
					"Connection closing",
					timeout.Token);
			} catch (Exception ex) {
				// Best-effort close; the socket may already be faulted, the peer may be gone,
				// or the close handshake may have timed out. Log and move on — DisposeAsync
				// must not throw, and this isn't actionable for the framework.
				LogCloseFailed(this._logger, this.ConnectionId, state, ex);
			}
		}

		this.WebSocket.Dispose();
	}

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Warning,
		Message = "Graceful close failed for WebSocket connection {ConnectionId} (state: {State}). The socket will be disposed regardless.")]
	private static partial void LogCloseFailed(
		ILogger logger,
		string connectionId,
		WebSocketState state,
		Exception exception);

}

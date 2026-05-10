namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation;
using Cirreum.Invocation.Configuration;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.WebSockets;

/// <summary>
/// Per-connection driver that takes a WebSocket-upgrade HTTP request and runs the full
/// connection lifecycle against the app's <see cref="WebSocketHandler"/>: pre-accept
/// gate (<see cref="WebSocketHandler.OnAcceptAsync"/>), subprotocol negotiation
/// (<see cref="WebSocketHandler.OnSelectSubProtocolAsync"/>), upgrade, connect hooks,
/// frame receive loop, and disconnect hooks. Manages the per-connection DI scope, the
/// per-message DI scopes (one per complete message), the synthetic invocation scopes
/// around lifecycle hooks, and the bounded cleanup CTS for disconnect.
/// </summary>
/// <remarks>
/// <para>
/// Internal — apps interact only via <see cref="WebSocketHandler"/>. The orchestrator is
/// the bridge between ASP.NET's WebSocket and the Cirreum handler contract.
/// </para>
/// <para>
/// Unlike SignalR (which provides per-invocation DI scopes via its built-in hub pipeline),
/// raw WebSocket requires explicit scope creation per message. This orchestrator creates
/// a new <see cref="IServiceScope"/> for each complete message, mirroring the per-method-
/// invocation scope guarantee that SignalR provides natively (ADR-0002 invariant #1).
/// </para>
/// <para>
/// Multi-frame messages are accumulated into a contiguous buffer before dispatch.
/// Per-instance limits (max message size, receive buffer size, disconnect timeout,
/// keep-alive interval) come from <see cref="WebSocketInvocationInstanceSettings"/>.
/// </para>
/// </remarks>
internal sealed partial class WebSocketOrchestrator(
	IInvocationContextAccessor accessor,
	IServiceScopeFactory scopeFactory,
	IHostApplicationLifetime appLifetime,
	TimeProvider timeProvider,
	ILogger<WebSocketOrchestrator> logger
) {

	/// <summary>
	/// Handles the WebSocket endpoint. Runs the pre-accept gate, accepts the
	/// WebSocket, and enters the frame loop.
	/// </summary>
	internal async Task HandleWebSocketAsync(
		HttpContext httpContext,
		Type handlerType,
		WebSocketInvocationInstanceSettings settings,
		CancellationToken cancellationToken) {

		// Create a connection-scoped DI scope for the handler's lifetime.
		await using var connectionScope = scopeFactory.CreateAsyncScope();
		var handler = (WebSocketHandler)connectionScope.ServiceProvider.GetRequiredService(handlerType);

		// Pre-accept gate — let the app inspect query params, validate tokens, reject.
		var accepted = await handler.OnAcceptAsync(httpContext);
		if (!accepted) {
			if (!httpContext.Response.HasStarted) {
				httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
			}
			LogPreAcceptRejected(logger, handlerType.Name, httpContext.Request.Path);
			return;
		}

		if (!httpContext.WebSockets.IsWebSocketRequest) {
			httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		// Optional subprotocol negotiation. The handler returns one of
		// httpContext.WebSockets.WebSocketRequestedProtocols (or null for no subprotocol).
		var selectedSubProtocol = await handler.OnSelectSubProtocolAsync(httpContext);

		// Build the accept context. Keep-alive fields are only set when the instance
		// configures them — otherwise we inherit the global UseWebSockets defaults.
		var acceptContext = new WebSocketAcceptContext {
			SubProtocol = selectedSubProtocol
		};
		if (settings.KeepAliveInterval is { } keepAliveInterval) {
			acceptContext.KeepAliveInterval = keepAliveInterval;
		}
		if (settings.KeepAliveTimeout is { } keepAliveTimeout) {
			acceptContext.KeepAliveTimeout = keepAliveTimeout;
		}

		using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync(acceptContext);
		var connectionId = Guid.NewGuid().ToString("N");
		var connectionLogger = connectionScope.ServiceProvider.GetRequiredService<ILogger<WebSocketConnection>>();

		var connection = new WebSocketConnection(
			connectionId,
			httpContext.User,
			webSocket,
			timeProvider.GetUtcNow(),
			handler.SerializerOptions,
			connectionLogger,
			cancellationToken);

		var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
		LogConnectionAccepted(logger, connectionId, handlerType.Name, address);

		// Copy UpgradeItems into Connection.Items.
		foreach (var kvp in handler.UpgradeItems) {
			connection.Items[kvp.Key] = kvp.Value;
		}

		// Give the handler a reference to its connection and the negotiated subprotocol.
		handler.Connection = connection;
		handler.SubProtocol = webSocket.SubProtocol;

		await using (connection) {
			await this.RunConnectionAsync(connection, connectionScope.ServiceProvider, handler, settings, cancellationToken);
		}

	}

	private async Task RunConnectionAsync(
		WebSocketConnection connection,
		IServiceProvider services,
		WebSocketHandler handler,
		WebSocketInvocationInstanceSettings settings,
		CancellationToken cancellationToken) {

		// OnConnected — synthetic invocation scope (ADR-0002 invariant #7).
		var connectAccepted = await this.RunOnConnectedAsync(connection, services, handler);
		if (!connectAccepted) {
			LogLifecycleRejectedUpgrade(logger, connection.ConnectionId);
			return;
		}

		// Frame receive loop.
		Exception? loopException = null;
		try {
			await this.RunFrameLoopAsync(connection, handler, settings);
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// Host shutdown — treat as graceful.
		} catch (Exception ex) {
			loopException = ex;
			LogFrameLoopException(logger, connection.ConnectionId, ex);
		}

		var duration = timeProvider.GetUtcNow() - connection.ConnectedAtUtc;
		var wasGraceful = loopException is null;
		LogConnectionClosed(logger, connection.ConnectionId, wasGraceful, duration);

		// OnDisconnected — synthetic invocation scope.
		await this.RunOnDisconnectedAsync(connection, services, handler, settings, loopException);
	}

	private async Task<bool> RunOnConnectedAsync(
		WebSocketConnection connection,
		IServiceProvider services,
		WebSocketHandler handler) {

		var invocation = new WebSocketInvocationContext(connection, services);
		accessor.Set(invocation);
		try {
			var lifecycles = services.GetServices<IConnectionLifecycle>();
			foreach (var lifecycle in lifecycles) {
				var accepted = await lifecycle.OnConnectedAsync(connection, connection.Aborted);
				if (!accepted) {
					return false;
				}
			}

			await handler.OnConnectedAsync(connection.Aborted);
			return true;
		} finally {
			accessor.Clear();
		}
	}

	private async Task RunFrameLoopAsync(
		WebSocketConnection connection,
		WebSocketHandler handler,
		WebSocketInvocationInstanceSettings settings) {

		// Receive buffer — pooled per-connection, fixed size (configurable per instance).
		var receiveBuffer = ArrayPool<byte>.Shared.Rent(settings.ReceiveBufferSizeBytes);

		// Message accumulator — write-only buffer that grows as multi-frame messages
		// are stitched together. ArrayBufferWriter<byte> is purpose-built for this:
		// fewer fields touched per Write than MemoryStream, and WrittenMemory is a
		// borrowed view (no per-message allocation) that we hand straight to
		// OnMessageAsync. Reset via Clear() between messages — keeps the internal
		// array; just resets WrittenCount to 0.
		var messageBuffer = new ArrayBufferWriter<byte>(settings.ReceiveBufferSizeBytes);

		try {
			while (connection.WebSocket.State == WebSocketState.Open
				&& !connection.Aborted.IsCancellationRequested) {

				var result = await connection.WebSocket.ReceiveAsync(
					receiveBuffer.AsMemory(),
					connection.Aborted);

				if (result.MessageType == WebSocketMessageType.Close) {
					break;
				}

				messageBuffer.Write(receiveBuffer.AsSpan(0, result.Count));

				if (messageBuffer.WrittenCount > settings.MaxMessageSizeBytes) {
					LogMessageSizeExceeded(logger, connection.ConnectionId, messageBuffer.WrittenCount, settings.MaxMessageSizeBytes);
					await connection.WebSocket.CloseAsync(
						WebSocketCloseStatus.MessageTooBig,
						"Message exceeds maximum size",
						CancellationToken.None);
					break;
				}

				if (!result.EndOfMessage) {
					continue;
				}

				// Borrow semantics: hand WrittenMemory to the handler. Valid for the
				// duration of the awaited DispatchMessageAsync; after it returns,
				// Clear() resets WrittenCount and the same backing array is reused.
				// Handlers that need to retain the bytes copy in OnMessageAsync.
				await this.DispatchMessageAsync(
					connection,
					handler,
					messageBuffer.WrittenMemory,
					result.MessageType);

				messageBuffer.Clear();
			}
		} finally {
			ArrayPool<byte>.Shared.Return(receiveBuffer);
		}
	}

	private async Task DispatchMessageAsync(
		WebSocketConnection connection,
		WebSocketHandler handler,
		ReadOnlyMemory<byte> message,
		WebSocketMessageType messageType) {

		await using var scope = scopeFactory.CreateAsyncScope();
		var invocation = new WebSocketInvocationContext(connection, scope.ServiceProvider);
		accessor.Set(invocation);
		try {
			await handler.OnMessageAsync(invocation, message, messageType);
		} finally {
			accessor.Clear();
		}
	}

	private async Task RunOnDisconnectedAsync(
		WebSocketConnection connection,
		IServiceProvider services,
		WebSocketHandler handler,
		WebSocketInvocationInstanceSettings settings,
		Exception? exception) {

		var wasGraceful = exception is null
			&& connection.WebSocket.CloseStatus == WebSocketCloseStatus.NormalClosure;

		var info = new DisconnectInfo(
			WasGraceful: wasGraceful,
			Exception: exception,
			Reason: connection.WebSocket.CloseStatusDescription ?? exception?.Message);

		// Cancel the connection's own cancellation. The frame loop has already exited
		// by this point; this is mostly for any code still holding connection.Aborted.
		// We pass a SEPARATE bounded-cleanup token to disconnect hooks below.
		connection.Abort();

		// Bounded cleanup budget for disconnect hooks. Fires on either the cleanup
		// timeout (configured per-instance) or host shutdown
		// (IHostApplicationLifetime.ApplicationStopping). Hooks pass this into their
		// cancellable cleanup calls (downstream socket close, metrics flush, final domain
		// writes) so a hung downstream can't block teardown.
		var cleanupBudget = TimeSpan.FromSeconds(settings.DisconnectTimeoutSeconds);
		using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(
			appLifetime.ApplicationStopping);
		cleanupCts.CancelAfter(cleanupBudget);

		// IInvocationContext.Aborted during disconnect reflects the cleanup budget — same
		// token the handler's OnDisconnectedAsync parameter sees, so ambient consumers
		// (IInvocationContextAccessor.Current) stay coherent with explicit parameters.
		var invocation = new WebSocketInvocationContext(connection, services, cleanupCts.Token);
		accessor.Set(invocation);
		try {
			try {
				await handler.OnDisconnectedAsync(info, cleanupCts.Token);
			} catch {
				// Per L2 contract: exceptions from disconnect hooks are absorbed.
			}

			var lifecycles = services.GetServices<IConnectionLifecycle>();
			foreach (var lifecycle in lifecycles) {
				try {
					await lifecycle.OnDisconnectedAsync(connection, info, cleanupCts.Token);
				} catch {
					// Per L2 contract: exceptions from disconnect hooks are absorbed.
				}
			}
		} finally {
			accessor.Clear();
		}

		// Distinguish "we hit the cleanup timeout" from "host is shutting down" — the
		// former is a perf signal worth logging, the latter is expected and quiet.
		if (cleanupCts.IsCancellationRequested
			&& !appLifetime.ApplicationStopping.IsCancellationRequested) {
			LogCleanupBudgetExceeded(logger, connection.ConnectionId, cleanupBudget);
		}
	}

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Warning,
		Message = "WebSocket frame loop for connection {ConnectionId} exited with an unhandled exception.")]
	private static partial void LogFrameLoopException(
		ILogger logger,
		string connectionId,
		Exception exception);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Warning,
		Message = "WebSocket connection {ConnectionId} sent a message of {MessageSize} bytes, exceeding the {MaxSize} byte limit. Closing with MessageTooBig.")]
	private static partial void LogMessageSizeExceeded(
		ILogger logger,
		string connectionId,
		long messageSize,
		int maxSize);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Warning,
		Message = "WebSocket connection {ConnectionId} disconnect cleanup did not complete within the {Budget} budget. Hooks were canceled.")]
	private static partial void LogCleanupBudgetExceeded(
		ILogger logger,
		string connectionId,
		TimeSpan budget);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Debug,
		Message = "WebSocket connection {ConnectionId} accepted for handler {HandlerType} from {RemoteAddress}.")]
	private static partial void LogConnectionAccepted(
		ILogger logger,
		string connectionId,
		string handlerType,
		string remoteAddress);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Debug,
		Message = "WebSocket pre-accept gate rejected request for handler {HandlerType} at {Path}.")]
	private static partial void LogPreAcceptRejected(
		ILogger logger,
		string handlerType,
		PathString path);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Debug,
		Message = "WebSocket connection {ConnectionId} rejected by an IConnectionLifecycle hook.")]
	private static partial void LogLifecycleRejectedUpgrade(
		ILogger logger,
		string connectionId);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Debug,
		Message = "WebSocket connection {ConnectionId} closed (graceful: {WasGraceful}, duration: {Duration}).")]
	private static partial void LogConnectionClosed(
		ILogger logger,
		string connectionId,
		bool wasGraceful,
		TimeSpan duration);

}

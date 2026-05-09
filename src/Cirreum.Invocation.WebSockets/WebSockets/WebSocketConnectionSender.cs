namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// <see cref="IConnectionSender"/> for WebSocket. Resolves the active
/// <see cref="WebSocketConnection"/> from the ambient <see cref="IInvocationContextAccessor"/>
/// and dispatches sends through the underlying <see cref="WebSocket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped lifetime — resolved per-invocation via DI. Reads the connection through the
/// ambient accessor rather than via DI directly because the connection is invocation-
/// bound, not service-bound.
/// </para>
/// <para>
/// WebSocket is a raw-byte transport — there is no method-routing built into the protocol.
/// The no-method <see cref="IConnectionSender.SendAsync{T}(T, CancellationToken)"/>
/// overload serializes the payload as JSON text. The keyed overload wraps the payload in a
/// <c>{ "method": "...", "payload": ... }</c> envelope for apps that implement their own
/// method-dispatch protocol on top of WebSocket.
/// </para>
/// </remarks>
internal sealed class WebSocketConnectionSender(
	IInvocationContextAccessor accessor
) : IConnectionSender {

	private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

	public async ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
		var connection = this.ResolveActiveConnection();
		var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
		await connection.WebSocket.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	public async ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
		var connection = this.ResolveActiveConnection();
		var envelope = new { method, payload };
		var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
		await connection.WebSocket.SendAsync(
			bytes.AsMemory(),
			WebSocketMessageType.Text,
			endOfMessage: true,
			cancellationToken);
	}

	private WebSocketConnection ResolveActiveConnection() {
		var invocation = accessor.Current
			?? throw new InvalidOperationException(
				"IConnectionSender requires an active invocation. Inject this from a WebSocket handler or other code that runs inside the Cirreum invocation pipeline.");

		return invocation.Connection as WebSocketConnection
			?? throw new InvalidOperationException(
				$"IConnectionSender requires a WebSocket-sourced invocation; the active invocation source is '{invocation.InvocationSource}'.");
	}

}

namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation.Connections;
using System.Net.WebSockets;

/// <summary>
/// WebSocket-specific extension of <see cref="IInvocationConnection"/> that exposes the
/// frame-level send primitive (<see cref="SendBytesAsync"/>). The base typed
/// <c>SendAsync&lt;T&gt;</c> overloads on <see cref="IInvocationConnection"/> handle the
/// transport-agnostic JSON-over-Text-frame case using the active handler's
/// <c>SerializerOptions</c>; downcast to this interface when raw frame writes are required
/// (binary protocols, audio chunks, pre-serialized payloads).
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting code that needs raw byte writes downcasts the ambient connection:
/// <code>
/// if (accessor.Current?.Connection is IWebSocketConnection ws) {
///     await ws.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ct);
/// }
/// </code>
/// Handler code does the same against <c>this.Connection</c>:
/// <code>
/// var ws = (IWebSocketConnection)this.Connection!;
/// await ws.SendBytesAsync(audioChunk, WebSocketMessageType.Binary, ct);
/// </code>
/// The downcast is the explicit "I am committed to WebSocket-specific behavior"
/// acknowledgment — code that goes through it cannot transport-substitute later.
/// </para>
/// <para>
/// SignalR and gRPC streaming have no raw-frame equivalent — their wire formats are
/// always wrapped in protocol envelopes (<c>IHubProtocol</c>, Protobuf). Apps targeting
/// those transports send typed payloads through the L2 <c>SendAsync&lt;T&gt;</c>
/// overloads instead.
/// </para>
/// </remarks>
public interface IWebSocketConnection : IInvocationConnection {

	/// <summary>
	/// Push raw bytes directly to the peer as a single complete WebSocket frame, bypassing
	/// JSON serialization entirely. Use for binary protocols (MessagePack, Protobuf,
	/// audio/video chunks) or pre-serialized payloads.
	/// </summary>
	/// <param name="bytes">The raw payload bytes to write to the wire.</param>
	/// <param name="messageType">
	/// WebSocket frame type. Defaults to <see cref="WebSocketMessageType.Binary"/> — the
	/// natural choice for raw bytes; pass <see cref="WebSocketMessageType.Text"/> for
	/// pre-serialized UTF-8 textual payloads (JSON written by app-controlled
	/// serialization, etc.).
	/// </param>
	/// <param name="cancellationToken">Cancellation token for the send.</param>
	ValueTask SendBytesAsync(
		ReadOnlyMemory<byte> bytes,
		WebSocketMessageType messageType = WebSocketMessageType.Binary,
		CancellationToken cancellationToken = default);

}

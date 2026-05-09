namespace Cirreum.Invocation.Configuration;

/// <summary>
/// Instance settings for a WebSocket invocation source.
/// Each instance represents one WebSocket endpoint mapped at a configured
/// <see cref="InvocationProviderInstanceSettings.Path"/>, with an optional companion
/// HTTP endpoint at <see cref="RequestPath"/> for pre-connection negotiation.
/// </summary>
/// <remarks>
/// <para>
/// Two-phase connection model: apps that need an HTTP request/response exchange before
/// the WebSocket connection (session creation, token issuance, call metadata capture)
/// configure <see cref="RequestPath"/> alongside the base
/// <see cref="InvocationProviderInstanceSettings.Path"/>. The framework maps both
/// endpoints — <see cref="RequestPath"/> routes to a minimal API delegate provided at
/// the <c>AddWebSocket&lt;THandler&gt;</c> call site (via the <c>request:</c> builder),
/// and <c>Path</c> routes to the WebSocket handler's
/// <see cref="WebSockets.WebSocketHandler.OnAcceptAsync"/> gate followed by the
/// connection lifecycle.
/// </para>
/// <para>
/// When only <c>Path</c> is set (no <see cref="RequestPath"/>), the WebSocket endpoint
/// is hit directly with no companion HTTP endpoint.
/// </para>
/// <para>
/// Naming: <strong>request</strong> refers to the inbound HTTP request that initiates
/// the WebSocket flow (companion endpoint). <strong>Upgrade</strong> refers to the
/// HTTP→WS protocol upgrade itself, which happens at <c>Path</c> when
/// <c>AcceptWebSocketAsync</c> is called.
/// </para>
/// </remarks>
public sealed class WebSocketInvocationInstanceSettings
	: InvocationProviderInstanceSettings {

	/// <summary>
	/// Default disconnect cleanup budget — 30 seconds.
	/// </summary>
	public const int DefaultDisconnectTimeoutSeconds = 30;

	/// <summary>
	/// Hard cap on disconnect cleanup budget — 5 minutes.
	/// </summary>
	public const int MaxDisconnectTimeoutSeconds = 300;

	/// <summary>
	/// Default max message size — 64 KB. Conservative default that covers Twilio media
	/// streams, chat, telemetry, and most IoT protocols. Apps with larger messages
	/// (file chunks, big telemetry batches) raise this explicitly.
	/// </summary>
	public const int DefaultMaxMessageSizeBytes = 64 * 1024;

	/// <summary>
	/// Hard cap on max message size — 8 MB. Realistic upper bound for non-chunked
	/// protocols; truly large transfers should be split into multiple smaller messages.
	/// Caps memory pressure under load (e.g. 1000 connections × 8 MB = 8 GB peak).
	/// </summary>
	public const int MaxMaxMessageSizeBytes = 8 * 1024 * 1024;

	/// <summary>
	/// Default initial receive buffer size — 4 KB.
	/// </summary>
	public const int DefaultReceiveBufferSizeBytes = 4096;

	/// <summary>
	/// Hard cap on initial receive buffer size — 64 KB.
	/// </summary>
	public const int MaxReceiveBufferSizeBytes = 64 * 1024;


	/// <summary>
	/// Gets or sets the optional HTTP endpoint path for pre-connection negotiation
	/// (e.g. <c>"/api/voice/connect"</c>). When set, the framework maps a regular HTTP
	/// endpoint at this path using the minimal API delegate provided at the
	/// <c>AddWebSocket&lt;THandler&gt;(instanceKey, request: r =&gt; r.Map(...))</c> call site.
	/// </summary>
	/// <remarks>
	/// Must be paired with a <c>request:</c> builder at the <c>AddWebSocket</c> call
	/// site that calls <c>Map</c>. The framework throws at startup if
	/// <see cref="RequestPath"/> is configured without a request handler, or vice versa.
	/// </remarks>
	public string? RequestPath { get; set; }

	/// <summary>
	/// Gets or sets the disconnect cleanup budget in seconds. Bounds how long
	/// <see cref="WebSockets.WebSocketHandler.OnDisconnectedAsync"/> and
	/// <c>IConnectionLifecycle.OnDisconnectedAsync</c> hooks have to complete cleanup
	/// before the framework cancels their cancellation tokens. Default:
	/// <see cref="DefaultDisconnectTimeoutSeconds"/> (30s). Hard cap:
	/// <see cref="MaxDisconnectTimeoutSeconds"/> (5 min).
	/// </summary>
	/// <remarks>
	/// Different invocation sources have different cleanup needs — voice calls may need
	/// to flush call records and close downstream sockets (longer budget); telemetry
	/// instances may want to fail fast on hung downstreams (shorter budget).
	/// </remarks>
	public int DisconnectTimeoutSeconds { get; set; } = DefaultDisconnectTimeoutSeconds;

	/// <summary>
	/// Gets or sets the maximum size in bytes of a single complete WebSocket message.
	/// Multi-frame messages exceeding this limit are rejected with a
	/// <c>MessageTooBig</c> close frame. Default:
	/// <see cref="DefaultMaxMessageSizeBytes"/> (64 KB) — conservative, covers Twilio
	/// media, chat, telemetry, and IoT defaults. Hard cap:
	/// <see cref="MaxMaxMessageSizeBytes"/> (8 MB).
	/// </summary>
	/// <remarks>
	/// Apps that genuinely need larger transfers should use a streaming-chunk protocol
	/// on top of WebSocket (multiple smaller messages with a sequence header) rather
	/// than raising this limit. SignalR's analogous default
	/// (<c>MaximumReceiveMessageSize</c>) is 32 KB — even more conservative — for the
	/// same reason: forcing apps to think about message sizing keeps memory pressure
	/// predictable under load.
	/// </remarks>
	public int MaxMessageSizeBytes { get; set; } = DefaultMaxMessageSizeBytes;

	/// <summary>
	/// Gets or sets the initial size in bytes of the per-connection pooled receive buffer.
	/// The buffer is rented from <c>ArrayPool&lt;byte&gt;.Shared</c> for the connection's
	/// lifetime. Default: <see cref="DefaultReceiveBufferSizeBytes"/> (4 KB). Hard cap:
	/// <see cref="MaxReceiveBufferSizeBytes"/> (64 KB).
	/// </summary>
	/// <remarks>
	/// Larger buffers reduce <c>ReceiveAsync</c> call frequency for transports that send
	/// large frames (binary audio, telemetry batches). Smaller buffers reduce per-connection
	/// memory footprint. The buffer is one allocation per connection — multi-frame messages
	/// reuse it across receives.
	/// </remarks>
	public int ReceiveBufferSizeBytes { get; set; } = DefaultReceiveBufferSizeBytes;

	/// <summary>
	/// Gets or sets the keep-alive ping interval — how often the server sends unsolicited
	/// pings to the client. When set, overrides the global
	/// <c>WebSocketOptions.KeepAliveInterval</c> (default 2 minutes from
	/// <c>UseWebSockets</c>). Defaults to <see langword="null"/> — inherit the global value.
	/// </summary>
	/// <remarks>
	/// Voice and realtime workloads may want a shorter interval for liveness detection;
	/// telemetry workloads may prefer longer intervals to reduce bandwidth.
	/// </remarks>
	public TimeSpan? KeepAliveInterval { get; set; }

	/// <summary>
	/// Gets or sets the keep-alive timeout — how long to wait for the client to respond
	/// to a ping before the connection is considered dead and aborted. When set, overrides
	/// the global <c>WebSocketOptions.KeepAliveTimeout</c> (default 30 seconds from
	/// <c>UseWebSockets</c>). Defaults to <see langword="null"/> — inherit the global value.
	/// </summary>
	/// <remarks>
	/// Pairs with <see cref="KeepAliveInterval"/>. Short interval + short timeout = fast
	/// liveness detection (good for voice / realtime where stale connections are wasted
	/// resource). Long interval + lenient timeout = bandwidth efficiency (good for
	/// telemetry, occasional pushes).
	/// </remarks>
	public TimeSpan? KeepAliveTimeout { get; set; }

}

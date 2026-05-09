namespace Cirreum.Invocation.WebSockets;

/// <summary>
/// Endpoint metadata that ties a mapped route to its WebSocket invocation-source
/// instance key. Attached by <see cref="WebSocketInvocationRegistrar.MapSource"/>
/// when mapping the upgrade endpoint, so <see cref="IWebSocketUrlBuilder"/> can resolve
/// the correct instance implicitly via the active endpoint.
/// </summary>
/// <param name="InstanceKey">The configuration instance key.</param>
public sealed record WebSocketInstanceMetadata(string InstanceKey);

namespace Cirreum.Invocation.WebSockets;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Builds absolute WebSocket URLs (<c>wss://</c> or <c>ws://</c>) for configured
/// invocation-source instances. Resolves the path template from instance settings
/// and the host/scheme from the active <see cref="HttpContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Designed for use inside upgrade-endpoint delegates registered via
/// <c>AddWebSocket&lt;THandler&gt;("instanceKey", upgrade: ...)</c>. The framework
/// attaches a <see cref="WebSocketInstanceMetadata"/> to the upgrade endpoint at map
/// time; the implicit <see cref="Build(HttpContext, object?, object?)"/> overload reads
/// it to determine the target instance.
/// </para>
/// <para>
/// Outside an upgrade endpoint, use the explicit
/// <see cref="Build(string, HttpContext, object?, object?)"/> overload to name the
/// instance directly.
/// </para>
/// <para>
/// Path template placeholders (e.g. <c>{callSid}</c>) are resolved in priority order:
/// (1) explicit <c>routeValues</c>, (2) <c>Request.RouteValues</c>,
/// (3) <c>Request.Query</c>, (4) <c>Request.Form</c> when the content type is
/// form-encoded. Name matching is case-insensitive. Unresolved placeholders throw
/// at build time rather than producing malformed URLs.
/// </para>
/// </remarks>
public interface IWebSocketUrlBuilder {

	/// <summary>
	/// Builds an absolute WebSocket URL for the instance whose upgrade endpoint is
	/// currently executing. The instance is resolved from the active endpoint's
	/// <see cref="WebSocketInstanceMetadata"/>.
	/// </summary>
	/// <param name="context">The active HTTP context (typically the upgrade request).</param>
	/// <param name="routeValues">Optional route values for the path template.</param>
	/// <param name="queryValues">Optional query-string values appended after the path.</param>
	/// <returns>An absolute WebSocket URL (e.g. <c>wss://example.com/ws/voice/abc?token=xyz</c>).</returns>
	/// <exception cref="InvalidOperationException">
	/// Thrown when the active endpoint has no <see cref="WebSocketInstanceMetadata"/>
	/// (i.e. this overload is being used outside an upgrade endpoint).
	/// </exception>
	string Build(HttpContext context, object? routeValues = null, object? queryValues = null);

	/// <summary>
	/// Builds an absolute WebSocket URL for the named instance.
	/// </summary>
	/// <param name="instanceKey">The configured instance key.</param>
	/// <param name="context">The active HTTP context — used for host and scheme.</param>
	/// <param name="routeValues">Optional route values for the path template.</param>
	/// <param name="queryValues">Optional query-string values appended after the path.</param>
	/// <returns>An absolute WebSocket URL (e.g. <c>wss://example.com/ws/voice/abc?token=xyz</c>).</returns>
	/// <exception cref="InvalidOperationException">
	/// Thrown when <paramref name="instanceKey"/> is not a configured WebSocket instance.
	/// </exception>
	string Build(string instanceKey, HttpContext context, object? routeValues = null, object? queryValues = null);

}

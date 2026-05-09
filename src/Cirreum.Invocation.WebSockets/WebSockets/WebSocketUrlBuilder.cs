namespace Cirreum.Invocation.WebSockets;

using Cirreum.Invocation.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Text;

/// <summary>
/// Default <see cref="IWebSocketUrlBuilder"/> implementation. Wraps
/// <see cref="LinkGenerator"/> for path-template resolution and converts the request's
/// HTTP scheme to the corresponding WebSocket scheme (<c>https</c> → <c>wss</c>,
/// <c>http</c> → <c>ws</c>).
/// </summary>
internal sealed class WebSocketUrlBuilder(
	IOptions<WebSocketInvocationSettings> options
) : IWebSocketUrlBuilder {

	public string Build(HttpContext context, object? routeValues = null, object? queryValues = null) {
		var metadata = context.GetEndpoint()?.Metadata.GetMetadata<WebSocketInstanceMetadata>()
			?? throw new InvalidOperationException(
				"IWebSocketUrlBuilder.Build(HttpContext, ...) requires an active endpoint with WebSocketInstanceMetadata. " +
				"Use this overload from inside an upgrade-endpoint delegate registered via " +
				"AddWebSocket<THandler>(\"key\", upgrade: ...). For other contexts, use the " +
				"Build(instanceKey, ...) overload.");

		return this.BuildInternal(metadata.InstanceKey, context, routeValues, queryValues);
	}

	public string Build(string instanceKey, HttpContext context, object? routeValues = null, object? queryValues = null) =>
		this.BuildInternal(instanceKey, context, routeValues, queryValues);

	private string BuildInternal(
		string instanceKey,
		HttpContext context,
		object? routeValues,
		object? queryValues) {

		ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
		ArgumentNullException.ThrowIfNull(context);

		if (!options.Value.Instances.TryGetValue(instanceKey, out var instance)) {
			throw new InvalidOperationException(
				$"WebSocket instance '{instanceKey}' is not configured. Check " +
				$"Cirreum:Invocation:Providers:WebSocket:Instances:{instanceKey} in appsettings.");
		}

		// Substitute placeholders in the path template — explicit routeValues first,
		// then auto-extract from RouteValues, Query, and Form (case-insensitive).
		var path = ApplyTemplateValues(instance.Path, context, routeValues);

		// Append query string if values provided.
		if (queryValues is not null) {
			path = AppendQueryString(path, queryValues);
		}

		// Convert HTTP scheme to WebSocket scheme.
		var wsScheme = context.Request.IsHttps ? "wss" : "ws";

		return $"{wsScheme}://{context.Request.Host}{path}";
	}

	private static string ApplyTemplateValues(string template, HttpContext context, object? routeValues) {
		// Build a single case-insensitive lookup combining all sources, in priority order:
		// 1. Explicit routeValues (highest priority — caller-supplied)
		// 2. Request.RouteValues (URL route parameters)
		// 3. Request.Query (query string)
		// 4. Request.Form (form fields — only when content-type is form-encoded)
		// First-write-wins: more specific sources don't overwrite more authoritative ones.
		// Keys are normalized to lowercase on insert so placeholder construction is
		// predictable regardless of source casing (Twilio's PascalCase form fields,
		// camelCase query params, etc. all collapse to a single canonical key).
		var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

		if (routeValues is not null) {
			foreach (var (key, value) in new RouteValueDictionary(routeValues)) {
				if (value is not null) {
					resolved.TryAdd(key.ToLowerInvariant(), value.ToString() ?? "");
				}
			}
		}

		foreach (var (key, value) in context.Request.RouteValues) {
			if (value is not null) {
				resolved.TryAdd(key.ToLowerInvariant(), value.ToString() ?? "");
			}
		}

		foreach (var (key, value) in context.Request.Query) {
			resolved.TryAdd(key.ToLowerInvariant(), value.ToString());
		}

		if (context.Request.HasFormContentType) {
			foreach (var (key, value) in context.Request.Form) {
				resolved.TryAdd(key.ToLowerInvariant(), value.ToString());
			}
		}

		// Substitute placeholders in the template. Placeholders are constructed with
		// the lowercase canonical key; OrdinalIgnoreCase matches templates regardless
		// of how the dev wrote the placeholder (e.g. {callSid} vs {CallSid}).
		var result = template;
		foreach (var (key, value) in resolved) {
			var placeholder = "{" + key + "}";
			if (!result.Contains(placeholder, StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			result = result.Replace(
				placeholder,
				Uri.EscapeDataString(value),
				StringComparison.OrdinalIgnoreCase);
		}

		// Surface unresolved placeholders as a clear error rather than malformed URLs.
		if (result.Contains('{') && result.Contains('}')) {
			throw new InvalidOperationException(
				$"WebSocket path template '{template}' has unresolved placeholders after binding. " +
				$"Pass values explicitly via routeValues, or ensure they are present in the request's " +
				$"route, query string, or form data (case-insensitive name match).");
		}

		return result;
	}

	private static string AppendQueryString(string path, object queryValues) {
		var values = new RouteValueDictionary(queryValues);
		if (values.Count == 0) {
			return path;
		}

		var sb = new StringBuilder(path);
		var first = !path.Contains('?');
		foreach (var (key, value) in values) {
			if (value is null) {
				continue;
			}

			sb.Append(first ? '?' : '&');
			sb.Append(Uri.EscapeDataString(key));
			sb.Append('=');
			sb.Append(Uri.EscapeDataString(value.ToString() ?? ""));
			first = false;
		}

		return sb.ToString();
	}

}

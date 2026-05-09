namespace Cirreum.Invocation;

using Cirreum.Invocation.Configuration;
using Cirreum.Invocation.Connections;
using Cirreum.Invocation.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Registrar for the WebSocket invocation source. Maps WebSocket endpoints from
/// <c>Cirreum:Invocation:Providers:WebSocket</c> configuration and wires the
/// <see cref="WebSocketOrchestrator"/> that publishes <see cref="IInvocationContext"/>
/// per WebSocket message.
/// </summary>
/// <remarks>
/// <para>
/// Supports a two-phase connection model: when
/// <see cref="WebSocketInvocationInstanceSettings.RequestPath"/> is configured alongside
/// a <c>request:</c> builder at the <c>AddWebSocket</c> call site, the registrar maps
/// a companion HTTP endpoint for pre-connection negotiation (session creation, token
/// issuance) using full minimal API parameter binding. The WebSocket endpoint at
/// <see cref="InvocationProviderInstanceSettings.Path"/> runs the handler's
/// <see cref="WebSocketHandler.OnAcceptAsync"/> gate before accepting the connection.
/// </para>
/// <para>
/// Handler-type-agnostic: this registrar does not know which concrete
/// <see cref="WebSocketHandler"/> to resolve for a given instance. The L5
/// <c>AddWebSocket&lt;THandler&gt;(instanceKey, request: ...)</c> extension stashes a
/// <see cref="WebSocketHandlerMapping"/> in DI carrying the (instanceKey, HandlerType,
/// RequestHandler, RequestMethod, ConfigureRequestRoute) tuple; <see cref="MapSource"/>
/// resolves it and dispatches through the orchestrator at endpoints-phase time.
/// </para>
/// </remarks>
public sealed class WebSocketInvocationRegistrar
	: InvocationProviderRegistrar<WebSocketInvocationSettings, WebSocketInvocationInstanceSettings> {

	/// <summary>
	/// Represents the provider key used to identify the WebSocket provider in configuration
	/// or service registration scenarios.
	/// </summary>
	public const string ProviderKey = "WebSocket";

	/// <inheritdoc/>
	public override string ProviderName => ProviderKey;

	/// <inheritdoc/>
	public override void ValidateSettings(WebSocketInvocationInstanceSettings settings) {

		if (!settings.Path.StartsWith('/')) {
			throw new InvalidOperationException(
				$"WebSocket provider instance Path '{settings.Path}' must start with '/'.");
		}

		if (!string.IsNullOrWhiteSpace(settings.RequestPath) && !settings.RequestPath.StartsWith('/')) {
			throw new InvalidOperationException(
				$"WebSocket provider instance RequestPath '{settings.RequestPath}' must start with '/'.");
		}

		if (settings.DisconnectTimeoutSeconds <= 0
			|| settings.DisconnectTimeoutSeconds > WebSocketInvocationInstanceSettings.MaxDisconnectTimeoutSeconds) {
			throw new InvalidOperationException(
				$"WebSocket provider instance DisconnectTimeoutSeconds must be in (0, {WebSocketInvocationInstanceSettings.MaxDisconnectTimeoutSeconds}]; got {settings.DisconnectTimeoutSeconds}.");
		}

		if (settings.MaxMessageSizeBytes <= 0
			|| settings.MaxMessageSizeBytes > WebSocketInvocationInstanceSettings.MaxMaxMessageSizeBytes) {
			throw new InvalidOperationException(
				$"WebSocket provider instance MaxMessageSizeBytes must be in (0, {WebSocketInvocationInstanceSettings.MaxMaxMessageSizeBytes}]; got {settings.MaxMessageSizeBytes}.");
		}

		if (settings.ReceiveBufferSizeBytes <= 0
			|| settings.ReceiveBufferSizeBytes > WebSocketInvocationInstanceSettings.MaxReceiveBufferSizeBytes) {
			throw new InvalidOperationException(
				$"WebSocket provider instance ReceiveBufferSizeBytes must be in (0, {WebSocketInvocationInstanceSettings.MaxReceiveBufferSizeBytes}]; got {settings.ReceiveBufferSizeBytes}.");
		}

	}

	/// <inheritdoc/>
	public override void Register(
		WebSocketInvocationSettings providerSettings,
		IServiceCollection services,
		IConfiguration configuration) {

		// Bind the entire WebSocket provider settings as IOptions for the URL builder
		// to read instance paths at request time. Done at the provider level (not per-
		// instance) because IOptions is a single object containing all instances.
		var providerSection = configuration.GetSection(
			$"Cirreum:Invocation:Providers:{this.ProviderName}");
		services.Configure<WebSocketInvocationSettings>(providerSection);

		// Single registration for the URL builder — instance-aware via endpoint metadata.
		services.TryAddSingleton<IWebSocketUrlBuilder, WebSocketUrlBuilder>();

		// Delegate per-instance registration to the base implementation.
		base.Register(providerSettings, services, configuration);
	}

	/// <inheritdoc/>
	protected override void RegisterSource(
		string key,
		WebSocketInvocationInstanceSettings settings,
		IServiceCollection services,
		IConfiguration configuration) {

		services.TryAddSingleton<WebSocketOrchestrator>();
		services.TryAddScoped<IConnectionSender, WebSocketConnectionSender>();
	}

	/// <inheritdoc/>
	protected override void MapSource(
		string key,
		WebSocketInvocationInstanceSettings settings,
		IEndpointRouteBuilder endpoints) {

		var mapping = endpoints.ServiceProvider
			.GetServices<WebSocketHandlerMapping>()
			.FirstOrDefault(m => m.InstanceKey == key)
			?? throw new InvalidOperationException(
				$"No WebSocketHandlerMapping found for instance '{key}'. " +
				$"Did you call builder.AddInvocation(b => b.AddWebSocket<THandler>(\"{key}\")) at the L5 layer?");

		var hasRequestPath = !string.IsNullOrWhiteSpace(settings.RequestPath);
		var hasRequestHandler = mapping.RequestHandler is not null;

		if (hasRequestPath && !hasRequestHandler) {
			throw new InvalidOperationException(
				$"WebSocket instance '{key}' has RequestPath '{settings.RequestPath}' configured but no " +
				$"request handler was provided. Pass a request: builder to " +
				$"AddWebSocket<THandler>(\"{key}\", request: r => r.Map(...)) or remove RequestPath from configuration.");
		}

		if (hasRequestHandler && !hasRequestPath) {
			throw new InvalidOperationException(
				$"WebSocket instance '{key}' has a request handler but no RequestPath configured. " +
				$"Add \"RequestPath\": \"/your/path\" to the instance configuration or remove the " +
				$"request: builder from AddWebSocket<THandler>(\"{key}\").");
		}

		var orchestrator = endpoints.ServiceProvider.GetRequiredService<WebSocketOrchestrator>();

		// Map the WebSocket endpoint (always). Use Map() — not MapGet() — because WebSocket
		// arrives as GET (HTTP/1.1, RFC 6455) OR CONNECT (HTTP/2+, RFC 8441/9220).
		// Restricting to GET would silently break HTTP/2+ clients with a 405. The closure
		// captures the per-instance settings so the orchestrator can apply per-instance
		// limits (max message size, receive buffer size, disconnect timeout, keep-alive).
		// Excluded from OpenAPI/Swagger discovery because WebSocket isn't a REST operation;
		// AsyncAPI is the appropriate spec for documenting WebSocket endpoints externally.
		var socketRoute = endpoints
			.Map(settings.Path, async context => {
				await orchestrator.HandleWebSocketAsync(context, mapping.HandlerType, settings, context.RequestAborted);
			})
			.WithMetadata(new ExcludeFromDescriptionAttribute());

		if (!string.IsNullOrWhiteSpace(settings.Scheme)) {
			socketRoute.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = settings.Scheme });
		}

		// Map the request HTTP endpoint (when both config path + handler are provided).
		// Defaults to POST (the natural webhook method) — apps override via the explicit-
		// method Map overload on the request builder. Apps customize OpenAPI / naming /
		// metadata via the configure callback inside the request builder's Map call.
		if (hasRequestPath && hasRequestHandler) {
			var requestMethod = mapping.RequestMethod ?? "POST";
			var requestRoute = endpoints
				.MapMethods(settings.RequestPath!, [requestMethod], mapping.RequestHandler!)
				.WithMetadata(new WebSocketInstanceMetadata(key));

			if (!string.IsNullOrWhiteSpace(settings.Scheme)) {
				requestRoute.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = settings.Scheme });
			}

			mapping.ConfigureRequestRoute?.Invoke(requestRoute);
		}
	}

}

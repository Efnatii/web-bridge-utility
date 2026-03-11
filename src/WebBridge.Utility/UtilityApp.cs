using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Adapters.SystemAccess;
using WebBridge.Utility.Core;
using WebBridge.Utility.Infrastructure;
using WebBridge.Utility.Protocol;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility;

public static class UtilityApp
{
    public static WebApplication BuildWebApplication(
        UtilitySettings settings,
        Channel<ActivationRequest> activationChannel)
    {
        WebApplicationOptions options = new()
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(Program).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = settings.EnvironmentName,
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseUrls(settings.ListenUrl);
        UtilityLogging.Configure(builder.Logging, settings);
        builder.Services.AddSingleton(settings);
        builder.Services.Replace(ServiceDescriptor.Singleton(activationChannel));
        builder.Services.AddSingleton<IUtilityClock, SystemUtilityClock>();
        builder.Services.AddSingleton<UtilityRuntimeStateTracker>();
        builder.Services.AddSingleton<ISessionManager>(sp =>
            new SessionManager(
                sp.GetRequiredService<IUtilityClock>(),
                settings,
                sp.GetRequiredService<ILogger<SessionManager>>()));
        builder.Services.AddSingleton<IIdleShutdownSupervisor, IdleShutdownSupervisor>();
        builder.Services.AddSingleton<ISecurityValidator>(_ => new SecurityValidator(settings));
        builder.Services.AddSingleton<IVersionCompatibilityService, VersionCompatibilityService>();
        builder.Services.AddSingleton<IRuntimeConfigurationManager, RuntimeConfigurationManager>();
        builder.Services.AddSingleton<IBrowserLaunchDecider, BrowserLaunchDecider>();
        builder.Services.AddSingleton<IBrowserLauncher, ProcessBrowserLauncher>();
        builder.Services.AddHttpClient<IManifestService, ManifestService>();
        builder.Services.AddSingleton<IProfileStore>(_ => new ProfileStore(settings));
        builder.Services.AddSingleton<SystemRuntime>(_ => new SystemRuntime(settings));
        builder.Services.AddSingleton<SystemInvokeSurface>();
        builder.Services.AddSingleton<IComInvokeSurfaceFactory, ComInvokeSurfaceFactory>();
        builder.Services.AddSingleton<IAdapterInvokeSurfaceRegistry, AdapterInvokeSurfaceRegistry>();
        builder.Services.AddSingleton<ICommandPlanCompiler, CommandPlanCompiler>();
        builder.Services.AddSingleton<ICommandDispatcher, CommandDispatcher>();
        builder.Services.AddSingleton<SessionSocketHub>();
        builder.Services.AddSingleton<IUtilityControlService, UtilityControlService>();
        builder.Services.AddSingleton<ActivationRequestProcessor>();
        builder.Services.AddHostedService<ActivationBackgroundService>();
        builder.Services.AddHostedService<UtilityBackgroundService>();

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            string origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin))
            {
                ISecurityValidator validator = context.RequestServices.GetRequiredService<ISecurityValidator>();
                SecurityValidationResult validation = validator.ValidateOrigin(origin);
                if (validation.IsAllowed)
                {
                    context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                    context.Response.Headers["Access-Control-Allow-Headers"] = $"{ProtocolConstants.PairingTokenHeader}, {ProtocolConstants.CorrelationIdHeader}, Content-Type";
                    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                    context.Response.Headers["Vary"] = "Origin";
                }
                else if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(ProtocolEnvelope.Api<object>("security.error", null, error: validation.Error)).ConfigureAwait(false);
                    return;
                }
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next().ConfigureAwait(false);
        });
        app.UseWebSockets();
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        ILogger apiLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebBridge.Utility.Api");

        app.MapGet("/health", (HttpContext context, ISecurityValidator security, UtilityRuntimeStateTracker state, ISessionManager sessions, IUtilityClock clock) =>
        {
            SecurityValidationResult loopback = security.ValidateLoopback(context.Connection.RemoteIpAddress?.ToString());
            return !loopback.IsAllowed
                ? ToSecurityResult(loopback)
                : Results.Json(ProtocolEnvelope.Api(
                    "health",
                    new HealthResponse("ok", state.State, clock.UtcNow, sessions.ActiveSessionCount, sessions.PresenceSessionCount)));
        });

        RouteGroupBuilder secured = app.MapGroup(string.Empty)
            .AddEndpointFilter(new SecurityEndpointFilter(requireOrigin: true, requireToken: true));

        secured.MapGet("/info", (UtilitySettings settings, UtilityRuntimeStateTracker state, ISessionManager sessions) =>
            Results.Json(ProtocolEnvelope.Api(
                "info",
                new InfoResponse(
                    settings.Metadata.Clone(),
                    settings.UtilityVersion,
                    state.State,
                    settings.ListenUrl,
                    settings.UiUrl,
                    settings.EnvironmentName,
                    sessions.ActiveSessionCount,
                    sessions.PresenceSessionCount,
                    state.ActivationCount,
                    state.StartedAtUtc))));

        secured.MapGet("/config/effective", (UtilitySettings settings) =>
            Results.Json(ProtocolEnvelope.Api(
                "config.effective",
                new EffectiveConfigResponse(UtilitySettingsSanitizer.Redact(settings)))));

        secured.MapGet("/config/version", (IRuntimeConfigurationManager configManager) =>
            Results.Json(ProtocolEnvelope.Api(
                "config.version",
                configManager.GetVersion())));

        secured.MapPost("/config/load", async (LoadConfigRequest? request, IRuntimeConfigurationManager configManager, CancellationToken cancellationToken) =>
        {
            if (request?.Settings is null)
            {
                ApiError error = new("invalid_config_payload", "Request body must contain a non-null settings object.");
                return Results.Json(
                    ProtocolEnvelope.Api<ConfigUpdateResponse>("config.loaded", null, error: error),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            ConfigUpdateResponse response = await configManager.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ProtocolEnvelope.Api("config.loaded", response),
                statusCode: response.Applied ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        secured.MapPost("/config/reload", async (IRuntimeConfigurationManager configManager, CancellationToken cancellationToken) =>
        {
            ConfigUpdateResponse response = await configManager.ReloadFromDiskAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ProtocolEnvelope.Api("config.reloaded", response),
                statusCode: response.Applied ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        secured.MapPost("/session/register", (RegisterSessionRequest request, UtilitySettings settings, ISessionManager sessions, UtilityRuntimeStateTracker state) =>
        {
            SessionRegistrationResult registration = sessions.Register(request);
            UtilityLog.SessionRegistered(apiLogger, registration.SessionId, request.ClientName, registration.ActiveSessionCount);
            Uri listenUri = new(settings.ListenUrl);
            UriBuilder wsBuilder = new(listenUri)
            {
                Scheme = string.Equals(listenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? Uri.UriSchemeWss
                    : Uri.UriSchemeWs,
                Path = ProtocolConstants.SessionWebSocketPath,
                Query = $"sessionId={registration.SessionId}",
            };
            RegisterSessionResponse response = new(
                registration.SessionId,
                wsBuilder.Uri.ToString(),
                registration.HeartbeatIntervalSeconds,
                state.State,
                settings.UtilityVersion,
                registration.ActiveSessionCount,
                registration.PresenceSessionCount);
            return Results.Json(ProtocolEnvelope.Api("session.registered", response));
        });

        secured.MapPost("/session/closing", (ClosingSessionRequest request, ISessionManager sessions, SessionSocketHub hub) =>
        {
            bool closed = sessions.Close(request.SessionId, request.Reason);
            hub.Detach(request.SessionId);
            UtilityLog.SessionClosed(apiLogger, request.SessionId, closed, request.Reason);
            return Results.Json(ProtocolEnvelope.Api(
                "session.closed",
                new JsonObject
                {
                    ["sessionId"] = request.SessionId,
                    ["closed"] = closed,
                }));
        });

        secured.MapGet("/sessions", (ISessionManager sessions) =>
            Results.Json(ProtocolEnvelope.Api(
                "sessions",
                JsonSerializer.SerializeToNode(sessions.GetSessions()))));

        secured.MapPost("/commands/execute", async (HttpContext context, ExecuteCommandRequest request, ICommandDispatcher dispatcher, SessionSocketHub hub, CancellationToken cancellationToken) =>
        {
            string? correlationId = context.Request.Headers[ProtocolConstants.CorrelationIdHeader].ToString();
            UtilityLog.CommandExecuting(apiLogger, request.ProfileId, request.CommandId);
            CommandExecutionResult execution = await dispatcher.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            ExecuteCommandResponse response = new(execution.ExecutionId, execution.Success, execution.Result, execution.Error, execution.Report);
            await hub.BroadcastAsync("command-result", JsonSerializer.SerializeToNode(response), correlationId, cancellationToken).ConfigureAwait(false);
            UtilityLog.CommandFinished(apiLogger, request.ProfileId, request.CommandId, execution.Success, execution.ExecutionId);
            int statusCode = execution.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
            return Results.Json(ProtocolEnvelope.Api("command.result", response, correlationId, execution.Error), statusCode: statusCode);
        });

        secured.MapPost("/commands/execute-batch", async (HttpContext context, ExecuteBatchCommandRequest request, ICommandDispatcher dispatcher, SessionSocketHub hub, CancellationToken cancellationToken) =>
        {
            string? correlationId = context.Request.Headers[ProtocolConstants.CorrelationIdHeader].ToString();
            ExecuteBatchCommandResponse execution = await dispatcher.ExecuteBatchAsync(request, cancellationToken).ConfigureAwait(false);
            await hub.BroadcastAsync("command-batch-result", JsonSerializer.SerializeToNode(execution), correlationId, cancellationToken).ConfigureAwait(false);
            int statusCode = execution.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
            return Results.Json(ProtocolEnvelope.Api("command.batch-result", execution, correlationId), statusCode: statusCode);
        });

        secured.MapGet("/manifest/status", (IManifestService manifestService) =>
        {
            ManifestStatusSnapshot status = manifestService.GetStatus();
            return Results.Json(ProtocolEnvelope.Api(
                "manifest.status",
                new ManifestStatusResponse(
                    status.Source,
                    status.UsingCache,
                    status.ManifestVersion,
                    status.Checksum,
                    status.LastUpdatedUtc,
                    status.LastError),
                error: status.LastError));
        });

        secured.MapPost("/manifest/refresh", async (IManifestService manifestService, CancellationToken cancellationToken) =>
        {
            UtilityLog.ManifestRefreshing(apiLogger);
            ManifestRefreshResult result = await manifestService.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(ProtocolEnvelope.Api(
                "manifest.refreshed",
                new ManifestStatusResponse(
                    result.Status.Source,
                    result.Status.UsingCache,
                    result.Status.ManifestVersion,
                    result.Status.Checksum,
                    result.Status.LastUpdatedUtc,
                    result.Status.LastError),
                error: result.Status.LastError),
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        secured.MapPost("/utility/open-ui", async (UtilityControlRequest request, IUtilityControlService controlService, CancellationToken cancellationToken) =>
        {
            UtilityControlResponse response = await controlService.OpenUiAsync(request.Force, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ProtocolEnvelope.Api("utility.open-ui", response),
                statusCode: response.Accepted ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        secured.MapPost("/utility/shutdown", async (UtilityControlRequest request, IUtilityControlService controlService, CancellationToken cancellationToken) =>
        {
            UtilityControlResponse response = await controlService.ShutdownAsync(request.Reason, request.Force, cancellationToken).ConfigureAwait(false);
            return Results.Json(
                ProtocolEnvelope.Api("utility.shutdown", response),
                statusCode: response.Accepted ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        app.Map(ProtocolConstants.SessionWebSocketPath, HandleWebSocketAsync);
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ISecurityValidator security = context.RequestServices.GetRequiredService<ISecurityValidator>();
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebBridge.Utility.WebSocket");
        SecurityValidationResult loopback = security.ValidateLoopback(context.Connection.RemoteIpAddress?.ToString());
        if (!loopback.IsAllowed)
        {
            await WriteSecurityErrorAsync(context, loopback).ConfigureAwait(false);
            return;
        }

        SecurityValidationResult origin = security.ValidateOrigin(context.Request.Headers.Origin.ToString());
        if (!origin.IsAllowed)
        {
            await WriteSecurityErrorAsync(context, origin).ConfigureAwait(false);
            return;
        }

        SecurityValidationResult token = security.ValidatePairingToken(context.Request.Query["token"].ToString());
        if (!token.IsAllowed)
        {
            await WriteSecurityErrorAsync(context, token).ConfigureAwait(false);
            return;
        }

        string sessionId = context.Request.Query["sessionId"].ToString();
        ISessionManager sessionManager = context.RequestServices.GetRequiredService<ISessionManager>();
        SessionSocketHub hub = context.RequestServices.GetRequiredService<SessionSocketHub>();

        if (!sessionManager.TryAttachSocket(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        hub.Attach(sessionId, webSocket);
        UtilityLog.WebSocketAccepted(logger, sessionId);

        byte[] buffer = new byte[8192];
        try
        {
            while (webSocket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                JsonNode? json = JsonNode.Parse(message);
                string? type = json?["type"]?.GetValue<string>();
                string? correlationId = json?["correlationId"]?.GetValue<string>();

                if (string.Equals(type, "hello", StringComparison.OrdinalIgnoreCase))
                {
                    sessionManager.TryAcceptHello(sessionId);
                    UtilityLog.WebSocketHello(logger, sessionId);
                    await hub.SendAsync(
                        sessionId,
                        "hello",
                        new JsonObject
                        {
                            ["sessionId"] = sessionId,
                            ["activeSessionCount"] = sessionManager.ActiveSessionCount,
                        },
                        correlationId,
                        context.RequestAborted).ConfigureAwait(false);
                }
                else if (string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    sessionManager.TryHeartbeat(sessionId);
                    UtilityLog.WebSocketHeartbeat(logger, sessionId);
                    await hub.SendAsync(
                        sessionId,
                        "heartbeat",
                        new JsonObject { ["sessionId"] = sessionId },
                        correlationId,
                        context.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    await hub.SendAsync(
                        sessionId,
                        "error",
                        null,
                        correlationId,
                        context.RequestAborted,
                        new ApiError("ws_message_not_supported", $"Unsupported WebSocket message type '{type ?? "<null>"}'."))
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            UtilityLog.WebSocketRequestAborted(logger, sessionId);
        }
        catch (WebSocketException exception) when (IsExpectedSocketClosure(exception))
        {
            UtilityLog.WebSocketRemoteClosed(logger, exception, sessionId);
        }
        catch (IOException exception) when (IsExpectedSocketClosure(exception))
        {
            UtilityLog.WebSocketTransportClosed(logger, exception, sessionId);
        }
        finally
        {
            sessionManager.MarkSocketClosed(sessionId, "socket-closed");
            hub.Detach(sessionId);
            UtilityLog.WebSocketClosed(logger, sessionId);
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException exception) when (IsExpectedSocketClosure(exception))
                {
                    UtilityLog.WebSocketCloseRaceIgnored(logger, exception, sessionId);
                }
                catch (IOException exception) when (IsExpectedSocketClosure(exception))
                {
                    UtilityLog.WebSocketTransportCloseRaceIgnored(logger, exception, sessionId);
                }
            }
        }
    }

    private static bool IsExpectedSocketClosure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is WebSocketException webSocketException)
        {
            return webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
                || webSocketException.InnerException is ConnectionResetException
                || webSocketException.InnerException is IOException
                || webSocketException.Message.Contains("close handshake", StringComparison.OrdinalIgnoreCase);
        }

        if (exception is IOException ioException)
        {
            return ioException.InnerException is ConnectionResetException
                || ioException.InnerException is SocketException socketException && socketException.SocketErrorCode == SocketError.ConnectionReset;
        }

        return false;
    }

    private static IResult ToSecurityResult(SecurityValidationResult result)
    {
        return Results.Json(
            ProtocolEnvelope.Api<object>("security.error", null, error: result.Error),
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task WriteSecurityErrorAsync(HttpContext context, SecurityValidationResult result)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(ProtocolEnvelope.Api<object>("security.error", null, error: result.Error)).ConfigureAwait(false);
    }
}


using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility;

public sealed class SessionSocketHub
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public void Attach(string sessionId, WebSocket socket)
    {
        _sockets[sessionId] = socket;
    }

    public void Detach(string sessionId)
    {
        _sockets.TryRemove(sessionId, out _);
    }

    public Task BroadcastAsync(string type, JsonNode? payload, string? correlationId, CancellationToken cancellationToken)
    {
        Task[] tasks = _sockets.Keys
            .Select(sessionId => SendAsync(sessionId, type, payload, correlationId, cancellationToken))
            .ToArray();
        return Task.WhenAll(tasks);
    }

    public async Task SendAsync(
        string sessionId,
        string type,
        JsonNode? payload,
        string? correlationId,
        CancellationToken cancellationToken,
        ApiError? error = null)
    {
        if (!_sockets.TryGetValue(sessionId, out WebSocket? socket) || socket.State != WebSocketState.Open)
        {
            return;
        }

        WsEnvelope<JsonNode?> envelope = new(
            Guid.NewGuid().ToString("N"),
            type,
            DateTimeOffset.UtcNow,
            correlationId,
            payload,
            error);

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, _serializerOptions));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ActivationRequestProcessor
{
    private readonly UtilityRuntimeStateTracker _stateTracker;
    private readonly IManifestService _manifestService;
    private readonly ISessionManager _sessionManager;
    private readonly IBrowserLaunchDecider _decider;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IUtilityControlService _controlService;
    private readonly UtilitySettings _settings;
    private readonly SessionSocketHub _hub;
    private readonly ILogger<ActivationRequestProcessor> _logger;

    public ActivationRequestProcessor(
        UtilityRuntimeStateTracker stateTracker,
        IManifestService manifestService,
        ISessionManager sessionManager,
        IBrowserLaunchDecider decider,
        IBrowserLauncher browserLauncher,
        IUtilityControlService controlService,
        UtilitySettings settings,
        SessionSocketHub hub,
        ILogger<ActivationRequestProcessor> logger)
    {
        _stateTracker = stateTracker;
        _manifestService = manifestService;
        _sessionManager = sessionManager;
        _decider = decider;
        _browserLauncher = browserLauncher;
        _controlService = controlService;
        _settings = settings;
        _hub = hub;
        _logger = logger;
    }

    public async Task HandleAsync(ActivationRequest request, CancellationToken cancellationToken)
    {
        _stateTracker.RecordActivation(request);
        UtilityLog.ActivationReceived(_logger, request.RequestBrowserOpen, request.RequestManifestRefresh, request.RequestShutdown);
        await _hub.BroadcastAsync(
            "log/event",
            new JsonObject
            {
                ["message"] = "secondary-instance-activation",
                ["requestedAtUtc"] = request.RequestedAtUtc,
                ["requestBrowserOpen"] = request.RequestBrowserOpen,
                ["requestManifestRefresh"] = request.RequestManifestRefresh,
                ["requestShutdown"] = request.RequestShutdown,
            },
            null,
            cancellationToken).ConfigureAwait(false);

        if (request.RequestManifestRefresh)
        {
            UtilityLog.ActivationManifestRefresh(_logger);
            await _manifestService.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (request.RequestBrowserOpen &&
            !string.IsNullOrWhiteSpace(_settings.UiUrl) &&
            _decider.ShouldLaunchBrowser(_settings, _sessionManager.HasPresenceSessions, _sessionManager.HasActiveSessions))
        {
            UtilityLog.ActivationBrowserLaunch(_logger, _settings.UiUrl);
            await _browserLauncher.LaunchAsync(new Uri(_settings.UiUrl), cancellationToken).ConfigureAwait(false);
        }

        if (request.RequestShutdown)
        {
            UtilityLog.ActivationShutdown(_logger);
            _ = await _controlService.ShutdownAsync("secondary-instance-request", force: true, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class ActivationBackgroundService : BackgroundService
{
    private readonly Channel<ActivationRequest> _channel;
    private readonly ActivationRequestProcessor _processor;

    public ActivationBackgroundService(Channel<ActivationRequest> channel, ActivationRequestProcessor processor)
    {
        _channel = channel;
        _processor = processor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ActivationRequest request in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _processor.HandleAsync(request, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public sealed class UtilityBackgroundService : BackgroundService
{
    private readonly IManifestService _manifestService;
    private readonly ISessionManager _sessionManager;
    private readonly IIdleShutdownSupervisor _idleShutdownSupervisor;
    private readonly UtilityRuntimeStateTracker _stateTracker;
    private readonly UtilitySettings _settings;
    private readonly IBrowserLaunchDecider _browserLaunchDecider;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IUtilityClock _clock;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly SessionSocketHub _hub;
    private readonly ILogger<UtilityBackgroundService> _logger;
    private int _lastWarningSecond = int.MinValue;

    public UtilityBackgroundService(
        IManifestService manifestService,
        ISessionManager sessionManager,
        IIdleShutdownSupervisor idleShutdownSupervisor,
        UtilityRuntimeStateTracker stateTracker,
        UtilitySettings settings,
        IBrowserLaunchDecider browserLaunchDecider,
        IBrowserLauncher browserLauncher,
        IUtilityClock clock,
        IHostApplicationLifetime appLifetime,
        SessionSocketHub hub,
        ILogger<UtilityBackgroundService> logger)
    {
        _manifestService = manifestService;
        _sessionManager = sessionManager;
        _idleShutdownSupervisor = idleShutdownSupervisor;
        _stateTracker = stateTracker;
        _settings = settings;
        _browserLaunchDecider = browserLaunchDecider;
        _browserLauncher = browserLauncher;
        _clock = clock;
        _appLifetime = appLifetime;
        _hub = hub;
        _logger = logger;

        _sessionManager.ActiveSessionCountChanged += OnActiveSessionCountChanged;
        _idleShutdownSupervisor.ShutdownRequested += (_, eventArgs) =>
        {
            UtilityLog.IdleShutdownRequested(_logger, eventArgs.RequestedAtUtc);
            _appLifetime.StopApplication();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _stateTracker.TransitionTo(UtilityRuntimeState.Starting);
            UtilityLog.BackgroundStarting(_logger);
            await _manifestService.InitializeAsync(stoppingToken).ConfigureAwait(false);
            _stateTracker.TransitionTo(UtilityRuntimeState.WaitingForSession);
            UtilityLog.BackgroundWaitingForSession(_logger);

            DateTimeOffset waitUntil = _clock.UtcNow.AddSeconds(_settings.SessionWaitSeconds);
            while (_clock.UtcNow < waitUntil &&
                !ShouldSuppressAutoOpen() &&
                !stoppingToken.IsCancellationRequested)
            {
                await _clock.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_settings.UiUrl) &&
                _browserLaunchDecider.ShouldLaunchBrowser(_settings, _sessionManager.HasPresenceSessions, _sessionManager.HasActiveSessions))
            {
                UtilityLog.BackgroundLaunchingBrowser(_logger, _settings.UiUrl);
                await _browserLauncher.LaunchAsync(new Uri(_settings.UiUrl), stoppingToken).ConfigureAwait(false);
            }

            _idleShutdownSupervisor.BeginMonitoring();
            _idleShutdownSupervisor.NotifyActiveSessionCountChanged(_sessionManager.ActiveSessionCount);

            while (!stoppingToken.IsCancellationRequested)
            {
                _sessionManager.SweepExpiredSessions();
                _idleShutdownSupervisor.Evaluate();
                await SendShutdownWarningIfNeededAsync(stoppingToken).ConfigureAwait(false);
                await _clock.Delay(TimeSpan.FromSeconds(_settings.Session.SweepIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _stateTracker.TransitionTo(UtilityRuntimeState.Stopped);
            UtilityLog.BackgroundStopped(_logger);
        }
    }

    private async Task SendShutdownWarningIfNeededAsync(CancellationToken cancellationToken)
    {
        TimeSpan? remaining = _idleShutdownSupervisor.GetRemaining();
        if (remaining is null || remaining > TimeSpan.FromSeconds(10))
        {
            return;
        }

        int seconds = (int)Math.Ceiling(remaining.Value.TotalSeconds);
        if (seconds == _lastWarningSecond)
        {
            return;
        }

        _lastWarningSecond = seconds;
        UtilityLog.ShutdownWarning(_logger, seconds);
        await _hub.BroadcastAsync(
            "shutdown-warning",
            new JsonObject { ["remainingSeconds"] = seconds },
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private void OnActiveSessionCountChanged(object? sender, ActiveSessionCountChangedEventArgs eventArgs)
    {
        UtilityLog.ActiveSessionCountChanged(_logger, eventArgs.ActiveCount);
        _idleShutdownSupervisor.NotifyActiveSessionCountChanged(eventArgs.ActiveCount);
    }

    private bool ShouldSuppressAutoOpen()
    {
        return _settings.Session.SuppressAutoOpenOnPresenceSessions
            ? _sessionManager.HasPresenceSessions
            : _sessionManager.HasActiveSessions;
    }
}

public sealed class SecurityEndpointFilter : IEndpointFilter
{
    private readonly bool _requireOrigin;
    private readonly bool _requireToken;

    public SecurityEndpointFilter(bool requireOrigin, bool requireToken)
    {
        _requireOrigin = requireOrigin;
        _requireToken = requireToken;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        ISecurityValidator validator = httpContext.RequestServices.GetRequiredService<ISecurityValidator>();

        SecurityValidationResult loopback = validator.ValidateLoopback(httpContext.Connection.RemoteIpAddress?.ToString());
        if (!loopback.IsAllowed)
        {
            return Results.Json(ProtocolEnvelope.Api<object>("security.error", null, error: loopback.Error), statusCode: StatusCodes.Status403Forbidden);
        }

        if (_requireOrigin)
        {
            SecurityValidationResult origin = validator.ValidateOrigin(httpContext.Request.Headers.Origin.ToString());
            if (!origin.IsAllowed)
            {
                return Results.Json(ProtocolEnvelope.Api<object>("security.error", null, error: origin.Error), statusCode: StatusCodes.Status403Forbidden);
            }
        }

        if (_requireToken)
        {
            SecurityValidationResult token = validator.ValidatePairingToken(httpContext.Request.Headers[ProtocolConstants.PairingTokenHeader].ToString());
            if (!token.IsAllowed)
            {
                return Results.Json(ProtocolEnvelope.Api<object>("security.error", null, error: token.Error), statusCode: StatusCodes.Status401Unauthorized);
            }
        }

        return await next(context).ConfigureAwait(false);
    }
}


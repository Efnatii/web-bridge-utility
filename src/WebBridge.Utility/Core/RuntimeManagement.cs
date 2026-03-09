using System.Text.Json;
using WebBridge.Utility;
using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

public sealed class RuntimeConfigurationManager : IRuntimeConfigurationManager
{
    private static readonly JsonSerializerOptions ConfigSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly UtilitySettings _settings;
    private readonly IManifestService _manifestService;
    private readonly IUtilityClock _clock;
    private readonly ILogger<RuntimeConfigurationManager> _logger;
    private readonly object _sync = new();
    private DateTimeOffset _loadedAtUtc;

    public RuntimeConfigurationManager(
        UtilitySettings settings,
        IManifestService manifestService,
        IUtilityClock clock,
        ILogger<RuntimeConfigurationManager> logger)
    {
        _settings = settings;
        _manifestService = manifestService;
        _clock = clock;
        _logger = logger;
        _loadedAtUtc = clock.UtcNow;
    }

    public UtilitySettings GetSettingsSnapshot()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public ConfigVersionResponse GetVersion()
    {
        UtilitySettings snapshot = GetSettingsSnapshot();
        ManifestStatusSnapshot manifestStatus = _manifestService.GetStatus();
        IReadOnlyCollection<ProfileVersionInfo> profiles = snapshot.Profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProfileId))
            .OrderBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new ProfileVersionInfo(
                profile.ProfileId,
                profile.Checksum,
                profile.Commands.Count))
            .ToArray();

        return new ConfigVersionResponse(
            snapshot.ConfigVersion,
            snapshot.ConfigSchemaVersion,
            snapshot.UtilityVersion,
            manifestStatus.ManifestVersion ?? snapshot.Manifest?.ManifestVersion,
            manifestStatus.Checksum ?? snapshot.Manifest?.Checksum,
            profiles.Count,
            _loadedAtUtc,
            profiles);
    }

    public async Task<ConfigUpdateResponse> ApplyAsync(LoadConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Settings);

        UtilitySettings currentSnapshot = GetSettingsSnapshot();
        UtilitySettings next = request.Settings.Clone();
        next.ConfigPath = string.IsNullOrWhiteSpace(next.ConfigPath)
            ? currentSnapshot.ConfigPath
            : next.ConfigPath;
        UtilityCli.NormalizeEffectiveSettings(next, next.ConfigPath);

        bool restartRequired = RequiresRestart(currentSnapshot, next);
        bool persisted = false;

        lock (_sync)
        {
            _settings.ApplyFrom(next);
            _loadedAtUtc = _clock.UtcNow;
        }

        if (request.Persist)
        {
            persisted = await PersistAsync(next, cancellationToken).ConfigureAwait(false);
        }

        string message = restartRequired
            ? "Конфиг применён. Для части параметров требуется перезапуск агента."
            : "Конфиг применён без обязательного перезапуска.";

        try
        {
            await _manifestService.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ManagementLog.ConfigManifestInitializationFailed(_logger, exception);
            message = $"{message} Инициализация manifest завершилась ошибкой: {exception.Message}";
        }

        return new ConfigUpdateResponse(true, persisted, restartRequired, message, GetVersion());
    }

    public async Task<ConfigUpdateResponse> ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        UtilitySettings current = GetSettingsSnapshot();
        if (string.IsNullOrWhiteSpace(current.ConfigPath))
        {
            return new ConfigUpdateResponse(false, false, false, "У агента не задан ConfigPath.", GetVersion());
        }

        UtilitySettings? loaded = UtilityCli.LoadStructuredConfig(current.ConfigPath);
        if (loaded is null)
        {
            return new ConfigUpdateResponse(false, false, false, $"Не удалось прочитать config: {current.ConfigPath}", GetVersion());
        }

        loaded.ConfigPath = current.ConfigPath;
        return await ApplyAsync(new LoadConfigRequest(loaded, Persist: false), cancellationToken).ConfigureAwait(false);
    }

    private static bool RequiresRestart(UtilitySettings current, UtilitySettings next)
    {
        return !string.Equals(current.ListenUrl, next.ListenUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.EnvironmentName, next.EnvironmentName, StringComparison.OrdinalIgnoreCase) ||
            current.DebugMode != next.DebugMode ||
            !string.Equals(current.LogLevel, next.LogLevel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.LogFilePath, next.LogFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> PersistAsync(UtilitySettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ConfigPath))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(settings.ConfigPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, ConfigSerializerOptions);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class UtilityControlService : IUtilityControlService
{
    private readonly UtilitySettings _settings;
    private readonly UtilityRuntimeStateTracker _stateTracker;
    private readonly ISessionManager _sessionManager;
    private readonly IBrowserLaunchDecider _browserLaunchDecider;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly SessionSocketHub _hub;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<UtilityControlService> _logger;

    public UtilityControlService(
        UtilitySettings settings,
        UtilityRuntimeStateTracker stateTracker,
        ISessionManager sessionManager,
        IBrowserLaunchDecider browserLaunchDecider,
        IBrowserLauncher browserLauncher,
        SessionSocketHub hub,
        IHostApplicationLifetime applicationLifetime,
        ILogger<UtilityControlService> logger)
    {
        _settings = settings;
        _stateTracker = stateTracker;
        _sessionManager = sessionManager;
        _browserLaunchDecider = browserLaunchDecider;
        _browserLauncher = browserLauncher;
        _hub = hub;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    public async Task<UtilityControlResponse> OpenUiAsync(bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.UiUrl))
        {
            return CreateResponse("open-ui", accepted: false, "UiUrl в config не задан.");
        }

        if (!force && !_browserLaunchDecider.ShouldLaunchBrowser(_settings, _sessionManager.HasPresenceSessions, _sessionManager.HasActiveSessions))
        {
            return CreateResponse("open-ui", accepted: false, "Политика OpenUi запрещает автоматическое открытие UI в текущем состоянии.");
        }

        await _browserLauncher.LaunchAsync(new Uri(_settings.UiUrl), cancellationToken).ConfigureAwait(false);
        ManagementLog.UiLaunchRequested(_logger, force, _settings.UiUrl);
        await _hub.BroadcastAsync(
            "log/event",
            new System.Text.Json.Nodes.JsonObject
            {
                ["message"] = "ui-open-requested",
                ["force"] = force,
                ["uiUrl"] = _settings.UiUrl,
            },
            null,
            cancellationToken).ConfigureAwait(false);
        return CreateResponse("open-ui", accepted: true, "Запуск UI отправлен оболочке ОС.");
    }

    public async Task<UtilityControlResponse> ShutdownAsync(string? reason, bool force, CancellationToken cancellationToken)
    {
        if (_sessionManager.HasActiveSessions && !force)
        {
            return CreateResponse("shutdown", accepted: false, "Есть активные UI-сессии. Для принудительного завершения укажите force=true.");
        }

        _stateTracker.TransitionTo(UtilityRuntimeState.Stopping);
        ManagementLog.UtilityShutdownRequested(_logger, force, reason);
        await _hub.BroadcastAsync(
            "log/event",
            new System.Text.Json.Nodes.JsonObject
            {
                ["message"] = "utility-shutdown-requested",
                ["reason"] = reason,
                ["force"] = force,
            },
            null,
            cancellationToken).ConfigureAwait(false);
        _applicationLifetime.StopApplication();
        return CreateResponse("shutdown", accepted: true, "Завершение агента запрошено.");
    }

    private UtilityControlResponse CreateResponse(string action, bool accepted, string message)
    {
        return new UtilityControlResponse(
            action,
            accepted,
            message,
            _stateTracker.State,
            _sessionManager.ActiveSessionCount);
    }
}

internal static partial class ManagementLog
{
    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "Config was applied, but manifest initialization failed after runtime update.")]
    public static partial void ConfigManifestInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "UI launch requested from runtime control. Force={Force}, UiUrl={UiUrl}.")]
    public static partial void UiLaunchRequested(ILogger logger, bool force, string? uiUrl);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Utility shutdown requested. Force={Force}, Reason={Reason}.")]
    public static partial void UtilityShutdownRequested(ILogger logger, bool force, string? reason);
}


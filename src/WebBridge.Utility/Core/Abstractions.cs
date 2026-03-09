using System.Text.Json.Nodes;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Core;

public interface IUtilityClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

public interface ISessionManager
{
    event EventHandler<ActiveSessionCountChangedEventArgs>? ActiveSessionCountChanged;

    SessionRegistrationResult Register(RegisterSessionRequest request);

    bool TryAttachSocket(string sessionId);

    bool TryAcceptHello(string sessionId);

    bool TryHeartbeat(string sessionId);

    bool Close(string sessionId, string? reason);

    bool MarkSocketClosed(string sessionId, string? reason);

    IReadOnlyCollection<SessionSnapshot> GetSessions();

    int ActiveSessionCount { get; }

    int PresenceSessionCount { get; }

    bool HasActiveSessions { get; }

    bool HasPresenceSessions { get; }

    void SweepExpiredSessions();
}

public interface IIdleShutdownSupervisor
{
    event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;

    void BeginMonitoring();

    void NotifyActiveSessionCountChanged(int activeCount);

    void Evaluate();

    TimeSpan? GetRemaining();
}

public interface IManifestService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<ManifestRefreshResult> RefreshAsync(CancellationToken cancellationToken);

    ManifestStatusSnapshot GetStatus();
}

public interface IProfileStore
{
    Task<ProfileDefinition?> GetProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ProfileDefinition>> ListProfilesAsync(CancellationToken cancellationToken);
}

public interface ICommandDispatcher
{
    Task<CommandExecutionResult> ExecuteAsync(ExecuteCommandRequest request, CancellationToken cancellationToken);

    Task<ExecuteBatchCommandResponse> ExecuteBatchAsync(ExecuteBatchCommandRequest request, CancellationToken cancellationToken);
}

public interface ICommandPlanCompiler
{
    PreparedCommandPlan Compile(ProfileDefinition profile, CommandDefinition definition);
}

public interface IAdapterInvokeSurface
{
    string AdapterName { get; }

    PreparedCommandPlan Compile(CommandDefinition definition);
}

public interface IReflectiveInvokeRuntime
{
    string AdapterName { get; }

    object? ResolveRoot(string rootName, JsonObject arguments);

    JsonNode? ConvertResult(object? value, InvokeDefinition definition, JsonObject arguments);

    ReportVerbosity GetDefaultReportVerbosity(JsonObject arguments);

    Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken);

    CommandExecutionResult MapInvokeException(string commandId, Exception exception, InvokeDefinition definition);
}

public interface IHandleArgumentResolver
{
    object ResolveHandleArgument(string handleId);
}

public interface IPathArgumentNormalizer
{
    string NormalizePathArgument(string path);
}

public interface IReflectiveMemberProvider
{
    bool TryGetReflectiveMember(string member, out object? value);
}

public interface IBrowserLauncher
{
    Task LaunchAsync(Uri uri, CancellationToken cancellationToken);
}

public interface ISingleInstanceCoordinator : IAsyncDisposable
{
    Task<SingleInstanceInitializationResult> InitializeAsync(
        string instanceName,
        Func<ActivationRequest, Task> onActivation,
        CancellationToken cancellationToken);

    Task<bool> SendActivationAsync(string instanceName, ActivationRequest request, CancellationToken cancellationToken);
}

public interface ISecurityValidator
{
    SecurityValidationResult ValidateLoopback(string? remoteIpAddress);

    SecurityValidationResult ValidateOrigin(string? origin);

    SecurityValidationResult ValidatePairingToken(string? token);
}

public interface IVersionCompatibilityService
{
    CompatibilityEvaluation Evaluate(Manifest manifest, string utilityVersion);
}

public interface IRuntimeConfigurationManager
{
    UtilitySettings GetSettingsSnapshot();

    ConfigVersionResponse GetVersion();

    Task<ConfigUpdateResponse> ApplyAsync(LoadConfigRequest request, CancellationToken cancellationToken);

    Task<ConfigUpdateResponse> ReloadFromDiskAsync(CancellationToken cancellationToken);
}

public interface IBrowserLaunchDecider
{
    bool ShouldLaunchBrowser(UtilitySettings settings, bool hasPresenceSession, bool hasActiveSession);
}

public interface IUtilityControlService
{
    Task<UtilityControlResponse> OpenUiAsync(bool force, CancellationToken cancellationToken);

    Task<UtilityControlResponse> ShutdownAsync(string? reason, bool force, CancellationToken cancellationToken);
}

public sealed record SessionRegistrationResult(string SessionId, int HeartbeatIntervalSeconds, int ActiveSessionCount, int PresenceSessionCount);

public sealed record SessionSnapshot(
    string SessionId,
    string ClientName,
    string UiVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenUtc,
    bool SocketAttached,
    bool HelloReceived,
    bool IsPresent,
    bool IsInteractive,
    bool IsActive,
    string? ClosingReason);

public sealed class ActiveSessionCountChangedEventArgs : EventArgs
{
    public ActiveSessionCountChangedEventArgs(int activeCount)
    {
        ActiveCount = activeCount;
    }

    public int ActiveCount { get; }
}

public sealed class ShutdownRequestedEventArgs : EventArgs
{
    public ShutdownRequestedEventArgs(DateTimeOffset requestedAtUtc)
    {
        RequestedAtUtc = requestedAtUtc;
    }

    public DateTimeOffset RequestedAtUtc { get; }
}

public sealed record SingleInstanceInitializationResult(bool IsPrimary, string InstanceName);

public sealed record PreparedCommandPlan(
    string AdapterName,
    string CommandId,
    Func<JsonObject, CancellationToken, Task<CommandExecutionResult>> ExecuteAsync);

public sealed record SecurityValidationResult(bool IsAllowed, ApiError? Error)
{
    public static SecurityValidationResult Allowed() => new(true, null);

    public static SecurityValidationResult Rejected(string code, string message)
        => new(false, new ApiError(code, message));
}

public sealed record CompatibilityEvaluation(bool IsCompatible, string? Warning, string? Error);

public sealed record ManifestRefreshResult(bool Success, ManifestStatusSnapshot Status);

public sealed record ManifestStatusSnapshot(
    string Source,
    bool UsingCache,
    string? ManifestVersion,
    string? Checksum,
    DateTimeOffset? LastUpdatedUtc,
    ApiError? LastError);


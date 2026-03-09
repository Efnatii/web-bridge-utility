using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

internal static partial class CoreLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Registered session {SessionId} for client {ClientName}.")]
    public static partial void SessionRegistered(ILogger logger, string sessionId, string clientName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Active session count changed from {PreviousCount} to {CurrentCount} during sweep.")]
    public static partial void SessionCountChangedDuringSweep(ILogger logger, int previousCount, int currentCount);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Active session count changed from {PreviousCount} to {CurrentCount} for session {SessionId}.")]
    public static partial void SessionCountChangedForSession(ILogger logger, int previousCount, int currentCount, string sessionId);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Idle shutdown monitoring enabled. Policy={Policy}, Timeout={IdleTimeout}.")]
    public static partial void IdleMonitoringEnabled(ILogger logger, ShutdownPolicy policy, TimeSpan idleTimeout);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Idle shutdown cancelled because active sessions are present.")]
    public static partial void IdleCancelled(ILogger logger);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "Idle shutdown not armed because policy is {Policy}.")]
    public static partial void IdleNotArmed(ILogger logger, ShutdownPolicy policy);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, Message = "Idle shutdown countdown armed for {DeadlineUtc}.")]
    public static partial void IdleCountdownArmed(ILogger logger, DateTimeOffset? deadlineUtc);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Idle shutdown deadline reached.")]
    public static partial void IdleDeadlineReached(ILogger logger);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Dispatching command {ProfileId}/{CommandId}.")]
    public static partial void DispatchingCommand(ILogger logger, string profileId, string commandId);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Profile {ProfileId} was not found for command {CommandId}.")]
    public static partial void ProfileNotFound(ILogger logger, string profileId, string commandId);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Command {CommandId} was not found in profile {ProfileId}.")]
    public static partial void CommandNotFound(ILogger logger, string commandId, string profileId);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Error, Message = "Failed to compile command {ProfileId}/{CommandId}.")]
    public static partial void CommandCompileFailed(ILogger logger, Exception exception, string profileId, string commandId);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Information, Message = "Invoke command {ProfileId}/{CommandId} finished. Success={Success}.")]
    public static partial void InvokeFinished(ILogger logger, string profileId, string commandId, bool success);

    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "Manifest service initialized from embedded config.")]
    public static partial void ManifestInitializedEmbedded(ILogger logger);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Manifest service initialized without remote URL. Source={Source}.")]
    public static partial void ManifestInitializedWithoutRemote(ILogger logger, string source);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information, Message = "Manifest refresh skipped because ManifestUrl is not configured. Source={Source}.")]
    public static partial void ManifestRefreshSkipped(ILogger logger, string source);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Information, Message = "Refreshing manifest from {ManifestUrl}.")]
    public static partial void ManifestRefreshStarting(ILogger logger, string manifestUrl);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "Remote manifest is incompatible with utility version {UtilityVersion}.")]
    public static partial void ManifestIncompatible(ILogger logger, string utilityVersion);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Information, Message = "Manifest refresh completed successfully. Version={ManifestVersion}.")]
    public static partial void ManifestRefreshCompleted(ILogger logger, string manifestVersion);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Warning, Message = "Manifest refresh failed. Falling back to local sources if possible.")]
    public static partial void ManifestRefreshFailed(ILogger logger, Exception exception);
}


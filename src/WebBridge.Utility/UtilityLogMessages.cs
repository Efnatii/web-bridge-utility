using Microsoft.Extensions.Logging;

namespace WebBridge.Utility;

internal static partial class UtilityLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Utility started. ListenUrl={ListenUrl}, DebugMode={DebugMode}, LogFilePath={LogFilePath}, ConfigPath={ConfigPath}.")]
    public static partial void UtilityStarted(ILogger logger, string listenUrl, bool debugMode, string? logFilePath, string? configPath);

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Registered UI session {SessionId} for client {ClientName}. Active sessions: {ActiveSessionCount}.")]
    public static partial void SessionRegistered(ILogger logger, string sessionId, string clientName, int activeSessionCount);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Closed UI session {SessionId}. Closed={Closed}. Reason={Reason}.")]
    public static partial void SessionClosed(ILogger logger, string sessionId, bool closed, string? reason);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Executing command {ProfileId}/{CommandId}.")]
    public static partial void CommandExecuting(ILogger logger, string profileId, string commandId);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Information, Message = "Command {ProfileId}/{CommandId} finished. Success={Success}. ExecutionId={ExecutionId}.")]
    public static partial void CommandFinished(ILogger logger, string profileId, string commandId, bool success, string executionId);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information, Message = "Refreshing manifest.")]
    public static partial void ManifestRefreshing(ILogger logger);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Information, Message = "Accepted WebSocket for session {SessionId}.")]
    public static partial void WebSocketAccepted(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Debug, Message = "Received hello for session {SessionId}.")]
    public static partial void WebSocketHello(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Debug, Message = "Received heartbeat for session {SessionId}.")]
    public static partial void WebSocketHeartbeat(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Information, Message = "Closed WebSocket for session {SessionId}.")]
    public static partial void WebSocketClosed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "Received activation from secondary instance. BrowserOpen={BrowserOpen}, ManifestRefresh={ManifestRefresh}, Shutdown={Shutdown}.")]
    public static partial void ActivationReceived(ILogger logger, bool browserOpen, bool manifestRefresh, bool shutdown);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Activation requested manifest refresh.")]
    public static partial void ActivationManifestRefresh(ILogger logger);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Activation requested browser launch for {UiUrl}.")]
    public static partial void ActivationBrowserLaunch(ILogger logger, string? uiUrl);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Activation requested utility shutdown.")]
    public static partial void ActivationShutdown(ILogger logger);

    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning, Message = "Idle shutdown requested at {RequestedAtUtc}.")]
    public static partial void IdleShutdownRequested(ILogger logger, DateTimeOffset requestedAtUtc);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Utility background service starting.")]
    public static partial void BackgroundStarting(ILogger logger);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information, Message = "Manifest initialized. Waiting for UI session.")]
    public static partial void BackgroundWaitingForSession(ILogger logger);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Information, Message = "Launching browser for UI URL {UiUrl}.")]
    public static partial void BackgroundLaunchingBrowser(ILogger logger, string? uiUrl);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Information, Message = "Utility background service stopped.")]
    public static partial void BackgroundStopped(ILogger logger);

    [LoggerMessage(EventId = 2305, Level = LogLevel.Warning, Message = "Idle shutdown warning: {RemainingSeconds} seconds remaining.")]
    public static partial void ShutdownWarning(ILogger logger, int remainingSeconds);

    [LoggerMessage(EventId = 2306, Level = LogLevel.Information, Message = "Active UI session count changed to {ActiveCount}.")]
    public static partial void ActiveSessionCountChanged(ILogger logger, int activeCount);

    [LoggerMessage(EventId = 2307, Level = LogLevel.Debug, Message = "WebSocket request aborted for session {SessionId}.")]
    public static partial void WebSocketRequestAborted(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2308, Level = LogLevel.Information, Message = "WebSocket connection closed by remote peer for session {SessionId}.")]
    public static partial void WebSocketRemoteClosed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(EventId = 2309, Level = LogLevel.Information, Message = "WebSocket transport closed for session {SessionId}.")]
    public static partial void WebSocketTransportClosed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(EventId = 2310, Level = LogLevel.Debug, Message = "Ignored WebSocket close race for session {SessionId}.")]
    public static partial void WebSocketCloseRaceIgnored(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(EventId = 2311, Level = LogLevel.Debug, Message = "Ignored WebSocket transport close race for session {SessionId}.")]
    public static partial void WebSocketTransportCloseRaceIgnored(ILogger logger, Exception exception, string sessionId);
}


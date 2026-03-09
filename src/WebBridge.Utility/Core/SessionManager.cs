using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

public sealed class SessionManager : ISessionManager
{
    private readonly IUtilityClock _clock;
    private readonly UtilitySettings _settings;
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ILogger<SessionManager> _logger;
    private int _activeSessionCount;
    private int _presenceSessionCount;

    public SessionManager(IUtilityClock clock, UtilitySettings settings, ILogger<SessionManager> logger)
    {
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<ActiveSessionCountChangedEventArgs>? ActiveSessionCountChanged;

    public int ActiveSessionCount
    {
        get
        {
            lock (_sync)
            {
                return _activeSessionCount;
            }
        }
    }

    public bool HasActiveSessions => ActiveSessionCount > 0;

    public int PresenceSessionCount
    {
        get
        {
            lock (_sync)
            {
                return _presenceSessionCount;
            }
        }
    }

    public bool HasPresenceSessions => PresenceSessionCount > 0;

    public SessionRegistrationResult Register(RegisterSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sessionId = string.IsNullOrWhiteSpace(request.DesiredSessionId)
            ? Guid.NewGuid().ToString("N")
            : request.DesiredSessionId;

        int activeCount;
        int presenceCount;
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out SessionEntry? existing))
            {
                existing.ClientName = request.ClientName;
                existing.UiVersion = request.UiVersion;
                existing.LastSeenUtc = _clock.UtcNow;
                existing.IsClosed = false;
                existing.ClosingReason = null;
            }
            else
            {
                _sessions[sessionId] = new SessionEntry
                {
                    SessionId = sessionId,
                    ClientName = request.ClientName,
                    UiVersion = request.UiVersion,
                    CreatedAtUtc = _clock.UtcNow,
                    LastSeenUtc = _clock.UtcNow,
                };
            }

            RecalculateCounts_NoLock();
            activeCount = _activeSessionCount;
            presenceCount = _presenceSessionCount;
        }

        CoreLog.SessionRegistered(_logger, sessionId, request.ClientName);
        return new SessionRegistrationResult(sessionId, _settings.Session.HeartbeatIntervalSeconds, activeCount, presenceCount);
    }

    public bool TryAttachSocket(string sessionId)
    {
        return UpdateSession(sessionId, entry =>
        {
            entry.SocketAttached = true;
            entry.LastSeenUtc = _clock.UtcNow;
            entry.IsClosed = false;
            entry.ClosingReason = null;
        });
    }

    public bool TryAcceptHello(string sessionId)
    {
        return UpdateSession(sessionId, entry =>
        {
            entry.SocketAttached = true;
            entry.HelloReceived = true;
            entry.LastSeenUtc = _clock.UtcNow;
            entry.IsClosed = false;
            entry.ClosingReason = null;
        });
    }

    public bool TryHeartbeat(string sessionId)
    {
        return UpdateSession(sessionId, entry =>
        {
            if (entry.SocketAttached)
            {
                entry.LastSeenUtc = _clock.UtcNow;
                entry.IsClosed = false;
            }
        });
    }

    public bool Close(string sessionId, string? reason)
    {
        return UpdateSession(sessionId, entry =>
        {
            entry.SocketAttached = false;
            entry.HelloReceived = false;
            entry.IsClosed = true;
            entry.ClosingReason = reason;
        });
    }

    public bool MarkSocketClosed(string sessionId, string? reason)
    {
        return Close(sessionId, reason);
    }

    public IReadOnlyCollection<SessionSnapshot> GetSessions()
    {
        lock (_sync)
        {
            return _sessions.Values
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public void SweepExpiredSessions()
    {
        int previousCount;
        int currentCount;

        lock (_sync)
        {
            previousCount = _activeSessionCount;
            DateTimeOffset now = _clock.UtcNow;
            TimeSpan heartbeatTimeout = GetHeartbeatTimeout();
            TimeSpan presenceTimeout = GetPresenceTimeout();

            foreach (SessionEntry session in _sessions.Values)
            {
                bool activeExpired = now - session.LastSeenUtc > heartbeatTimeout;
                bool presenceExpired = now - session.LastSeenUtc > presenceTimeout;
                if (activeExpired)
                {
                    session.SocketAttached = false;
                    session.HelloReceived = false;
                    session.ClosingReason ??= "heartbeat-timeout";
                }

                if (presenceExpired)
                {
                    session.SocketAttached = false;
                    session.HelloReceived = false;
                    session.IsClosed = true;
                    session.ClosingReason ??= "presence-timeout";
                }
            }

            RecalculateCounts_NoLock();
            currentCount = _activeSessionCount;
        }

        if (previousCount != currentCount)
        {
            CoreLog.SessionCountChangedDuringSweep(_logger, previousCount, currentCount);
            ActiveSessionCountChanged?.Invoke(this, new ActiveSessionCountChangedEventArgs(currentCount));
        }

    }

    private bool UpdateSession(string sessionId, Action<SessionEntry> update)
    {
        int previousCount;
        int currentCount;

        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out SessionEntry? entry))
            {
                return false;
            }

            previousCount = _activeSessionCount;
            update(entry);
            RecalculateCounts_NoLock();
            currentCount = _activeSessionCount;
        }

        if (previousCount != currentCount)
        {
            CoreLog.SessionCountChangedForSession(_logger, previousCount, currentCount, sessionId);
            ActiveSessionCountChanged?.Invoke(this, new ActiveSessionCountChangedEventArgs(currentCount));
        }

        return true;
    }

    private void RecalculateCounts_NoLock()
    {
        DateTimeOffset now = _clock.UtcNow;
        TimeSpan heartbeatTimeout = GetHeartbeatTimeout();
        TimeSpan presenceTimeout = GetPresenceTimeout();
        _activeSessionCount = _sessions.Values.Count(session =>
            IsActive_NoLock(session, now, heartbeatTimeout));
        _presenceSessionCount = _sessions.Values.Count(session =>
            IsPresent_NoLock(session, now, presenceTimeout));
    }

    private SessionSnapshot ToSnapshot(SessionEntry entry)
    {
        DateTimeOffset now = _clock.UtcNow;
        TimeSpan heartbeatTimeout = GetHeartbeatTimeout();
        TimeSpan presenceTimeout = GetPresenceTimeout();
        bool isPresent = IsPresent_NoLock(entry, now, presenceTimeout);
        bool isInteractive = entry.HelloReceived && isPresent;
        bool isActive = IsActive_NoLock(entry, now, heartbeatTimeout);

        return new SessionSnapshot(
            entry.SessionId,
            entry.ClientName,
            entry.UiVersion,
            entry.CreatedAtUtc,
            entry.LastSeenUtc,
            entry.SocketAttached,
            entry.HelloReceived,
            isPresent,
            isInteractive,
            isActive,
            entry.ClosingReason);
    }

    private TimeSpan GetHeartbeatTimeout()
    {
        return TimeSpan.FromSeconds(Math.Max(1, _settings.Session.HeartbeatTimeoutSeconds));
    }

    private TimeSpan GetPresenceTimeout()
    {
        return TimeSpan.FromSeconds(Math.Max(1, _settings.Session.PresenceTimeoutSeconds));
    }

    private static bool IsPresent_NoLock(SessionEntry session, DateTimeOffset now, TimeSpan presenceTimeout)
    {
        return !session.IsClosed && now - session.LastSeenUtc <= presenceTimeout;
    }

    private static bool IsActive_NoLock(SessionEntry session, DateTimeOffset now, TimeSpan heartbeatTimeout)
    {
        return !session.IsClosed &&
            session.SocketAttached &&
            session.HelloReceived &&
            now - session.LastSeenUtc <= heartbeatTimeout;
    }

    private sealed class SessionEntry
    {
        public string SessionId { get; init; } = string.Empty;

        public string ClientName { get; set; } = string.Empty;

        public string UiVersion { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; init; }

        public DateTimeOffset LastSeenUtc { get; set; }

        public bool SocketAttached { get; set; }

        public bool HelloReceived { get; set; }

        public bool IsClosed { get; set; }

        public string? ClosingReason { get; set; }
    }
}


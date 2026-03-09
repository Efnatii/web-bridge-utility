using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

public sealed class IdleShutdownSupervisor : IIdleShutdownSupervisor
{
    private readonly IUtilityClock _clock;
    private readonly UtilityRuntimeStateTracker _stateTracker;
    private readonly UtilitySettings _settings;
    private readonly ILogger<IdleShutdownSupervisor> _logger;
    private DateTimeOffset? _deadlineUtc;
    private bool _monitoringEnabled;

    public IdleShutdownSupervisor(
        IUtilityClock clock,
        UtilityRuntimeStateTracker stateTracker,
        UtilitySettings settings,
        ILogger<IdleShutdownSupervisor> logger)
    {
        _clock = clock;
        _stateTracker = stateTracker;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<ShutdownRequestedEventArgs>? ShutdownRequested;

    public void BeginMonitoring()
    {
        _monitoringEnabled = true;
        CoreLog.IdleMonitoringEnabled(_logger, GetPolicy(), GetIdleTimeout());
    }

    public void NotifyActiveSessionCountChanged(int activeCount)
    {
        if (!_monitoringEnabled)
        {
            return;
        }

        if (activeCount > 0)
        {
            _deadlineUtc = null;
            _stateTracker.TransitionTo(UtilityRuntimeState.Active);
            CoreLog.IdleCancelled(_logger);
            return;
        }

        ShutdownPolicy policy = GetPolicy();
        if (policy != ShutdownPolicy.WhenIdle)
        {
            _stateTracker.TransitionTo(UtilityRuntimeState.WaitingForSession);
            CoreLog.IdleNotArmed(_logger, policy);
            return;
        }

        _deadlineUtc ??= _clock.UtcNow.Add(GetIdleTimeout());
        _stateTracker.TransitionTo(UtilityRuntimeState.IdleCountdown);
        CoreLog.IdleCountdownArmed(_logger, _deadlineUtc);
    }

    public void Evaluate()
    {
        if (!_monitoringEnabled || GetPolicy() != ShutdownPolicy.WhenIdle || _deadlineUtc is null)
        {
            return;
        }

        if (_clock.UtcNow < _deadlineUtc.Value)
        {
            return;
        }

        _stateTracker.TransitionTo(UtilityRuntimeState.Stopping);
        CoreLog.IdleDeadlineReached(_logger);
        ShutdownRequested?.Invoke(this, new ShutdownRequestedEventArgs(_clock.UtcNow));
    }

    public TimeSpan? GetRemaining()
    {
        if (_deadlineUtc is null)
        {
            return null;
        }

        TimeSpan remaining = _deadlineUtc.Value - _clock.UtcNow;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private ShutdownPolicy GetPolicy() => _settings.Shutdown;

    private TimeSpan GetIdleTimeout() => TimeSpan.FromSeconds(Math.Max(1, _settings.IdleSeconds));
}


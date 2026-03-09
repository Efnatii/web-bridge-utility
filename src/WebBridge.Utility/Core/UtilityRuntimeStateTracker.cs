using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Core;

public sealed class UtilityRuntimeStateTracker
{
    private readonly object _sync = new();

    public UtilityRuntimeStateTracker(IUtilityClock clock)
    {
        StartedAtUtc = clock.UtcNow;
    }

    public UtilityRuntimeState State { get; private set; } = UtilityRuntimeState.Starting;

    public DateTimeOffset StartedAtUtc { get; }

    public int ActivationCount { get; private set; }

    public ActivationRequest? LastActivation { get; private set; }

    public void TransitionTo(UtilityRuntimeState state)
    {
        lock (_sync)
        {
            State = state;
        }
    }

    public void RecordActivation(ActivationRequest request)
    {
        lock (_sync)
        {
            ActivationCount++;
            LastActivation = request;
        }
    }
}


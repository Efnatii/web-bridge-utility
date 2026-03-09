namespace WebBridge.Utility.Core;

internal sealed class RuntimeExecutionContext
{
    public required string AdapterName { get; init; }

    public required string DispatcherName { get; init; }

    public int QueueDepth { get; init; }

    public double QueueWaitMilliseconds { get; init; }

    public IReadOnlyList<int> BusyRetryDelaysMs { get; init; } = Array.Empty<int>();

    public int BusyRetryMaxAttempts { get; init; }
}

internal sealed class RuntimeExecutionContextScope : IDisposable
{
    private static readonly AsyncLocal<RuntimeExecutionContext?> CurrentContext = new();
    private readonly RuntimeExecutionContext? _previous;

    private RuntimeExecutionContextScope(RuntimeExecutionContext context)
    {
        _previous = CurrentContext.Value;
        CurrentContext.Value = context;
    }

    public static RuntimeExecutionContext? Current => CurrentContext.Value;

    public static RuntimeExecutionContextScope Push(RuntimeExecutionContext context) => new(context);

    public void Dispose()
    {
        CurrentContext.Value = _previous;
    }
}

internal sealed class StaInvocationOptions
{
    public required string AdapterName { get; init; }

    public required string DispatcherName { get; init; }

    public IReadOnlyList<int> BusyRetryDelaysMs { get; init; } = Array.Empty<int>();

    public int BusyRetryMaxAttempts { get; init; }
}


using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Core;

public sealed class AdapterInvokeSurfaceAlias : IAdapterInvokeSurface
{
    private readonly IAdapterInvokeSurface _inner;

    public AdapterInvokeSurfaceAlias(string adapterName, IAdapterInvokeSurface inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        ArgumentNullException.ThrowIfNull(inner);

        AdapterName = adapterName;
        _inner = inner;
    }

    public string AdapterName { get; }

    public PreparedCommandPlan Compile(CommandDefinition definition) => _inner.Compile(definition);
}

[SupportedOSPlatform("windows")]
public abstract class ComAutomationRuntimeBase
{
    private readonly Dictionary<string, object> _handles = new(StringComparer.OrdinalIgnoreCase);

    protected object ResolveHandle(JsonObject arguments, string adapterDisplayName)
    {
        string handleId = Require(arguments, "handleId");
        return ResolveRegisteredHandle(handleId, adapterDisplayName);
    }

    protected string RegisterHandle(object value, string? preferredHandleId = null)
    {
        foreach ((string existingId, object existingHandle) in _handles)
        {
            if (ReferenceEquals(existingHandle, value))
            {
                return existingId;
            }
        }

        string handleId = string.IsNullOrWhiteSpace(preferredHandleId)
            ? Guid.NewGuid().ToString("N")
            : preferredHandleId;
        _handles[handleId] = value;
        return handleId;
    }

    protected bool RemoveHandle(string handleId) => _handles.Remove(handleId);

    protected object ResolveRegisteredHandle(string handleId, string adapterDisplayName)
    {
        if (!_handles.TryGetValue(handleId, out object? handle))
        {
            throw new InvalidOperationException($"{adapterDisplayName} COM handle '{handleId}' was not found.");
        }

        return handle;
    }

    protected bool TryConvertGenericComObject(object value, out JsonNode? node)
    {
        if (!Marshal.IsComObject(value) && !value.GetType().IsCOMObject)
        {
            node = null;
            return false;
        }

        node = new JsonObject
        {
            ["handleId"] = RegisterHandle(value),
            ["runtimeType"] = value.GetType().FullName,
            ["stringValue"] = value.ToString(),
            ["memberNames"] = new JsonArray(
                value.GetType()
                    .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Select(member => member.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .Select(name => JsonValue.Create(name))
                    .ToArray()),
        };
        return true;
    }

    protected static string Require(JsonObject arguments, string propertyName)
    {
        string? value = arguments[propertyName]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Property '{propertyName}' is required.");
        }

        return value;
    }

    protected static string? TryReadString(Func<object?> accessor)
    {
        try
        {
            return accessor()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    protected static object? TryReadMemberValue(object target, string member)
    {
        try
        {
            return ReflectiveInvokeAccessor.GetMemberValue(target, member);
        }
        catch
        {
            return null;
        }
    }

    protected static string? TryReadMemberString(object target, string member)
    {
        return TryReadMemberValue(target, member)?.ToString();
    }

    protected static void TryFinalRelease(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
        }
    }

    protected static bool IsVisibleModeEnabled(string envVar = "KWB_REAL_COM_VISIBLE")
    {
        string? value = Environment.GetEnvironmentVariable(envVar);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    protected static string[] ReadProgIds(JsonObject arguments, params string[] defaults)
    {
        if (arguments["progIds"] is JsonArray array && array.Count > 0)
        {
            return array
                .Select(item => item?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }

        string? progId = arguments["progId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(progId))
        {
            return [progId];
        }

        return defaults;
    }

    protected static bool ReadBoolean(JsonObject arguments, string propertyName, bool defaultValue = false)
    {
        JsonNode? node = arguments[propertyName];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out bool booleanValue))
        {
            return booleanValue;
        }

        return bool.TryParse(node.ToString(), out bool parsed)
            ? parsed
            : defaultValue;
    }

    protected static ComApplicationBinding AcquireApplication(
        IEnumerable<string> progIds,
        bool attachOnly = false,
        bool createIfMissing = true,
        Action<object>? initializeExisting = null,
        Action<object>? initializeCreated = null)
    {
        bool discoveredRegisteredProgId = false;
        foreach (string progId in progIds)
        {
            if (ComInteropHelpers.TryGetActiveObject(progId, out object? runningInstance))
            {
                object existingInstance = runningInstance
                    ?? throw new InvalidOperationException($"COM ProgID '{progId}' returned a null application instance.");
                initializeExisting?.Invoke(existingInstance);
                return new ComApplicationBinding(existingInstance, progId, ReusedExistingInstance: true);
            }

            if (attachOnly || !createIfMissing)
            {
                continue;
            }

            Type? type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type is null)
            {
                continue;
            }

            discoveredRegisteredProgId = true;

            object instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Failed to create COM application for ProgID '{progId}'.");
            initializeCreated?.Invoke(instance);
            return new ComApplicationBinding(instance, progId, ReusedExistingInstance: false);
        }

        if (attachOnly || !createIfMissing)
        {
            throw new InvalidOperationException("Requested COM application is not running.");
        }

        if (discoveredRegisteredProgId)
        {
            throw new InvalidOperationException("Failed to create requested COM application instance.");
        }

        throw new InvalidOperationException("Requested COM ProgID is not registered.");
    }
}

public sealed record ComApplicationBinding(object Application, string ProgId, bool ReusedExistingInstance);

[SupportedOSPlatform("windows")]
public sealed class StaOperationDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _workItems = new();
    private readonly Thread _thread;
    private int _queuedWorkItemCount;

    public StaOperationDispatcher(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public string Name => _thread.Name ?? "STA";

    internal Task<T> InvokeAsync<T>(Func<T> action, StaInvocationOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<T> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset queuedAtUtc = DateTimeOffset.UtcNow;
        int queueDepthAfterEnqueue = Interlocked.Increment(ref _queuedWorkItemCount);
        _workItems.Add(() =>
        {
            int queueDepth = Math.Max(0, Interlocked.Decrement(ref _queuedWorkItemCount));
            if (cancellationToken.IsCancellationRequested)
            {
                completionSource.TrySetCanceled();
                return;
            }

            RuntimeExecutionContext context = new()
            {
                AdapterName = options.AdapterName,
                DispatcherName = options.DispatcherName,
                QueueDepth = Math.Max(queueDepth, queueDepthAfterEnqueue - 1),
                QueueWaitMilliseconds = Math.Max(0, (DateTimeOffset.UtcNow - queuedAtUtc).TotalMilliseconds),
                BusyRetryDelaysMs = options.BusyRetryDelaysMs,
                BusyRetryMaxAttempts = options.BusyRetryMaxAttempts,
            };

            try
            {
                using RuntimeExecutionContextScope _ = RuntimeExecutionContextScope.Push(context);
                completionSource.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                completionSource.TrySetCanceled();
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        }, cancellationToken);

        return completionSource.Task;
    }

    public void Dispose()
    {
        if (!_workItems.IsAddingCompleted)
        {
            _workItems.CompleteAdding();
        }
    }

    private void Run()
    {
        using OleMessageFilterRegistration _ = OleMessageFilterRegistration.TryRegister();
        while (!_workItems.IsCompleted)
        {
            if (_workItems.TryTake(out Action? workItem, millisecondsTimeout: 25))
            {
                PumpWindowsMessages();
                workItem();
                PumpWindowsMessages();
                continue;
            }

            PumpWindowsMessages();
        }
    }

    private static void PumpWindowsMessages()
    {
        while (NativeMessagePump.Peek(out NativeMessagePump.MSG message))
        {
            NativeMessagePump.Translate(ref message);
            NativeMessagePump.Dispatch(ref message);
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class OleMessageFilterRegistration : IDisposable
{
    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

        [PreserveSig]
        int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    private sealed class OleMessageFilter : IOleMessageFilter
    {
        private const int ServerCallIsHandled = 0;
        private const int ServerCallRetryLater = 2;
        private const int PendingMessageWaitDefProcess = 2;

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
            => ServerCallIsHandled;

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
            => dwRejectType == ServerCallRetryLater ? 99 : -1;

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
            => PendingMessageWaitDefProcess;
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

    private readonly IOleMessageFilter? _previousFilter;
    private bool _disposed;

    private OleMessageFilterRegistration(IOleMessageFilter? previousFilter)
    {
        _previousFilter = previousFilter;
    }

    public static OleMessageFilterRegistration TryRegister()
    {
        OleMessageFilter filter = new();
        int hr = CoRegisterMessageFilter(filter, out IOleMessageFilter? previousFilter);
        return hr >= 0
            ? new OleMessageFilterRegistration(previousFilter)
            : new OleMessageFilterRegistration(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = CoRegisterMessageFilter(_previousFilter, out _);
    }
}

[SupportedOSPlatform("windows")]
internal static class ComInteropHelpers
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgIDEx(string progId, out Guid clsid);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? unknown);

    public static bool TryGetActiveObject(string progId, out object? instance)
    {
        instance = null;
        int result = CLSIDFromProgIDEx(progId, out Guid clsid);
        if (result < 0)
        {
            result = CLSIDFromProgID(progId, out clsid);
        }

        if (result < 0)
        {
            return false;
        }

        return GetActiveObject(ref clsid, IntPtr.Zero, out instance) >= 0 && instance is not null;
    }
}

[SupportedOSPlatform("windows")]
internal static class NativeMessagePump
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    private const uint PmRemove = 0x0001;

    public static bool Peek(out MSG message) => PeekMessage(out message, IntPtr.Zero, 0, 0, PmRemove);

    public static void Translate(ref MSG message) => _ = TranslateMessage(ref message);

    public static void Dispatch(ref MSG message) => _ = DispatchMessage(ref message);
}


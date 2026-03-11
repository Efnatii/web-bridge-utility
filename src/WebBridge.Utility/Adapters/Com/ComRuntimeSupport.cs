using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WebBridge.Utility.Core;

namespace WebBridge.Utility.Adapters.Com;

public sealed class ComInvokeSurfaceFactory : IComInvokeSurfaceFactory
{
    public IAdapterInvokeSurface Create(ComInvokeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        IReflectiveInvokeRuntime runtime = OperatingSystem.IsWindows()
            ? new ComInvokeRuntime(descriptor.Clone())
            : new UnavailableComInvokeRuntime(
                descriptor.AdapterName,
                string.IsNullOrWhiteSpace(descriptor.UnavailableMessage)
                    ? $"{descriptor.DisplayName} COM runtime is not available."
                    : descriptor.UnavailableMessage,
                descriptor.UnavailableErrorCode);
        return new ComInvokeSurface(runtime);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ComSurfaceCatalog
{
    private readonly ComInvokeDescriptor _descriptor;
    private readonly List<ResolvedComSurface> _surfaces;
    private readonly Dictionary<string, ResolvedComSurface> _surfaceLookup;

    public ComSurfaceCatalog(ComInvokeDescriptor descriptor)
    {
        _descriptor = descriptor;
        LoadInteropAssemblies(descriptor.InteropAssemblies);
        _surfaces = descriptor.Surfaces
            .Where(surface => !string.IsNullOrWhiteSpace(surface.Name))
            .Select(surface => new ResolvedComSurface(surface, ResolveConfiguredType(surface.ClrTypeName)))
            .ToList();
        _surfaceLookup = new Dictionary<string, ResolvedComSurface>(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedComSurface surface in _surfaces)
        {
            foreach (string lookupKey in surface.LookupKeys)
            {
                _surfaceLookup[lookupKey] = surface;
            }
        }
    }

    public bool HasConfiguredSurfaces => _surfaces.Count > 0;

    public ResolvedComSurface? TryResolveSurface(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        return _surfaceLookup.GetValueOrDefault(target);
    }

    public ResolvedComCastTarget ResolveCastTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        ResolvedComSurface? configured = TryResolveSurface(target);
        if (configured is not null)
        {
            return new ResolvedComCastTarget(target, configured, configured.InterfaceId);
        }

        if (Guid.TryParse(target, out Guid iid))
        {
            return new ResolvedComCastTarget(target, null, iid);
        }

        throw new InvalidOperationException(
            $"{_descriptor.DisplayName} adapter does not define COM surface '{target}'.");
    }

    public ResolvedComSurface? ResolveSurfaceForValue(object value)
    {
        object candidate = Unwrap(value);
        return _surfaces.FirstOrDefault(surface => surface.IsMatch(candidate));
    }

    public static bool TryResolveValue(object value, ResolvedComCastTarget target, out object? resolvedValue, out string? resolvedSurface)
    {
        object candidate = Unwrap(value);
        if (target.Surface is not null && TryResolveValue(candidate, target.Surface, out resolvedValue))
        {
            resolvedSurface = target.Surface.Name;
            return true;
        }

        if (target.InterfaceId is Guid iid && TryQueryInterface(candidate, iid, target.Surface?.ClrType, out resolvedValue))
        {
            resolvedSurface = target.Surface?.Name ?? target.DisplayName;
            return true;
        }

        resolvedValue = null;
        resolvedSurface = target.Surface?.Name ?? target.DisplayName;
        return false;
    }

    public IReadOnlyList<string> GetAvailableMembers(object value, string? currentSurface)
    {
        ResolvedComSurface? surface = ResolveCurrentSurface(value, currentSurface);
        if (surface is not null && surface.MemberNames.Count > 0)
        {
            return surface.MemberNames;
        }

        return value.GetType()
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray();
    }

    public IReadOnlyList<string> GetPossibleCasts(object value, string? currentSurface)
    {
        object candidate = Unwrap(value);
        return _surfaces
            .Where(surface => !string.Equals(surface.Name, currentSurface, StringComparison.OrdinalIgnoreCase))
            .Where(surface => TryResolveValue(candidate, surface, out _))
            .Select(surface => surface.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> FindCastsForMember(object value, string member, string? currentSurface)
    {
        object candidate = Unwrap(value);
        return _surfaces
            .Where(surface => !string.Equals(surface.Name, currentSurface, StringComparison.OrdinalIgnoreCase))
            .Where(surface => surface.ContainsMember(member))
            .Where(surface => TryResolveValue(candidate, surface, out _))
            .Select(surface => surface.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ResolvedComSurface? ResolveCurrentSurface(object value, string? currentSurface)
    {
        return TryResolveSurface(currentSurface) ?? ResolveSurfaceForValue(value);
    }

    private static object Unwrap(object value)
        => value is IReflectiveInvocationValueAdapter adapter ? adapter.GetInvocationValue() ?? value : value;

    private static bool TryResolveValue(object value, ResolvedComSurface surface, out object? resolvedValue)
    {
        if (surface.InterfaceId is Guid iid && TryQueryInterface(value, iid, surface.ClrType, out resolvedValue))
        {
            return true;
        }

        if (surface.ClrType is not null && surface.ClrType.IsInstanceOfType(value))
        {
            resolvedValue = value;
            return true;
        }

        resolvedValue = null;
        return false;
    }

    private static bool TryQueryInterface(object value, Guid iid, Type? targetType, out object? resolvedValue)
    {
        resolvedValue = null;
        if (!Marshal.IsComObject(value) && !value.GetType().IsCOMObject)
        {
            return false;
        }

        IntPtr unknown = IntPtr.Zero;
        IntPtr interfacePointer = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(value);
            if (Marshal.QueryInterface(unknown, ref iid, out interfacePointer) < 0 || interfacePointer == IntPtr.Zero)
            {
                return false;
            }

            resolvedValue = Marshal.GetObjectForIUnknown(interfacePointer);
            if (targetType is not null &&
                resolvedValue is not null &&
                !targetType.IsInstanceOfType(resolvedValue) &&
                !Marshal.IsComObject(resolvedValue) &&
                !resolvedValue.GetType().IsCOMObject)
            {
                resolvedValue = Marshal.GetTypedObjectForIUnknown(interfacePointer, targetType);
            }

            return true;
        }
        catch
        {
            resolvedValue = null;
            return false;
        }
        finally
        {
            if (interfacePointer != IntPtr.Zero)
            {
                _ = Marshal.Release(interfacePointer);
            }

            if (unknown != IntPtr.Zero)
            {
                _ = Marshal.Release(unknown);
            }
        }
    }

    private static Type? ResolveConfiguredType(string? clrTypeName)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            return null;
        }

        Type? direct = Type.GetType(clrTypeName, throwOnError: false, ignoreCase: true);
        if (direct is not null)
        {
            return direct;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? resolved = assembly.GetType(clrTypeName, throwOnError: false, ignoreCase: true);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static void LoadInteropAssemblies(IEnumerable<string> assemblyPaths)
    {
        foreach (string assemblyPath in assemblyPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(assemblyPath))
            {
                continue;
            }

            try
            {
                AssemblyName assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                        string.Equals(assembly.FullName, assemblyName.FullName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _ = Assembly.LoadFrom(assemblyPath);
            }
            catch
            {
            }
        }
    }
}

 [SupportedOSPlatform("windows")]
internal sealed class ResolvedComSurface
{
    private readonly HashSet<string> _memberNames;

    public ResolvedComSurface(ComSurfaceDefinition definition, Type? clrType)
    {
        Name = definition.Name;
        ClrTypeName = definition.ClrTypeName;
        ClrType = clrType;
        InterfaceId = Guid.TryParse(definition.Iid, out Guid parsedIid)
            ? parsedIid
            : ResolveInterfaceId(clrType);
        LookupKeys = BuildLookupKeys(definition, clrType, InterfaceId);
        MemberNames = BuildMemberNames(clrType);
        _memberNames = MemberNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public string? ClrTypeName { get; }

    public Type? ClrType { get; }

    public Guid? InterfaceId { get; }

    public IReadOnlyList<string> LookupKeys { get; }

    public IReadOnlyList<string> MemberNames { get; }

    [SupportedOSPlatform("windows")]
    public bool IsMatch(object value)
    {
        if (ClrType is not null && ClrType.IsInstanceOfType(value))
        {
            return true;
        }

        if (InterfaceId is Guid iid &&
            (Marshal.IsComObject(value) || value.GetType().IsCOMObject))
        {
            IntPtr unknown = IntPtr.Zero;
            IntPtr interfacePointer = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(value);
                return Marshal.QueryInterface(unknown, ref iid, out interfacePointer) >= 0 && interfacePointer != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (interfacePointer != IntPtr.Zero)
                {
                    _ = Marshal.Release(interfacePointer);
                }

                if (unknown != IntPtr.Zero)
                {
                    _ = Marshal.Release(unknown);
                }
            }
        }

        return false;
    }

    public bool ContainsMember(string member)
        => _memberNames.Count == 0 || _memberNames.Contains(member);

    [SupportedOSPlatform("windows")]
    private static string[] BuildLookupKeys(ComSurfaceDefinition definition, Type? clrType, Guid? iid)
    {
        List<string> values = [definition.Name];
        if (!string.IsNullOrWhiteSpace(definition.ClrTypeName))
        {
            values.Add(definition.ClrTypeName);
        }

        if (clrType?.FullName is not null)
        {
            values.Add(clrType.FullName);
        }

        values.AddRange(definition.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)));
        if (iid is Guid interfaceId)
        {
            values.Add(interfaceId.ToString("D"));
            values.Add(interfaceId.ToString("B"));
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildMemberNames(Type? clrType)
    {
        if (clrType is null)
        {
            return [];
        }

        return clrType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Guid? ResolveInterfaceId(Type? clrType)
    {
        if (clrType is null)
        {
            return null;
        }

        Guid guid = clrType.GUID;
        return guid == Guid.Empty ? null : guid;
    }
}

internal sealed record ResolvedComCastTarget(string DisplayName, ResolvedComSurface? Surface, Guid? InterfaceId);

[SupportedOSPlatform("windows")]
internal sealed class ComRuntimeValue : IReflectiveInvocationProxy, IReflectiveInvocationValueAdapter
{
    private readonly ComInvokeRuntime _runtime;
    private readonly object _value;

    public ComRuntimeValue(ComInvokeRuntime runtime, object value, ComHandleInfo handleInfo, string? currentSurface)
    {
        _runtime = runtime;
        _value = value;
        HandleInfo = handleInfo;
        CurrentSurface = currentSurface;
    }

    public ComHandleInfo HandleInfo { get; }

    public string? CurrentSurface { get; }

    public object? GetInvocationValue() => _value;

    public object? GetMemberValue(string member)
    {
        if (TryGetIntrospectionMember(member, out object? value))
        {
            return value;
        }

        EnsureMemberAccessible(member, "get");
        try
        {
            if (TryGetSurfaceClrDispatchTarget(out object? surfaceTarget, out Type? surfaceClrType) &&
                surfaceTarget is not null &&
                surfaceClrType is not null &&
                ReflectiveInvokeAccessor.TryGetClrMemberValue(surfaceTarget, surfaceClrType, member, out object? surfacedValue))
            {
                return _runtime.AdaptValue(surfacedValue);
            }

            return _runtime.AdaptValue(ReflectiveInvokeAccessor.GetMemberValue(_value, member));
        }
        catch (Exception exception)
        {
            throw _runtime.CreateInvokeDiagnosticException(_value, CurrentSurface, member, exception);
        }
    }

    public void SetMemberValue(string member, object? value)
    {
        EnsureMemberAccessible(member, "set");
        try
        {
            if (TryGetSurfaceClrDispatchTarget(out object? surfaceTarget, out Type? surfaceClrType) &&
                surfaceTarget is not null &&
                surfaceClrType is not null &&
                ReflectiveInvokeAccessor.TrySetClrMemberValue(surfaceTarget, surfaceClrType, member, value))
            {
                return;
            }

            ReflectiveInvokeAccessor.SetMemberValue(_value, member, value);
        }
        catch (Exception exception)
        {
            throw _runtime.CreateInvokeDiagnosticException(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome CallMember(string member, ResolvedInvokeArgument[] arguments)
    {
        EnsureMemberAccessible(member, "call");
        try
        {
            if (TryGetSurfaceClrDispatchTarget(out object? surfaceTarget, out Type? surfaceClrType) &&
                surfaceTarget is not null &&
                surfaceClrType is not null &&
                ReflectiveInvokeAccessor.TryInvokeClrMethod(surfaceTarget, surfaceClrType, member, arguments, out object? surfacedValue, out Dictionary<string, object?> surfacedCaptures))
            {
                return new InvokeCallOutcome(_runtime.AdaptValue(surfacedValue), surfacedCaptures);
            }

            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.CallMember(_value, member, arguments);
            return new InvokeCallOutcome(_runtime.AdaptValue(outcome.Value), outcome.CapturedArguments);
        }
        catch (Exception exception)
        {
            throw _runtime.CreateInvokeDiagnosticException(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome IndexValue(string member, ResolvedInvokeArgument[] arguments)
    {
        try
        {
            if (TryGetSurfaceClrDispatchTarget(out object? surfaceTarget, out Type? surfaceClrType) &&
                surfaceTarget is not null &&
                surfaceClrType is not null &&
                ReflectiveInvokeAccessor.TryGetClrIndexerValue(surfaceTarget, surfaceClrType, arguments, out object? surfacedValue, out Dictionary<string, object?> surfacedCaptures))
            {
                return new InvokeCallOutcome(_runtime.AdaptValue(surfacedValue), surfacedCaptures);
            }

            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.IndexValue(_value, member, arguments);
            return new InvokeCallOutcome(_runtime.AdaptValue(outcome.Value), outcome.CapturedArguments);
        }
        catch (Exception exception)
        {
            throw _runtime.CreateInvokeDiagnosticException(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome CreateInstance(ResolvedInvokeArgument[] arguments)
        => throw new InvalidOperationException("COM object handles do not support invoke.new.");

    private bool TryGetSurfaceClrDispatchTarget(out object? surfaceTarget, out Type? surfaceClrType)
    {
        ResolvedComSurface? surface = _runtime.ResolveSurface(CurrentSurface, _value);
        if (surface?.ClrType is null)
        {
            surfaceTarget = null;
            surfaceClrType = null;
            return false;
        }

        if (!Marshal.IsComObject(_value) && !_value.GetType().IsCOMObject)
        {
            if (!surface.ClrType.IsInstanceOfType(_value))
            {
                surfaceTarget = null;
                surfaceClrType = null;
                return false;
            }

            surfaceTarget = _value;
            surfaceClrType = surface.ClrType;
            return true;
        }

        ResolvedComSurface? naturalSurface = _runtime.ResolveSurface(null, _value);
        if (naturalSurface is not null &&
            string.Equals(naturalSurface.Name, surface.Name, StringComparison.OrdinalIgnoreCase))
        {
            surfaceTarget = null;
            surfaceClrType = null;
            return false;
        }

        if (surface.InterfaceId is not Guid iid)
        {
            surfaceTarget = null;
            surfaceClrType = null;
            return false;
        }

        IntPtr unknown = IntPtr.Zero;
        IntPtr interfacePointer = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(_value);
            if (Marshal.QueryInterface(unknown, ref iid, out interfacePointer) < 0 || interfacePointer == IntPtr.Zero)
            {
                surfaceTarget = null;
                surfaceClrType = null;
                return false;
            }

            object typedValue = Marshal.GetTypedObjectForIUnknown(interfacePointer, surface.ClrType);
            if (!surface.ClrType.IsInstanceOfType(typedValue))
            {
                surfaceTarget = null;
                surfaceClrType = null;
                return false;
            }

            surfaceTarget = typedValue;
            surfaceClrType = surface.ClrType;
            return true;
        }
        catch
        {
            surfaceTarget = null;
            surfaceClrType = null;
            return false;
        }
        finally
        {
            if (interfacePointer != IntPtr.Zero)
            {
                _ = Marshal.Release(interfacePointer);
            }

            if (unknown != IntPtr.Zero)
            {
                _ = Marshal.Release(unknown);
            }
        }
    }

    private bool TryGetIntrospectionMember(string member, out object? value)
    {
        if (string.Equals(member, "handleId", StringComparison.OrdinalIgnoreCase))
        {
            value = HandleInfo.HandleId;
            return true;
        }

        if (string.Equals(member, "adapter", StringComparison.OrdinalIgnoreCase))
        {
            value = HandleInfo.AdapterName;
            return true;
        }

        if (string.Equals(member, "runtimeType", StringComparison.OrdinalIgnoreCase))
        {
            value = HandleInfo.RuntimeType;
            return true;
        }

        if (string.Equals(member, "surface", StringComparison.OrdinalIgnoreCase))
        {
            value = CurrentSurface;
            return true;
        }

        if (string.Equals(member, "resolvedInterfaces", StringComparison.OrdinalIgnoreCase))
        {
            value = HandleInfo.ResolvedInterfaces
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }

        if (string.Equals(member, "memberNames", StringComparison.OrdinalIgnoreCase))
        {
            value = _runtime.GetAvailableMembers(_value, CurrentSurface).ToArray();
            return true;
        }

        if (string.Equals(member, "possibleCasts", StringComparison.OrdinalIgnoreCase))
        {
            value = _runtime.GetPossibleCasts(_value, CurrentSurface).ToArray();
            return true;
        }

        value = null;
        return false;
    }

    private void EnsureMemberAccessible(string member, string operation)
    {
        ResolvedComSurface? surface = _runtime.ResolveSurface(CurrentSurface, _value);
        if (surface is null || surface.ContainsMember(member))
        {
            return;
        }

        throw _runtime.CreateInvokeDiagnosticException(
            _value,
            surface.Name,
            member,
            new MissingMemberException($"Member '{member}' is not available on COM surface '{surface.Name}' during {operation}."));
    }
}

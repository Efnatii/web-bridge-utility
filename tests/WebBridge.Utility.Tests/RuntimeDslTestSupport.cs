using System.Reflection;
using System.Text.Json.Nodes;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Adapters.SystemAccess;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Tests;

internal sealed class TestInvokeSurface(TestComLikeRuntime runtime)
    : ReflectiveInvokeSurfaceBase<TestComLikeRuntime>(runtime);

internal sealed class TestComInvokeSurfaceFactory(Func<ComInvokeDescriptor, object> applicationFactory) : IComInvokeSurfaceFactory
{
    public IAdapterInvokeSurface Create(ComInvokeDescriptor descriptor)
        => new TestInvokeSurface(new TestComLikeRuntime(descriptor.Clone(), applicationFactory(descriptor)));
}

internal sealed class TestComLikeRuntime : IReflectiveInvokeRuntime, IHandleArgumentResolver, IReflectiveInvokeCastRuntime
{
    private readonly ComInvokeDescriptor _descriptor;
    private readonly object _applicationRoot;
    private readonly Dictionary<string, TestHandleInfo> _handles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, string> _handleIdsByObject = new(TestReferenceEqualityComparer.Instance);
    private readonly List<TestSurfaceInfo> _surfaces;
    private readonly Dictionary<string, TestSurfaceInfo> _surfaceLookup = new(StringComparer.OrdinalIgnoreCase);

    public TestComLikeRuntime(ComInvokeDescriptor descriptor, object applicationRoot)
    {
        _descriptor = descriptor;
        _applicationRoot = applicationRoot;
        _surfaces = descriptor.Surfaces
            .Where(surface => !string.IsNullOrWhiteSpace(surface.Name) && !string.IsNullOrWhiteSpace(surface.ClrTypeName))
            .Select(surface => new TestSurfaceInfo(
                surface.Name,
                ResolveType(surface.ClrTypeName!),
                surface.Iid,
                surface.Aliases.ToArray()))
            .ToList();
        foreach (TestSurfaceInfo surface in _surfaces)
        {
            foreach (string lookupKey in surface.LookupKeys)
            {
                _surfaceLookup[lookupKey] = surface;
            }
        }
    }

    public string AdapterName => _descriptor.AdapterName;

    public object? ResolveRoot(string rootName, JsonObject arguments)
    {
        return rootName.ToLowerInvariant() switch
        {
            "application" => _applicationRoot,
            "handle" => ResolveHandleArgument(RequireHandleId(arguments)),
            _ => throw new InvalidOperationException($"Test adapter root '{rootName}' is not supported."),
        };
    }

    public object? AdaptValue(object? value)
    {
        if (value is null || IsScalar(value))
        {
            return value;
        }

        if (value is TestRuntimeValue runtimeValue)
        {
            return runtimeValue;
        }

        TestSurfaceInfo? surface = ResolveSurfaceForValue(value);
        TestHandleInfo handleInfo = EnsureHandle(value, surface?.Name);
        object currentValue = !string.IsNullOrWhiteSpace(handleInfo.CurrentSurface) &&
            handleInfo.TryGetSurfaceValue(handleInfo.CurrentSurface!, out object? resolved)
            ? resolved ?? value
            : value;
        return new TestRuntimeValue(this, handleInfo, currentValue, surface?.Name ?? handleInfo.CurrentSurface);
    }

    public JsonNode? ConvertResult(object? value, InvokeDefinition definition, JsonObject arguments)
    {
        if (value is null)
        {
            return null;
        }

        if (value is TestRuntimeValue runtimeValue)
        {
            return new JsonObject
            {
                ["handleId"] = runtimeValue.HandleInfo.HandleId,
                ["adapter"] = _descriptor.AdapterName,
                ["runtimeType"] = runtimeValue.HandleInfo.RuntimeType,
                ["surface"] = runtimeValue.CurrentSurface,
                ["resolvedInterfaces"] = new JsonArray(
                    runtimeValue.HandleInfo.ResolvedInterfaces
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray()),
                ["memberNames"] = new JsonArray(
                    GetAvailableMembers(runtimeValue.GetInvocationValue() ?? runtimeValue, runtimeValue.CurrentSurface)
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray()),
                ["possibleCasts"] = new JsonArray(
                    GetPossibleCasts(runtimeValue.GetInvocationValue() ?? runtimeValue, runtimeValue.CurrentSurface)
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray()),
            };
        }

        return InvokeJsonNodeConverter.Convert(value);
    }

    public ReportVerbosity GetDefaultReportVerbosity(JsonObject arguments) => ReportVerbosity.Full;

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(action());
    }

    public CommandExecutionResult MapInvokeException(string commandId, Exception exception, InvokeDefinition definition)
        => CommandExecutionResult.Fail("test_invoke_failed", exception.Message);

    public object ResolveHandleArgument(string handleId)
    {
        if (!_handles.TryGetValue(handleId, out TestHandleInfo? handle))
        {
            throw new InvalidOperationException($"Handle '{handleId}' was not found.");
        }

        return handle.ResolveCurrentValue();
    }

    public object? CastValue(object? value, string target, InvokeCastSemantics semantics)
    {
        if (value is null)
        {
            return semantics == InvokeCastSemantics.TryCast
                ? null
                : throw new InvalidOperationException($"Cannot cast null to '{target}'.");
        }

        object candidate = value is IReflectiveInvocationValueAdapter invocationValueAdapter
            ? invocationValueAdapter.GetInvocationValue() ?? value
            : value;
        TestRuntimeValue? runtimeValue = value as TestRuntimeValue;
        TestSurfaceInfo surface = ResolveTarget(target);
        if (surface.Type.IsInstanceOfType(candidate))
        {
            TestHandleInfo handleInfo = runtimeValue?.HandleInfo ?? EnsureHandle(candidate, runtimeValue?.CurrentSurface);
            handleInfo.RememberSurfaceValue(surface.Name, candidate);
            return new TestRuntimeValue(this, handleInfo, candidate, surface.Name);
        }

        if (semantics == InvokeCastSemantics.TryCast)
        {
            return null;
        }

        string possibleCasts = string.Join(", ", GetPossibleCasts(candidate, runtimeValue?.CurrentSurface));
        string suffix = string.IsNullOrWhiteSpace(possibleCasts) ? string.Empty : $" Possible casts: {possibleCasts}.";
        throw new InvalidOperationException(
            $"Cannot cast runtime type '{candidate.GetType().FullName}' from '{runtimeValue?.CurrentSurface ?? "<unknown>"}' to '{target}'.{suffix}");
    }

    internal IReadOnlyList<string> GetAvailableMembers(object value, string? currentSurface)
    {
        TestSurfaceInfo? surface = ResolveSurface(currentSurface, value);
        return surface?.Members ?? value.GetType()
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<string> GetPossibleCasts(object value, string? currentSurface)
    {
        return _surfaces
            .Where(surface => !string.Equals(surface.Name, currentSurface, StringComparison.OrdinalIgnoreCase))
            .Where(surface => surface.Type.IsInstanceOfType(value))
            .Select(surface => surface.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<string> FindMemberCasts(object value, string member, string? currentSurface)
    {
        return _surfaces
            .Where(surface => !string.Equals(surface.Name, currentSurface, StringComparison.OrdinalIgnoreCase))
            .Where(surface => surface.Type.IsInstanceOfType(value))
            .Where(surface => surface.ContainsMember(member))
            .Select(surface => surface.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal TestSurfaceInfo? ResolveSurface(string? currentSurface, object value)
        => (currentSurface is not null && _surfaceLookup.TryGetValue(currentSurface, out TestSurfaceInfo? surface) ? surface : null)
        ?? ResolveSurfaceForValue(value);

    internal Exception CreateMemberDiagnostic(object value, string? currentSurface, string member, Exception inner)
    {
        string availableMembers = string.Join(", ", GetAvailableMembers(value, currentSurface).Take(16));
        string castHints = string.Join(", ", FindMemberCasts(value, member, currentSurface));
        string castsMessage = string.IsNullOrWhiteSpace(castHints) ? string.Empty : $" Try cast({castHints.Split(',')[0].Trim()}).";
        return new InvalidOperationException(
            $"Member '{member}' was not found on runtime type '{value.GetType().FullName}' for surface '{currentSurface ?? "<unknown>"}'. Available members: {availableMembers}.{castsMessage} {inner.Message}".Trim(),
            inner);
    }

    private TestHandleInfo EnsureHandle(object value, string? currentSurface)
    {
        if (_handleIdsByObject.TryGetValue(value, out string? existingId) &&
            _handles.TryGetValue(existingId, out TestHandleInfo? existing))
        {
            existing.Update(value, currentSurface);
            return existing;
        }

        string handleId = Guid.NewGuid().ToString("N");
        TestHandleInfo created = new(handleId, value, currentSurface);
        _handles[handleId] = created;
        _handleIdsByObject[value] = handleId;
        return created;
    }

    private TestSurfaceInfo ResolveTarget(string target)
    {
        if (_surfaceLookup.TryGetValue(target, out TestSurfaceInfo? surface))
        {
            return surface;
        }

        throw new InvalidOperationException($"Test adapter does not define surface '{target}'.");
    }

    private TestSurfaceInfo? ResolveSurfaceForValue(object value)
        => _surfaces.FirstOrDefault(surface => surface.Type.IsInstanceOfType(value));

    private static string RequireHandleId(JsonObject arguments)
        => arguments["handleId"]?.GetValue<string>()
           ?? throw new InvalidOperationException("Property 'handleId' is required.");

    private static bool IsScalar(object value)
    {
        return value is string or bool or int or long or double or decimal or float or DateTime or DateTimeOffset or Guid ||
            value.GetType().IsPrimitive ||
            value is JsonNode;
    }

    private static Type ResolveType(string clrTypeName)
    {
        Type? direct = Type.GetType(clrTypeName, throwOnError: false);
        if (direct is not null)
        {
            return direct;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? resolved = assembly.GetType(clrTypeName, throwOnError: false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new InvalidOperationException($"Type '{clrTypeName}' was not found for test surface.");
    }
}

internal sealed class TestRuntimeValue : IReflectiveInvocationProxy, IReflectiveInvocationValueAdapter
{
    private readonly TestComLikeRuntime _runtime;
    private readonly object _value;

    public TestRuntimeValue(TestComLikeRuntime runtime, TestHandleInfo handleInfo, object value, string? currentSurface)
    {
        _runtime = runtime;
        HandleInfo = handleInfo;
        _value = value;
        CurrentSurface = currentSurface;
    }

    public TestHandleInfo HandleInfo { get; }

    public string? CurrentSurface { get; }

    public object? GetInvocationValue() => _value;

    public object? GetMemberValue(string member)
    {
        if (TryGetMetadataMember(member, out object? value))
        {
            return value;
        }

        EnsureMemberAccessible(member);
        try
        {
            return _runtime.AdaptValue(ReflectiveInvokeAccessor.GetMemberValue(_value, member));
        }
        catch (Exception exception)
        {
            throw _runtime.CreateMemberDiagnostic(_value, CurrentSurface, member, exception);
        }
    }

    public void SetMemberValue(string member, object? value)
    {
        EnsureMemberAccessible(member);
        try
        {
            ReflectiveInvokeAccessor.SetMemberValue(_value, member, value);
        }
        catch (Exception exception)
        {
            throw _runtime.CreateMemberDiagnostic(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome CallMember(string member, ResolvedInvokeArgument[] arguments)
    {
        EnsureMemberAccessible(member);
        try
        {
            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.CallMember(_value, member, arguments);
            return new InvokeCallOutcome(_runtime.AdaptValue(outcome.Value), outcome.CapturedArguments);
        }
        catch (Exception exception)
        {
            throw _runtime.CreateMemberDiagnostic(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome IndexValue(string member, ResolvedInvokeArgument[] arguments)
    {
        try
        {
            object?[] indexArguments = arguments.Select(argument => argument.Value).ToArray();
            PropertyInfo indexer = _value.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Single(property => property.Name == "Item" && property.GetIndexParameters().Length == indexArguments.Length);
            object? indexedValue = indexer.GetValue(_value, indexArguments);
            return new InvokeCallOutcome(
                _runtime.AdaptValue(indexedValue),
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            throw _runtime.CreateMemberDiagnostic(_value, CurrentSurface, member, exception);
        }
    }

    public InvokeCallOutcome CreateInstance(ResolvedInvokeArgument[] arguments)
        => throw new InvalidOperationException("Test COM handles do not support invoke.new.");

    private bool TryGetMetadataMember(string member, out object? value)
    {
        if (string.Equals(member, "handleId", StringComparison.OrdinalIgnoreCase))
        {
            value = HandleInfo.HandleId;
            return true;
        }

        if (string.Equals(member, "adapter", StringComparison.OrdinalIgnoreCase))
        {
            value = _runtime.AdapterName;
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
            value = HandleInfo.ResolvedInterfaces.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
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

    private void EnsureMemberAccessible(string member)
    {
        TestSurfaceInfo? surface = _runtime.ResolveSurface(CurrentSurface, _value);
        if (surface is null || surface.ContainsMember(member))
        {
            return;
        }

        throw _runtime.CreateMemberDiagnostic(
            _value,
            surface.Name,
            member,
            new MissingMemberException($"Member '{member}' is not available on surface '{surface.Name}'."));
    }
}

internal sealed class TestHandleInfo
{
    private readonly Dictionary<string, object> _surfaceValues = new(StringComparer.OrdinalIgnoreCase);

    public TestHandleInfo(string handleId, object rawValue, string? currentSurface)
    {
        HandleId = handleId;
        RuntimeType = rawValue.GetType().FullName ?? rawValue.GetType().Name;
        RawValue = rawValue;
        CurrentSurface = currentSurface;
        if (!string.IsNullOrWhiteSpace(currentSurface))
        {
            ResolvedInterfaces.Add(currentSurface);
            _surfaceValues[currentSurface] = rawValue;
        }
    }

    public string HandleId { get; }

    public string RuntimeType { get; private set; }

    public object RawValue { get; private set; }

    public string? CurrentSurface { get; private set; }

    public HashSet<string> ResolvedInterfaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Update(object rawValue, string? currentSurface)
    {
        RawValue = rawValue;
        RuntimeType = rawValue.GetType().FullName ?? rawValue.GetType().Name;
        if (!string.IsNullOrWhiteSpace(currentSurface))
        {
            RememberSurfaceValue(currentSurface, rawValue);
        }
    }

    public void RememberSurfaceValue(string surfaceName, object value)
    {
        CurrentSurface = surfaceName;
        RawValue = value;
        RuntimeType = value.GetType().FullName ?? value.GetType().Name;
        ResolvedInterfaces.Add(surfaceName);
        _surfaceValues[surfaceName] = value;
    }

    public bool TryGetSurfaceValue(string surfaceName, out object? value)
    {
        if (_surfaceValues.TryGetValue(surfaceName, out object? resolved))
        {
            value = resolved;
            return true;
        }

        value = null;
        return false;
    }

    public object ResolveCurrentValue()
    {
        if (!string.IsNullOrWhiteSpace(CurrentSurface) &&
            _surfaceValues.TryGetValue(CurrentSurface, out object? resolved))
        {
            return resolved;
        }

        return RawValue;
    }
}

internal sealed class TestSurfaceInfo
{
    private readonly HashSet<string> _members;

    public TestSurfaceInfo(string name, Type type, string? iid, string[] aliases)
    {
        Name = name;
        Type = type;
        Iid = iid;
        LookupKeys = aliases
            .Concat([name, type.FullName ?? type.Name])
            .Concat(string.IsNullOrWhiteSpace(iid) ? [] : [iid!])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _members = Members.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public Type Type { get; }

    public string? Iid { get; }

    public string[] LookupKeys { get; }

    public string[] Members { get; }

    public bool ContainsMember(string member) => _members.Contains(member);
}

internal sealed class TestDispatcherHarness
{
    public required UtilitySettings Settings { get; init; }

    public required RuntimeConfigurationManager ConfigurationManager { get; init; }

    public required CommandDispatcher Dispatcher { get; init; }
}

internal static class TestDsl
{
    public static InvokeStepDefinition Step(string operation, string member, params InvokeArgumentDefinition[] args)
        => new() { Operation = operation, Member = member, Args = args.ToList() };

    public static InvokeStepDefinition SetStep(string member, string valueArgument)
        => new() { Operation = "set", Member = member, ValueArgument = valueArgument };

    public static InvokeStepDefinition IndexStep(params InvokeArgumentDefinition[] args)
        => new() { Operation = "index", Member = string.Empty, Args = args.ToList() };

    public static InvokeArgumentDefinition Arg(string argumentName, string? converter = null)
        => new() { FromArgument = argumentName, Converter = converter };

    public static InvokeArgumentDefinition Literal(object? value, string? converter = null)
        => new()
        {
            Literal = value switch
            {
                null => null,
                string stringValue => JsonValue.Create(stringValue),
                bool boolValue => JsonValue.Create(boolValue),
                int intValue => JsonValue.Create(intValue),
                long longValue => JsonValue.Create(longValue),
                _ => JsonValue.Create(value.ToString()),
            },
            Converter = converter,
        };

    public static CommandDefinition Command(string commandId, string adapter, string root, params InvokeStepDefinition[] steps)
        => new()
        {
            CommandId = commandId,
            Adapter = adapter,
            Invoke = new InvokeDefinition
            {
                Root = root,
                Chain = steps.ToList(),
            },
        };

    public static ProfileDefinition Profile(string profileId, params CommandDefinition[] commands)
        => new()
        {
            ProfileId = profileId,
            ConfigSchemaVersion = 1,
            Commands = commands.ToDictionary(command => command.CommandId, StringComparer.OrdinalIgnoreCase),
        };

    public static UtilitySettings Settings(ProfileDefinition profile, params ComInvokeDescriptor[] adapters)
    {
        UtilitySettings settings = UtilityCli.NormalizeEffectiveSettings(new UtilitySettings(), configPath: null);
        settings.Profiles.Clear();
        settings.Profiles.Add(profile);
        settings.ComAdapters.Clear();
        settings.ComAdapters.AddRange(adapters.Select(adapter => adapter.Clone()));
        return settings;
    }

    public static TestDispatcherHarness CreateDispatcherHarness(
        UtilitySettings settings,
        Func<ComInvokeDescriptor, object> applicationFactory)
    {
        FakeManifestService manifestService = new();
        FakeClock clock = new(DateTimeOffset.Parse("2026-03-10T00:00:00+00:00", CultureInfo.InvariantCulture));
        RuntimeConfigurationManager configurationManager = new(
            settings,
            manifestService,
            clock,
            NullLogger<RuntimeConfigurationManager>.Instance);
        SystemRuntime systemRuntime = new(settings);
        SystemInvokeSurface systemSurface = new(systemRuntime);
        AdapterInvokeSurfaceRegistry registry = new(
            configurationManager,
            systemSurface,
            new TestComInvokeSurfaceFactory(applicationFactory));
        CommandPlanCompiler compiler = new(registry, configurationManager);
        CommandDispatcher dispatcher = new(
            new ProfileStore(settings),
            compiler,
            NullLogger<CommandDispatcher>.Instance);
        return new TestDispatcherHarness
        {
            Settings = settings,
            ConfigurationManager = configurationManager,
            Dispatcher = dispatcher,
        };
    }
}

internal sealed class TestAdapterRoot(string adapterStamp)
{
    public string AdapterStamp { get; } = adapterStamp;
}

internal sealed class FakeApplicationRoot(IFakeDocument2D document)
{
    public IFakeDocument2D ActiveDocument { get; } = document;
}

internal interface IFakeDocument2D
{
    IFakeViewsAndLayersManager ViewsAndLayersManager { get; }

    string Save(string path);
}

internal interface IFakeViewsAndLayersManager
{
    IFakeViews Views { get; }
}

internal interface IFakeViews
{
    IFakeView ActiveView { get; }
}

internal interface IFakeView
{
    string Name { get; }
}

internal interface IFakeSymbols2DContainer
{
    IFakeDrawingTables DrawingTables { get; }
}

internal interface IFakeDrawingTables
{
    IFakeDrawingTable Add(int rows, int cols);

    IFakeDrawingTable Load(string path);
}

internal interface IFakeDrawingTable
{
}

internal interface IFakeTable
{
    IFakeTableCell this[int row, int col] { get; }
}

internal interface IFakeTableCell
{
    IFakeText Text { get; }
}

internal interface IFakeText
{
    string Str { get; set; }
}

internal interface IFakeMissingSurface
{
}

internal sealed class FakeDocument2D(IFakeViewsAndLayersManager manager) : IFakeDocument2D
{
    public IFakeViewsAndLayersManager ViewsAndLayersManager { get; } = manager;

    public string? LastSavedPath { get; private set; }

    public string Save(string path)
    {
        LastSavedPath = path;
        return path;
    }
}

internal sealed class FakeViewsAndLayersManager(IFakeViews views) : IFakeViewsAndLayersManager
{
    public IFakeViews Views { get; } = views;
}

internal sealed class FakeViews(IFakeView activeView) : IFakeViews
{
    public IFakeView ActiveView { get; } = activeView;
}

internal sealed class FakeView(string name) : IFakeView, IFakeSymbols2DContainer
{
    public string Name { get; } = name;

    public IFakeDrawingTables DrawingTables { get; } = new FakeDrawingTables();
}

internal sealed class FakeDrawingTables : IFakeDrawingTables
{
    public List<string> LoadedPaths { get; } = new();

    public FakeDrawingTable? LastCreatedTable { get; private set; }

    public IFakeDrawingTable Load(string path)
    {
        LoadedPaths.Add(path);
        LastCreatedTable = new FakeDrawingTable(1, 1);
        return LastCreatedTable;
    }

    public IFakeDrawingTable Add(int rows, int cols)
    {
        LastCreatedTable = new FakeDrawingTable(rows, cols);
        return LastCreatedTable;
    }
}

internal sealed class FakeDrawingTable : IFakeDrawingTable, IFakeTable
{
    private readonly Dictionary<(int Row, int Col), FakeTableCell> _cells = new();

    public FakeDrawingTable(int rows, int cols)
    {
        Rows = rows;
        Cols = cols;
    }

    public int Rows { get; }

    public int Cols { get; }

    public IFakeTableCell this[int row, int col]
    {
        get
        {
            if (!_cells.TryGetValue((row, col), out FakeTableCell? cell))
            {
                cell = new FakeTableCell();
                _cells[(row, col)] = cell;
            }

            return cell;
        }
    }
}

internal sealed class FakeTableCell : IFakeTableCell
{
    public IFakeText Text { get; } = new FakeText();
}

internal sealed class FakeText : IFakeText
{
    public string Str { get; set; } = string.Empty;
}

internal sealed class TestReferenceEqualityComparer : IEqualityComparer<object>
{
    public static TestReferenceEqualityComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

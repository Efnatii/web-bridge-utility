using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Adapters.SystemAccess;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Tests;

internal static class ComCoverageDsl
{
    public static InvokeArgumentDefinition Arg(
        string argumentName,
        string? converter = null,
        string? name = null,
        bool byRef = false,
        string? captureAs = null,
        string? fromStored = null)
        => new()
        {
            FromArgument = fromStored is null ? argumentName : null,
            FromStored = fromStored,
            Converter = converter,
            Name = name,
            ByRef = byRef,
            CaptureAs = captureAs,
        };

    public static InvokeArgumentDefinition Literal(
        object? value,
        string? converter = null,
        string? name = null,
        bool byRef = false,
        string? captureAs = null)
        => new()
        {
            Literal = value switch
            {
                null => null,
                string stringValue => JsonValue.Create(stringValue),
                bool boolValue => JsonValue.Create(boolValue),
                int intValue => JsonValue.Create(intValue),
                long longValue => JsonValue.Create(longValue),
                double doubleValue => JsonValue.Create(doubleValue),
                decimal decimalValue => JsonValue.Create(decimalValue),
                JsonNode jsonNode => jsonNode.DeepClone(),
                _ => JsonValue.Create(value.ToString()),
            },
            Converter = converter,
            Name = name,
            ByRef = byRef,
            CaptureAs = captureAs,
        };

    public static CommandDefinition Command(
        string commandId,
        string adapter,
        string root,
        IEnumerable<InvokeStepDefinition> steps,
        string? returnPath = null,
        JsonObject? defaultArguments = null,
        IReadOnlyDictionary<string, string>? enumMap = null,
        IReadOnlyDictionary<string, string>? converters = null)
        => new()
        {
            CommandId = commandId,
            Adapter = adapter,
            DefaultArguments = defaultArguments?.DeepClone()?.AsObject(),
            Invoke = new InvokeDefinition
            {
                Root = root,
                Chain = steps.Select(step => step.Clone()).ToList(),
                ReturnPath = returnPath,
                EnumMap = enumMap is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(enumMap, StringComparer.OrdinalIgnoreCase),
                Converters = converters is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(converters, StringComparer.OrdinalIgnoreCase),
            },
        };

    public static InvokeStepDefinition Step(string operation, string member, params InvokeArgumentDefinition[] args)
        => new() { Operation = operation, Member = member, Args = args.ToList() };

    public static InvokeStepDefinition SetStep(string member, string valueArgument)
        => new() { Operation = "set", Member = member, ValueArgument = valueArgument };

    public static InvokeStepDefinition IndexStep(string member = "", params InvokeArgumentDefinition[] args)
        => new() { Operation = "index", Member = member, Args = args.ToList() };
}

[SupportedOSPlatform("windows")]
internal sealed class DeterministicComInvokeSurfaceFactory(DeterministicComApplicationProvider provider) : IComInvokeSurfaceFactory
{
    public IAdapterInvokeSurface Create(ComInvokeDescriptor descriptor)
    {
        return new ComInvokeSurface(new ComInvokeRuntime(
            descriptor.Clone(),
            new ComInvokeRuntimeHooks
            {
                AcquireApplication = provider.Acquire,
                ReleaseApplication = provider.Release,
            }));
    }
}

internal sealed class DeterministicComApplicationProvider
{
    private readonly object _sync = new();
    private readonly HashSet<string> _registeredProgIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeterministicComApplication> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _acquireCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _createCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, int> _releaseCounts = new(TestReferenceEqualityComparer.Instance);
    private readonly Func<string, int, DeterministicComApplication> _factory;

    public DeterministicComApplicationProvider(Func<string, int, DeterministicComApplication>? factory = null)
    {
        _factory = factory ?? ((progId, instanceId) => new DeterministicComApplication(progId, instanceId));
        RegisterProgId(ComCoverageFixture.DefaultProgId);
    }

    public void RegisterProgId(string progId)
    {
        lock (_sync)
        {
            _registeredProgIds.Add(progId);
        }
    }

    public DeterministicComApplication EnsureRunning(string progId = ComCoverageFixture.DefaultProgId)
    {
        lock (_sync)
        {
            _registeredProgIds.Add(progId);
            if (_running.TryGetValue(progId, out DeterministicComApplication? existing))
            {
                return existing;
            }

            DeterministicComApplication created = CreateApplication(progId);
            _running[progId] = created;
            return created;
        }
    }

    public DeterministicComApplication ReplaceRunning(string progId = ComCoverageFixture.DefaultProgId, string? documentTitle = null)
    {
        lock (_sync)
        {
            _registeredProgIds.Add(progId);
            DeterministicComApplication created = CreateApplication(progId);
            if (!string.IsNullOrWhiteSpace(documentTitle))
            {
                created.ReplaceDocument(documentTitle!);
            }

            _running[progId] = created;
            return created;
        }
    }

    public bool RemoveRunning(string progId)
    {
        lock (_sync)
        {
            return _running.Remove(progId);
        }
    }

    public int GetAcquireCount(string progId)
    {
        lock (_sync)
        {
            return _acquireCounts.TryGetValue(progId, out int count) ? count : 0;
        }
    }

    public int GetReleaseCount(object instance)
    {
        lock (_sync)
        {
            return _releaseCounts.TryGetValue(instance, out int count) ? count : 0;
        }
    }

    public ComApplicationBinding Acquire(
        IEnumerable<string> progIds,
        bool attachOnly,
        bool createIfMissing,
        Action<object>? initializeExisting,
        Action<object>? initializeCreated)
    {
        lock (_sync)
        {
            bool discoveredRegisteredProgId = false;
            foreach (string progId in progIds)
            {
                if (_running.TryGetValue(progId, out DeterministicComApplication? existing))
                {
                    _acquireCounts[progId] = GetAcquireCount(progId) + 1;
                    initializeExisting?.Invoke(existing);
                    return new ComApplicationBinding(existing, progId, ReusedExistingInstance: true);
                }

                if (!_registeredProgIds.Contains(progId))
                {
                    continue;
                }

                discoveredRegisteredProgId = true;
                if (attachOnly || !createIfMissing)
                {
                    continue;
                }

                DeterministicComApplication created = CreateApplication(progId);
                _running[progId] = created;
                _acquireCounts[progId] = GetAcquireCount(progId) + 1;
                initializeCreated?.Invoke(created);
                return new ComApplicationBinding(created, progId, ReusedExistingInstance: false);
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

    public void Release(object? instance)
    {
        if (instance is null)
        {
            return;
        }

        lock (_sync)
        {
            _releaseCounts[instance] = GetReleaseCount(instance) + 1;
            if (instance is DeterministicComApplication application)
            {
                application.MarkReleased();
            }
        }
    }

    private DeterministicComApplication CreateApplication(string progId)
    {
        int next = _createCounts.TryGetValue(progId, out int count) ? count + 1 : 1;
        _createCounts[progId] = next;
        return _factory(progId, next);
    }
}

internal static class ComCoverageFixture
{
    public const string DefaultProgId = "KWB.Deterministic.Application";
    public const string ApplicationSurfaceIid = "{A0000000-0000-0000-0000-000000000001}";
    public const string DocumentSurfaceIid = "{A0000000-0000-0000-0000-000000000002}";
    public const string ViewSurfaceIid = "{A0000000-0000-0000-0000-000000000003}";
    public const string SymbolsSurfaceIid = "{A0000000-0000-0000-0000-000000000004}";
    public const string DrawingTablesSurfaceIid = "{A0000000-0000-0000-0000-000000000005}";
    public const string DrawingTableSurfaceIid = "{A0000000-0000-0000-0000-000000000006}";
    public const string TableSurfaceIid = "{A0000000-0000-0000-0000-000000000007}";
    public const string CellSurfaceIid = "{A0000000-0000-0000-0000-000000000008}";
    public const string TextSurfaceIid = "{A0000000-0000-0000-0000-000000000009}";

    public static ComInvokeDescriptor CreateDescriptor(
        bool includeSurfaces = true,
        Action<ComInvokeDescriptor>? configure = null)
    {
        ComInvokeDescriptor descriptor = new()
        {
            AdapterName = "det",
            DisplayName = "Deterministic COM",
            DefaultProgIds = [DefaultProgId],
            InvokeErrorCode = "det_invoke_failed",
            ComErrorCode = "det_com_error",
            UnavailableErrorCode = "det_unavailable",
            NotFoundMessageFragment = "not found",
            NotFoundErrorCode = "det_not_found",
            BusyRetryMaxAttempts = 3,
            BusyRetryDelaysMs = [1, 2, 3],
        };

        if (includeSurfaces)
        {
            descriptor.Surfaces =
            [
                Surface("IComCoverageApplication", typeof(IComCoverageApplication), ApplicationSurfaceIid, "application", "app"),
                Surface("IComCoverageDocument", typeof(IComCoverageDocument), DocumentSurfaceIid, "document", "doc"),
                Surface("IComCoverageView", typeof(IComCoverageView), ViewSurfaceIid, "view"),
                Surface("IComCoverageSymbols", typeof(IComCoverageSymbols), SymbolsSurfaceIid, "symbols", "symbols2d"),
                Surface("IComCoverageDrawingTables", typeof(IComCoverageDrawingTables), DrawingTablesSurfaceIid, "drawingTables"),
                Surface("IComCoverageDrawingTable", typeof(IComCoverageDrawingTable), DrawingTableSurfaceIid, "drawingTable"),
                Surface("IComCoverageTable", typeof(IComCoverageTable), TableSurfaceIid, "table"),
                Surface("IComCoverageCell", typeof(IComCoverageCell), CellSurfaceIid, "cell"),
                Surface("IComCoverageText", typeof(IComCoverageText), TextSurfaceIid, "text"),
            ];
        }

        configure?.Invoke(descriptor);
        return descriptor;
    }

    [SupportedOSPlatform("windows")]
    public static TestDispatcherHarness CreateHarness(
        DeterministicComApplicationProvider provider,
        ProfileDefinition profile,
        ComInvokeDescriptor descriptor)
    {
        UtilitySettings settings = TestDsl.Settings(profile, descriptor);
        FakeManifestService manifestService = new();
        FakeClock clock = new(DateTimeOffset.Parse("2026-03-11T00:00:00+00:00", CultureInfo.InvariantCulture));
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
            new DeterministicComInvokeSurfaceFactory(provider));
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

    public static ComSurfaceDefinition Surface(string name, Type type, string iid, params string[] aliases)
    {
        return new ComSurfaceDefinition
        {
            Name = name,
            ClrTypeName = type.AssemblyQualifiedName,
            Iid = iid,
            Aliases = aliases.ToList(),
        };
    }
}

internal enum DeterministicState
{
    Unknown = 0,
    Ready = 1,
    Busy = 2,
}

internal interface IComCoverageApplication
{
    string ProgId { get; }
    string AdapterStamp { get; }
    string ThreadSummary { get; }
    bool BaseAssigned { get; set; }
    bool VisibleAssigned { get; set; }
    bool WarmupAssigned { get; set; }
    bool RealtimeAssigned { get; set; }
    bool ThrowOnIgnoredAssignment { get; set; }
    IComCoverageDocument ActiveDocument { get; }
    ComHintInfo Info { get; }
    ComHintCollection Items { get; }
    ComHintItem[] ItemArray { get; }
    string EchoOptional(string prefix, string suffix = "default", int repeat = 1);
    string NamedOptional(string first, string second = "two", string third = "three");
    string Overload(int value);
    string Overload(string value);
    string DescribeValue(object? value);
    string DescribeMissingToken(Type value);
    string AcceptState(DeterministicState state);
    string Mutate(ref int value, out string status);
    string CaptureThread(string tag, int delayMs = 0);
    void ThrowNotFound();
    void ThrowInvoke();
    void ThrowCallRejected();
    void ThrowServerBusy();
    void ThrowDisconnected();
}

internal interface IComCoverageDocument
{
    string Title { get; }
    string FullName { get; }
    bool Released { get; }
    IComCoverageView ActiveView { get; }
    string Save(string path);
    string CaptureThread(string tag, int delayMs = 0);
}

internal interface IComCoverageView
{
    string Name { get; }
}

internal interface IComCoverageSymbols
{
    IComCoverageDrawingTables DrawingTables { get; }
}

internal interface IComCoverageDrawingTables : IEnumerable<ComHintItem>
{
    int Count { get; }
    IComCoverageDrawingTable Add(int rows, int cols);
    IComCoverageDrawingTable Load(string path);
    ComHintItem Item(int index);
    ComHintItem Item(string name);
}

internal interface IComCoverageDrawingTable
{
}

internal interface IComCoverageTable
{
    IComCoverageCell this[int row, int col] { get; }
    IComCoverageCell get_Cell(int row, int col);
}

internal interface IComCoverageCell
{
    IComCoverageText Text { get; }
}

internal interface IComCoverageText
{
    string Str { get; set; }
}

[SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Deterministic fixture must emulate COM HRESULT failures.")]
internal sealed class DeterministicComApplication : IComCoverageApplication
{
    private readonly DeterministicComDocument _document;
    private bool _baseAssigned;
    private bool _visibleAssigned;
    private bool _warmupAssigned;
    private bool _realtimeAssigned;
    private bool _throwOnIgnoredAssignment;

    public DeterministicComApplication(string progId, int instanceId)
    {
        ProgId = progId;
        InstanceId = instanceId;
        _document = new DeterministicComDocument(this, $"Document-{instanceId}.cdw");
        Info = new ComHintInfo($"info-{instanceId}", $"category-{instanceId}", $@"C:\Temp\info-{instanceId}.txt");
        Items = new ComHintCollection(
        [
            new ComHintItem("first", instanceId),
            new ComHintItem("second", instanceId + 1),
            new ComHintItem("third", instanceId + 2),
        ]);
    }

    public string ProgId { get; }

    public int InstanceId { get; }

    public string AdapterStamp => $"{ProgId}:{InstanceId}";

    public string ThreadSummary => string.Join(",", CapturedThreads.OrderBy(value => value));

    public int BaseAssignmentCount { get; private set; }

    public int VisibleAssignmentCount { get; private set; }

    public int WarmupAssignmentCount { get; private set; }

    public int RealtimeAssignmentCount { get; private set; }

    public bool Released { get; private set; }

    public List<int> CapturedThreads { get; } = [];

    public bool BaseAssigned
    {
        get => _baseAssigned;
        set
        {
            _baseAssigned = value;
            BaseAssignmentCount++;
        }
    }

    public bool VisibleAssigned
    {
        get => _visibleAssigned;
        set
        {
            _visibleAssigned = value;
            VisibleAssignmentCount++;
        }
    }

    public bool WarmupAssigned
    {
        get => _warmupAssigned;
        set
        {
            _warmupAssigned = value;
            WarmupAssignmentCount++;
        }
    }

    public bool RealtimeAssigned
    {
        get => _realtimeAssigned;
        set
        {
            _realtimeAssigned = value;
            RealtimeAssignmentCount++;
        }
    }

    public bool ThrowOnIgnoredAssignment
    {
        get => _throwOnIgnoredAssignment;
        set
        {
            _throwOnIgnoredAssignment = value;
            if (value)
            {
                throw new InvalidOperationException("ignored assignment failure");
            }
        }
    }

    public IComCoverageDocument ActiveDocument
    {
        get
        {
            EnsureAvailable();
            return _document;
        }
    }

    public ComHintInfo Info { get; }

    public ComHintCollection Items { get; }

    public ComHintItem[] ItemArray => Items.ToArray();

    public string EchoOptional(string prefix, string suffix = "default", int repeat = 1)
        => string.Join("|", Enumerable.Repeat($"{prefix}:{suffix}", repeat));

    public string NamedOptional(string first, string second = "two", string third = "three")
        => $"{first}|{second}|{third}";

    public string Overload(int value) => $"int:{value}";

    public string Overload(string value) => $"string:{value}";

    public string DescribeValue(object? value)
    {
        return value switch
        {
            null => "null",
            DBNull => "dbnull",
            object[,] matrix => $"matrix:{matrix.GetLength(0)}x{matrix.GetLength(1)}:{matrix[0, 0]}",
            object[] array => $"array:{array.Length}:{array[0]}",
            Type missing when ReferenceEquals(missing, Type.Missing) => "missing",
            _ => $"{value.GetType().Name}:{value}",
        };
    }

    public string DescribeMissingToken(Type value)
        => ReferenceEquals(value, Type.Missing) ? "missing" : $"type:{value.FullName}";

    public string AcceptState(DeterministicState state) => $"state:{state}";

    public string Mutate(ref int value, out string status)
    {
        value += 7;
        status = $"status:{value}";
        return $"mutated:{value}";
    }

    public string CaptureThread(string tag, int delayMs = 0)
    {
        EnsureAvailable();
        int threadId = Environment.CurrentManagedThreadId;
        CapturedThreads.Add(threadId);
        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        return $"{tag}:{threadId}:{InstanceId}";
    }

    public void ThrowNotFound() => throw new InvalidOperationException("Document not found for deterministic fixture.");

    public void ThrowInvoke() => throw new InvalidOperationException("Deterministic invoke failure.");

    public void ThrowCallRejected() => throw new COMException("RPC_E_CALL_REJECTED", unchecked((int)0x80010001));

    public void ThrowServerBusy() => throw new COMException("RPC_E_SERVERCALL_RETRYLATER", unchecked((int)0x8001010A));

    public void ThrowDisconnected() => throw new COMException("RPC_E_DISCONNECTED", unchecked((int)0x80010108));

    public DeterministicComDocument ReplaceDocument(string title)
    {
        _document.ReplaceIdentity(title);
        return _document;
    }

    public void MarkReleased()
    {
        Released = true;
        _document.MarkReleased();
    }

    private void EnsureAvailable()
    {
        if (Released)
        {
            throw new COMException("Application was released.", unchecked((int)0x80010108));
        }
    }
}

[SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Deterministic fixture must emulate COM disconnects.")]
internal sealed class DeterministicComDocument : IComCoverageDocument
{
    private readonly DeterministicComView _view = new("Main");

    public DeterministicComDocument(DeterministicComApplication application, string title)
    {
        Application = application;
        ReplaceIdentity(title);
    }

    public DeterministicComApplication Application { get; }

    public string Title { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public bool Released { get; private set; }

    public IComCoverageView ActiveView
    {
        get
        {
            EnsureAvailable();
            return _view;
        }
    }

    public string Save(string path)
    {
        EnsureAvailable();
        FullName = path;
        Title = Path.GetFileName(path);
        return path;
    }

    public string CaptureThread(string tag, int delayMs = 0)
    {
        EnsureAvailable();
        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        return $"{tag}:{Environment.CurrentManagedThreadId}:{Title}";
    }

    public void ReplaceIdentity(string title)
    {
        Title = title;
        FullName = $@"C:\Deterministic\{title}";
        Released = false;
    }

    public void MarkReleased() => Released = true;

    private void EnsureAvailable()
    {
        if (Released || Application.Released)
        {
            throw new COMException("Document was disconnected.", unchecked((int)0x80010108));
        }
    }
}

internal sealed class DeterministicComView(string name) : IComCoverageView, IComCoverageSymbols
{
    public string Name { get; } = name;

    public IComCoverageDrawingTables DrawingTables { get; } = new DeterministicDrawingTables();
}

internal sealed class DeterministicDrawingTables : IComCoverageDrawingTables
{
    private readonly List<ComHintItem> _items =
    [
        new("alpha", 10),
        new("beta", 20),
    ];

    public int Count => _items.Count;

    public IComCoverageDrawingTable Add(int rows, int cols) => new DeterministicDrawingTable(rows, cols);

    public IComCoverageDrawingTable Load(string path) => new DeterministicDrawingTable(1, 1) { SourcePath = path };

    public ComHintItem Item(int index) => _items[index];

    public ComHintItem Item(string name)
        => _items.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerator<ComHintItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class DeterministicDrawingTable(int rows, int cols) : IComCoverageDrawingTable, IComCoverageTable
{
    private readonly Dictionary<(int Row, int Col), DeterministicCell> _cells = new();

    public int Rows { get; } = rows;

    public int Cols { get; } = cols;

    public string? SourcePath { get; set; }

    public IComCoverageCell this[int row, int col]
    {
        get
        {
            if (!_cells.TryGetValue((row, col), out DeterministicCell? cell))
            {
                cell = new DeterministicCell();
                _cells[(row, col)] = cell;
            }

            return cell;
        }
    }

    public IComCoverageCell get_Cell(int row, int col) => this[row, col];
}

internal sealed class DeterministicCell : IComCoverageCell
{
    public IComCoverageText Text { get; } = new DeterministicText();
}

internal sealed class DeterministicText : IComCoverageText
{
    public string Str { get; set; } = string.Empty;
}

internal sealed record ComHintInfo(string Name, string Category, string Path);

internal sealed record ComHintItem(string Name, int Value);

internal sealed class ComHintCollection : IEnumerable<ComHintItem>
{
    private readonly List<ComHintItem> _items;

    public ComHintCollection(IEnumerable<ComHintItem> items)
    {
        _items = items.ToList();
    }

    public int Count => _items.Count;

    public ComHintItem this[int index] => _items[index];

    public ComHintItem this[string name] => _items.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    public ComHintItem[] ToArray() => _items.ToArray();

    public IEnumerator<ComHintItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

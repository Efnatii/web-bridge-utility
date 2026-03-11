using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Adapters.Com;

public sealed class ComInvokeSurface : ReflectiveInvokeSurfaceBase<IReflectiveInvokeRuntime>, IDisposable
{
    private readonly IReflectiveInvokeRuntime _runtime;

    public ComInvokeSurface(IReflectiveInvokeRuntime runtime)
        : base(runtime)
    {
        _runtime = runtime;
    }

    public void Dispose()
    {
        if (_runtime is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed class UnavailableComInvokeRuntime : IReflectiveInvokeRuntime
{
    private readonly string _adapterName;
    private readonly string _message;
    private readonly string _errorCode;

    public UnavailableComInvokeRuntime(string adapterName, string message, string errorCode = "adapter_unavailable")
    {
        _adapterName = adapterName;
        _message = message;
        _errorCode = errorCode;
    }

    public string AdapterName => _adapterName;

    public object ResolveRoot(string rootName, JsonObject arguments)
        => throw new InvalidOperationException(_message);

    public object? AdaptValue(object? value) => value;

    public JsonNode? ConvertResult(object? value, InvokeDefinition definition, JsonObject arguments)
        => throw new InvalidOperationException(_message);

    public ReportVerbosity GetDefaultReportVerbosity(JsonObject arguments) => ReportVerbosity.Full;

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
        => throw new InvalidOperationException(_message);

    public CommandExecutionResult MapInvokeException(string commandId, Exception exception, InvokeDefinition definition)
        => CommandExecutionResult.Fail(_errorCode, _message);
}

internal delegate ComApplicationBinding AcquireComApplicationDelegate(
    IEnumerable<string> progIds,
    bool attachOnly,
    bool createIfMissing,
    Action<object>? initializeExisting,
    Action<object>? initializeCreated);

[SupportedOSPlatform("windows")]
internal sealed class ComInvokeRuntimeHooks
{
    public AcquireComApplicationDelegate AcquireApplication { get; init; } = ComInvokeRuntime.DefaultAcquireApplication;

    public Action<object?> ReleaseApplication { get; init; } = ComInvokeRuntime.DefaultReleaseApplication;
}

[SupportedOSPlatform("windows")]
public sealed class ComInvokeRuntime : ComAutomationRuntimeBase, IReflectiveInvokeRuntime, IHandleArgumentResolver, IReflectiveInvokeCastRuntime, IDisposable
{
    private readonly ComInvokeDescriptor _descriptor;
    private readonly string _adapterName;
    private readonly ComSurfaceCatalog _catalog;
    private readonly StaOperationDispatcher _dispatcher;
    private readonly ComInvokeRuntimeHooks _hooks;
    private readonly Dictionary<string, CachedComApplication> _applicationsByProgId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComSharedContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public ComInvokeRuntime(ComInvokeDescriptor descriptor)
        : this(descriptor, hooks: null)
    {
    }

    internal ComInvokeRuntime(ComInvokeDescriptor descriptor, ComInvokeRuntimeHooks? hooks)
    {
        _descriptor = descriptor;
        _adapterName = descriptor.AdapterName;
        _catalog = new ComSurfaceCatalog(descriptor);
        _hooks = hooks ?? new ComInvokeRuntimeHooks();
        _dispatcher = new StaOperationDispatcher(
            string.IsNullOrWhiteSpace(descriptor.DispatcherName)
                ? $"{descriptor.DisplayName} COM"
                : descriptor.DispatcherName);
    }

    public string AdapterName => _adapterName;

    public object ResolveRoot(string rootName, JsonObject arguments)
    {
        return rootName.ToLowerInvariant() switch
        {
            "application" => GetApplication(arguments, _descriptor),
            "handle" => ResolveHandleRoot(arguments, _descriptor),
            _ => throw new InvalidOperationException($"{_descriptor.DisplayName} root '{rootName}' is not supported."),
        };
    }

    public object? AdaptValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is ComRuntimeValue runtimeValue)
        {
            return runtimeValue;
        }

        if (!ShouldWrapValue(value))
        {
            return value;
        }

        ResolvedComSurface? surface = _catalog.ResolveSurfaceForValue(value);
        ComHandleInfo handleInfo = RegisterHandleInfo(
            value,
            _descriptor.AdapterName,
            surface?.Name,
            surface is null ? null : [surface.Name]);
        object currentValue = surface is not null && handleInfo.TryGetSurfaceValue(surface.Name, out object? resolvedSurfaceValue)
            ? resolvedSurfaceValue ?? value
            : value;
        return new ComRuntimeValue(this, currentValue, handleInfo, surface?.Name ?? handleInfo.CurrentSurface);
    }

    public JsonNode? ConvertResult(object? value, InvokeDefinition definition, JsonObject arguments)
    {
        if (value is null)
        {
            return null;
        }

        object adapted = AdaptValue(value) ?? value;
        if (adapted is ComRuntimeValue runtimeValue)
        {
            return ConvertRuntimeValue(runtimeValue, arguments);
        }

        bool compact = IsCompactMode(arguments);
        foreach (ComResultHintDefinition hint in _descriptor.ResultHints)
        {
            if (TryConvertWithHint(adapted, hint, compact, out JsonNode? node))
            {
                UpdateSharedContext(arguments, hint.Name, adapted, node);
                return node;
            }
        }

        if (TryConvertGenericComObject(adapted, out JsonNode? comNode))
        {
            UpdateSharedContext(arguments, "last", adapted, comNode);
            return comNode;
        }

        return InvokeJsonNodeConverter.Convert(adapted);
    }

    public ReportVerbosity GetDefaultReportVerbosity(JsonObject arguments)
    {
        return _descriptor.CompactReportDefault || ReadBoolean(arguments, "realtime")
            ? ReportVerbosity.Compact
            : ReportVerbosity.Full;
    }

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        StaInvocationOptions options = new()
        {
            AdapterName = _adapterName,
            DispatcherName = _dispatcher.Name,
            BusyRetryDelaysMs = _descriptor.BusyRetryDelaysMs.Count == 0
                ? Array.Empty<int>()
                : _descriptor.BusyRetryDelaysMs.ToArray(),
            BusyRetryMaxAttempts = _descriptor.BusyRetryMaxAttempts,
        };
        return _dispatcher.InvokeAsync(action, options, cancellationToken);
    }

    public CommandExecutionResult MapInvokeException(string commandId, Exception exception, InvokeDefinition definition)
    {
        return exception switch
        {
            COMException comException => CommandExecutionResult.Fail(_descriptor.ComErrorCode, comException.Message),
            InvalidOperationException invalidOperationException
                when !string.IsNullOrWhiteSpace(_descriptor.NotFoundMessageFragment) &&
                    invalidOperationException.Message.Contains(_descriptor.NotFoundMessageFragment, StringComparison.OrdinalIgnoreCase)
                => CommandExecutionResult.Fail(_descriptor.NotFoundErrorCode ?? _descriptor.InvokeErrorCode, invalidOperationException.Message),
            InvalidOperationException invalidOperationException
                when invalidOperationException.Message.Contains("ProgID", StringComparison.OrdinalIgnoreCase)
                => CommandExecutionResult.Fail(_descriptor.UnavailableErrorCode, invalidOperationException.Message),
            InvalidOperationException invalidOperationException => CommandExecutionResult.Fail(_descriptor.InvokeErrorCode, invalidOperationException.Message),
            _ => CommandExecutionResult.Fail(_descriptor.InvokeErrorCode, exception.Message),
        };
    }

    public object ResolveHandleArgument(string handleId)
        => ResolveRegisteredHandleInfo(handleId, _descriptor.DisplayName).ResolveCurrentValue();

    public object? CastValue(object? value, string target, InvokeCastSemantics semantics)
    {
        if (value is null)
        {
            return semantics == InvokeCastSemantics.TryCast
                ? null
                : throw new InvalidOperationException(
                    $"{_descriptor.DisplayName} cannot cast a null value to '{target}'.");
        }

        object adapted = AdaptValue(value) ?? value;
        object sourceValue = adapted is IReflectiveInvocationValueAdapter invocationValueAdapter
            ? invocationValueAdapter.GetInvocationValue() ?? value
            : adapted;
        ComRuntimeValue? runtimeValue = adapted as ComRuntimeValue;
        ResolvedComCastTarget resolvedTarget = _catalog.ResolveCastTarget(target);
        if (ComSurfaceCatalog.TryResolveValue(sourceValue, resolvedTarget, out object? castValue, out string? resolvedSurface))
        {
            string? surfaceName = resolvedSurface;
            ComHandleInfo handleInfo = runtimeValue?.HandleInfo ?? RegisterHandleInfo(
                sourceValue,
                _descriptor.AdapterName,
                runtimeValue?.CurrentSurface,
                runtimeValue?.HandleInfo.ResolvedInterfaces);
            if (!string.IsNullOrWhiteSpace(surfaceName) && castValue is not null)
            {
                handleInfo.RememberSurfaceValue(surfaceName, castValue);
            }

            return castValue is null
                ? null
                : new ComRuntimeValue(this, castValue, handleInfo, surfaceName ?? runtimeValue?.CurrentSurface);
        }

        if (semantics == InvokeCastSemantics.TryCast)
        {
            return null;
        }

        string runtimeType = sourceValue.GetType().FullName ?? sourceValue.GetType().Name;
        string currentSurface = runtimeValue?.CurrentSurface ?? _catalog.ResolveSurfaceForValue(sourceValue)?.Name ?? "<unknown>";
        string possibleCasts = string.Join(", ", GetPossibleCasts(sourceValue, runtimeValue?.CurrentSurface));
        string suffix = string.IsNullOrWhiteSpace(possibleCasts)
            ? string.Empty
            : $" Possible casts: {possibleCasts}.";
        throw new InvalidOperationException(
            $"{_descriptor.DisplayName} cannot cast runtime type '{runtimeType}' from surface '{currentSurface}' to '{target}'.{suffix}");
    }

    public void Dispose()
    {
        foreach (object application in _applicationsByProgId.Values.Select(entry => entry.Application).Distinct(ReferenceEqualityComparer.Instance))
        {
            _hooks.ReleaseApplication(application);
        }
        _dispatcher.Dispose();
    }

    internal ResolvedComSurface? ResolveSurface(string? currentSurface, object value)
        => _catalog.ResolveCurrentSurface(value, currentSurface);

    internal IReadOnlyList<string> GetAvailableMembers(object value, string? currentSurface)
        => _catalog.GetAvailableMembers(UnwrapValue(value), currentSurface);

    internal IReadOnlyList<string> GetPossibleCasts(object value, string? currentSurface)
        => _catalog.GetPossibleCasts(UnwrapValue(value), currentSurface);

    internal Exception CreateInvokeDiagnosticException(object value, string? currentSurface, string member, Exception exception)
    {
        object candidate = UnwrapValue(value);
        string runtimeType = candidate.GetType().FullName ?? candidate.GetType().Name;
        string surfaceName = currentSurface ?? _catalog.ResolveSurfaceForValue(candidate)?.Name ?? "<unknown>";
        string availableMembers = string.Join(", ", GetAvailableMembers(candidate, currentSurface).Take(16));
        string castHints = string.Join(", ", _catalog.FindCastsForMember(candidate, member, currentSurface));
        string castMessage = string.IsNullOrWhiteSpace(castHints)
            ? string.Empty
            : $" Try cast({castHints.Split(',')[0].Trim()}).";
        string membersMessage = string.IsNullOrWhiteSpace(availableMembers)
            ? string.Empty
            : $" Available members: {availableMembers}.";
        return new InvalidOperationException(
            $"{_descriptor.DisplayName} could not resolve member '{member}' on runtime type '{runtimeType}' using surface '{surfaceName}'.{membersMessage}{castMessage} {exception.Message}".Trim(),
            exception);
    }

    private JsonNode? ConvertRuntimeValue(ComRuntimeValue value, JsonObject arguments)
    {
        bool compact = IsCompactMode(arguments);
        object rawValue = value.GetInvocationValue() ?? value;
        foreach (ComResultHintDefinition hint in _descriptor.ResultHints)
        {
            if (TryConvertWithHint(rawValue, hint, compact, out JsonNode? node))
            {
                JsonNode? decorated = DecorateNode(value, node);
                UpdateSharedContext(arguments, hint.Name, value, decorated);
                return decorated;
            }
        }

        JsonObject fallback = new()
        {
            ["stringValue"] = rawValue.ToString(),
        };
        JsonNode? decoratedFallback = DecorateNode(value, fallback);
        UpdateSharedContext(arguments, "last", value, decoratedFallback);
        return decoratedFallback;
    }

    private bool ShouldWrapValue(object value)
    {
        if (value is ComRuntimeValue)
        {
            return true;
        }

        if (_catalog.HasConfiguredSurfaces && _catalog.ResolveSurfaceForValue(value) is not null)
        {
            return true;
        }

        return Marshal.IsComObject(value) || value.GetType().IsCOMObject || FindHandleInfo(value) is not null;
    }

    private static object UnwrapValue(object value)
        => value is IReflectiveInvocationValueAdapter adapter ? adapter.GetInvocationValue() ?? value : value;

    private JsonObject DecorateNode(ComRuntimeValue runtimeValue, JsonNode? node)
    {
        JsonObject objectNode = node as JsonObject ?? new JsonObject { ["value"] = node?.DeepClone() };
        objectNode["handleId"] ??= runtimeValue.HandleInfo.HandleId;
        objectNode["adapter"] = _descriptor.AdapterName;
        objectNode["runtimeType"] = runtimeValue.HandleInfo.RuntimeType;
        objectNode["surface"] = runtimeValue.CurrentSurface;
        objectNode["resolvedInterfaces"] = new JsonArray(
            runtimeValue.HandleInfo.ResolvedInterfaces
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());
        objectNode["memberNames"] = new JsonArray(
            GetAvailableMembers(runtimeValue.GetInvocationValue() ?? runtimeValue, runtimeValue.CurrentSurface)
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());
        objectNode["possibleCasts"] = new JsonArray(
            GetPossibleCasts(runtimeValue.GetInvocationValue() ?? runtimeValue, runtimeValue.CurrentSurface)
                .Select(name => (JsonNode?)JsonValue.Create(name))
                .ToArray());
        return objectNode;
    }

    private object GetApplication(JsonObject arguments, ComInvokeDescriptor descriptor)
    {
        bool refresh = ReadBoolean(arguments, "refresh");
        bool attachOnly = ReadBoolean(arguments, "attachOnly");
        bool createIfMissing = ReadBoolean(arguments, "createIfMissing", defaultValue: !attachOnly);
        bool visible = ReadBoolean(arguments, "visible", defaultValue: IsVisibleModeEnabled());
        bool realtime = ReadBoolean(arguments, "realtime");
        string? sharedContextId = TryGetSharedContextId(arguments);
        string[] progIds = ReadProgIds(arguments, descriptor.DefaultProgIds.ToArray());

        if (!refresh &&
            descriptor.ReuseDocumentContext &&
            TryGetSharedContextApplication(sharedContextId, progIds, out object? contextualApplication))
        {
            EnsureApplicationConfigured(contextualApplication, descriptor, visible, realtime, progIds[0]);
            return contextualApplication;
        }

        if (!refresh &&
            descriptor.ReuseApplication &&
            TryGetCachedApplication(progIds, out object? cachedApplication, out string cachedProgId))
        {
            EnsureApplicationConfigured(cachedApplication, descriptor, visible, realtime, cachedProgId);
            UpdateSharedContextApplication(sharedContextId, cachedProgId, cachedApplication);
            return cachedApplication;
        }

        ComApplicationBinding binding = _hooks.AcquireApplication(
            progIds,
            attachOnly: attachOnly,
            createIfMissing: createIfMissing,
            initializeExisting: application => EnsureApplicationConfigured(application, descriptor, visible, realtime, bindingProgId: null),
            initializeCreated: application => EnsureApplicationConfigured(application, descriptor, visible, realtime, bindingProgId: null));
        if (_applicationsByProgId.TryGetValue(binding.ProgId, out CachedComApplication? previousApplication) &&
            !ReferenceEquals(previousApplication.Application, binding.Application))
        {
            _hooks.ReleaseApplication(previousApplication.Application);
        }

        CachedComApplication cacheEntry = EnsureCacheEntry(binding.ProgId, binding.Application);
        EnsureApplicationConfigured(cacheEntry.Application, descriptor, visible, realtime, binding.ProgId);
        UpdateSharedContextApplication(sharedContextId, binding.ProgId, cacheEntry.Application);
        return binding.Application;
    }

    private void EnsureApplicationConfigured(object application, ComInvokeDescriptor descriptor, bool visible, bool realtime, string? bindingProgId)
    {
        string progId = bindingProgId ??
            _applicationsByProgId.FirstOrDefault(pair => ReferenceEquals(pair.Value.Application, application)).Key ??
            descriptor.DefaultProgIds.FirstOrDefault() ??
            descriptor.AdapterName;
        CachedComApplication cacheEntry = EnsureCacheEntry(progId, application);
        string baseSignature = BuildAssignmentSignature(descriptor.ApplicationAssignments);
        if (!string.Equals(cacheEntry.ApplicationAssignmentsSignature, baseSignature, StringComparison.Ordinal))
        {
            ApplyAssignments(application, descriptor.ApplicationAssignments);
            cacheEntry.ApplicationAssignmentsSignature = baseSignature;
        }

        if (visible)
        {
            string visibleSignature = BuildAssignmentSignature(descriptor.VisibleApplicationAssignments);
            if (!string.Equals(cacheEntry.VisibleAssignmentsSignature, visibleSignature, StringComparison.Ordinal))
            {
                ApplyAssignments(application, descriptor.VisibleApplicationAssignments);
                cacheEntry.VisibleAssignmentsSignature = visibleSignature;
            }
        }

        string warmupSignature = BuildAssignmentSignature(descriptor.WarmupAssignments);
        if (!string.Equals(cacheEntry.WarmupAssignmentsSignature, warmupSignature, StringComparison.Ordinal))
        {
            ApplyAssignments(application, descriptor.WarmupAssignments);
            cacheEntry.WarmupAssignmentsSignature = warmupSignature;
        }

        if (realtime)
        {
            string realtimeSignature = BuildAssignmentSignature(descriptor.RealtimeAssignments);
            if (!string.Equals(cacheEntry.RealtimeAssignmentsSignature, realtimeSignature, StringComparison.Ordinal))
            {
                ApplyAssignments(application, descriptor.RealtimeAssignments);
                cacheEntry.RealtimeAssignmentsSignature = realtimeSignature;
            }
        }
    }

    private static void ApplyAssignments(object target, IReadOnlyList<ComMemberAssignmentDefinition> assignments)
    {
        foreach (ComMemberAssignmentDefinition assignment in assignments)
        {
            try
            {
                object? value = ConvertAssignmentValue(assignment.Value);
                ReflectiveInvokeAccessor.SetMemberValue(target, assignment.Member, value);
            }
            catch when (assignment.IgnoreErrors)
            {
            }
        }
    }

    private static object? ConvertAssignmentValue(JsonNode? value)
    {
        return value switch
        {
            null => null,
            JsonValue jsonValue when jsonValue.TryGetValue(out bool booleanValue) => booleanValue,
            JsonValue jsonValue when jsonValue.TryGetValue(out int intValue) => intValue,
            JsonValue jsonValue when jsonValue.TryGetValue(out long longValue) => longValue,
            JsonValue jsonValue when jsonValue.TryGetValue(out double doubleValue) => doubleValue,
            JsonValue jsonValue when jsonValue.TryGetValue(out decimal decimalValue) => decimalValue,
            JsonValue jsonValue when jsonValue.TryGetValue(out string? stringValue) => stringValue,
            _ => value.Deserialize<object?>(),
        };
    }

    private void UpdateSharedContext(JsonObject arguments, string hintName, object value, JsonNode? node)
    {
        string? sharedContextId = TryGetSharedContextId(arguments);
        if (string.IsNullOrWhiteSpace(sharedContextId))
        {
            return;
        }

        object rawValue = UnwrapValue(value);
        string? surfaceName = value is ComRuntimeValue runtimeValue
            ? runtimeValue.CurrentSurface
            : _catalog.ResolveSurfaceForValue(rawValue)?.Name;
        ComHandleInfo handleInfo = value is ComRuntimeValue existingRuntimeValue
            ? existingRuntimeValue.HandleInfo
            : RegisterHandleInfo(
                rawValue,
                _descriptor.AdapterName,
                surfaceName,
                surfaceName is null ? null : [surfaceName]);
        ComSharedContext context = GetOrCreateContext(sharedContextId);
        context.LastHandleId = handleInfo.HandleId;
        if (!string.IsNullOrWhiteSpace(hintName))
        {
            context.NamedHandleIds[hintName] = handleInfo.HandleId;
        }

        if (node is JsonObject objectNode)
        {
            objectNode["handleId"] = handleInfo.HandleId;
        }
    }

    private bool TryConvertWithHint(object value, ComResultHintDefinition hint, bool compact, out JsonNode? node)
    {
        if (hint.IsCollection)
        {
            return TryConvertCollection(value, hint, compact, out node);
        }

        if (!MatchesObjectHint(value, hint))
        {
            node = null;
            return false;
        }

        node = BuildObjectNode(value, hint, compact);
        return true;
    }

    private bool TryConvertCollection(object value, ComResultHintDefinition hint, bool compact, out JsonNode? node)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            node = null;
            return false;
        }

        JsonArray array = new();
        bool convertedAny = false;
        foreach (object? item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            if (!MatchesItemHint(item, hint))
            {
                node = null;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(hint.CollectionScalarMember))
            {
                object? scalar = ReadFirstValue(item, [hint.CollectionScalarMember!]);
                if (!HasMeaningfulValue(scalar))
                {
                    node = null;
                    return false;
                }

                array.Add(ToJsonNode(scalar));
            }
            else
            {
                array.Add(BuildObjectNode(item, hint, compact));
            }

            convertedAny = true;
        }

        node = convertedAny ? array : null;
        return convertedAny;
    }

    private bool MatchesObjectHint(object value, ComResultHintDefinition hint)
    {
        if (hint.MatchCurrentApplicationReference && IsRegisteredApplication(value))
        {
            return true;
        }

        return MatchesHintMembers(value, hint);
    }

    private static bool MatchesItemHint(object value, ComResultHintDefinition hint)
        => MatchesHintMembers(value, hint);

    private static bool MatchesHintMembers(object value, ComResultHintDefinition hint)
    {
        if (hint.RequiredAllMembers.Count > 0 &&
            hint.RequiredAllMembers.Any(member => !HasMeaningfulValue(ReadFirstValue(value, [member]))))
        {
            return false;
        }

        if (hint.RequiredAnyMembers.Count > 0 &&
            !hint.RequiredAnyMembers.Any(member => HasMeaningfulValue(ReadFirstValue(value, [member]))))
        {
            return false;
        }

        return true;
    }

    private JsonObject BuildObjectNode(object value, ComResultHintDefinition hint, bool compact)
    {
        JsonObject node = new();
        string? runtimeProgId = TryResolveRuntimeProgId(value);
        string? surfaceName = _catalog.ResolveSurfaceForValue(value)?.Name;
        ComHandleInfo? handleInfo = ShouldWrapValue(value)
            ? RegisterHandleInfo(
                value,
                _descriptor.AdapterName,
                surfaceName,
                surfaceName is null ? null : [surfaceName])
            : null;

        if (hint.IncludeHandleId)
        {
            node["handleId"] = handleInfo?.HandleId;
        }

        IReadOnlyList<ComResultFieldDefinition> fields = compact && hint.CompactFields.Count > 0
            ? hint.CompactFields
            : hint.Fields;
        foreach (ComResultFieldDefinition field in fields)
        {
            node[field.Name] = ResolveFieldValue(value, field, runtimeProgId);
        }

        if (handleInfo is not null)
        {
            node["adapter"] = _descriptor.AdapterName;
            node["runtimeType"] = handleInfo.RuntimeType;
            node["surface"] = surfaceName;
            node["resolvedInterfaces"] = new JsonArray(
                handleInfo.ResolvedInterfaces
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray());
            node["memberNames"] = new JsonArray(
                GetAvailableMembers(value, surfaceName)
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray());
            node["possibleCasts"] = new JsonArray(
                GetPossibleCasts(value, surfaceName)
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray());
        }

        return node;
    }

    private static JsonNode? ResolveFieldValue(object value, ComResultFieldDefinition field, string? runtimeProgId)
    {
        if (field.UseRuntimeProgId)
        {
            return runtimeProgId is null ? null : JsonValue.Create(runtimeProgId);
        }

        if (field.StaticValue is not null)
        {
            return field.StaticValue.DeepClone();
        }

        object? memberValue = ReadFirstValue(value, field.MemberPaths);
        return ToJsonNode(memberValue);
    }

    private bool TryGetCachedApplication(IEnumerable<string> requestedProgIds, out object application, out string progId)
    {
        foreach (string requestedProgId in requestedProgIds)
        {
            if (_applicationsByProgId.TryGetValue(requestedProgId, out CachedComApplication? cachedApplication))
            {
                application = cachedApplication.Application;
                progId = requestedProgId;
                return true;
            }
        }

        application = null!;
        progId = string.Empty;
        return false;
    }

    private bool IsRegisteredApplication(object value)
        => _applicationsByProgId.Values.Any(application => ReferenceEquals(application.Application, value));

    private string? TryResolveRuntimeProgId(object value)
    {
        foreach ((string progId, CachedComApplication application) in _applicationsByProgId)
        {
            if (ReferenceEquals(application.Application, value))
            {
                return progId;
            }
        }

        return null;
    }

    private CachedComApplication EnsureCacheEntry(string progId, object application)
    {
        if (_applicationsByProgId.TryGetValue(progId, out CachedComApplication? existing))
        {
            existing.Application = application;
            return existing;
        }

        CachedComApplication created = new()
        {
            ProgId = progId,
            Application = application,
        };
        _applicationsByProgId[progId] = created;
        return created;
    }

    private bool TryGetSharedContextApplication(string? sharedContextId, IEnumerable<string> requestedProgIds, out object application)
    {
        if (string.IsNullOrWhiteSpace(sharedContextId) || !_contexts.TryGetValue(sharedContextId, out ComSharedContext? context))
        {
            application = null!;
            return false;
        }

        if (context.Application is null || string.IsNullOrWhiteSpace(context.ApplicationProgId))
        {
            application = null!;
            return false;
        }

        if (!requestedProgIds.Contains(context.ApplicationProgId, StringComparer.OrdinalIgnoreCase))
        {
            application = null!;
            return false;
        }

        application = context.Application;
        return true;
    }

    private void UpdateSharedContextApplication(string? sharedContextId, string progId, object application)
    {
        if (string.IsNullOrWhiteSpace(sharedContextId))
        {
            return;
        }

        ComSharedContext context = GetOrCreateContext(sharedContextId);
        context.ApplicationProgId = progId;
        context.Application = application;
        string? surfaceName = _catalog.ResolveSurfaceForValue(application)?.Name;
        context.LastHandleId = RegisterHandleInfo(
            application,
            _descriptor.AdapterName,
            surfaceName,
            surfaceName is null ? null : [surfaceName]).HandleId;
        context.NamedHandleIds["application"] = context.LastHandleId;
    }

    private ComSharedContext GetOrCreateContext(string sharedContextId)
    {
        if (_contexts.TryGetValue(sharedContextId, out ComSharedContext? context))
        {
            return context;
        }

        context = new ComSharedContext();
        _contexts[sharedContextId] = context;
        return context;
    }

    private object ResolveHandleRoot(JsonObject arguments, ComInvokeDescriptor descriptor)
    {
        if (arguments["handleId"] is not null)
        {
            return ResolveHandle(arguments, descriptor.DisplayName);
        }

        string? sharedContextId = TryGetSharedContextId(arguments);
        if (!string.IsNullOrWhiteSpace(sharedContextId) && _contexts.TryGetValue(sharedContextId, out ComSharedContext? context))
        {
            string? handleKey = arguments["contextHandle"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(handleKey) &&
                context.NamedHandleIds.TryGetValue(handleKey, out string? namedHandleId))
            {
                return ResolveRegisteredHandleInfo(namedHandleId, descriptor.DisplayName).ResolveCurrentValue();
            }

            if (!string.IsNullOrWhiteSpace(context.LastHandleId))
            {
                return ResolveRegisteredHandleInfo(context.LastHandleId, descriptor.DisplayName).ResolveCurrentValue();
            }
        }

        throw new InvalidOperationException($"{descriptor.DisplayName} COM handle was not provided and no shared context handle is available.");
    }

    private static string BuildAssignmentSignature(IReadOnlyList<ComMemberAssignmentDefinition> assignments)
    {
        if (assignments.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("|", assignments.Select(assignment =>
            $"{assignment.Member}:{assignment.IgnoreErrors}:{assignment.Value?.ToJsonString() ?? "<null>"}"));
    }

    private static string? TryGetSharedContextId(JsonObject arguments)
        => arguments["__sharedContextId"]?.GetValue<string>();

    private static bool IsCompactMode(JsonObject arguments)
        => string.Equals(
            arguments["__reportVerbosity"]?.GetValue<string>(),
            ReportVerbosity.Compact.ToString(),
            StringComparison.OrdinalIgnoreCase);

    private static object? ReadFirstValue(object value, IReadOnlyList<string> memberPaths)
    {
        foreach (string memberPath in memberPaths)
        {
            object? memberValue = ReadMemberPath(value, memberPath);
            if (HasMeaningfulValue(memberValue))
            {
                return memberValue;
            }
        }

        return null;
    }

    private static object? ReadMemberPath(object value, string memberPath)
    {
        object? current = value;
        foreach (string segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is null)
            {
                return null;
            }

            current = TryReadMemberValue(current, segment);
        }

        return current;
    }

    private static bool HasMeaningfulValue(object? value)
    {
        return value switch
        {
            null => false,
            string stringValue => !string.IsNullOrWhiteSpace(stringValue),
            _ => true,
        };
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            _ => InvokeJsonNodeConverter.Convert(value),
        };
    }

    internal static ComApplicationBinding DefaultAcquireApplication(
        IEnumerable<string> progIds,
        bool attachOnly,
        bool createIfMissing,
        Action<object>? initializeExisting,
        Action<object>? initializeCreated)
        => AcquireApplication(
            progIds,
            attachOnly: attachOnly,
            createIfMissing: createIfMissing,
            initializeExisting: initializeExisting,
            initializeCreated: initializeCreated);

    internal static void DefaultReleaseApplication(object? value) => TryFinalRelease(value);
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

public sealed class ComInvokeDescriptor
{
    public string AdapterName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DispatcherName { get; set; } = string.Empty;

    public List<string> DefaultProgIds { get; set; } = new();

    public string ComErrorCode { get; set; } = string.Empty;

    public string InvokeErrorCode { get; set; } = string.Empty;

    public string UnavailableErrorCode { get; set; } = "adapter_unavailable";

    public string? NotFoundMessageFragment { get; set; }

    public string? NotFoundErrorCode { get; set; }

    public string UnavailableMessage { get; set; } = "COM runtime is not available.";

    public bool ReuseApplication { get; set; } = true;

    public bool ReuseDocumentContext { get; set; } = true;

    public int BusyRetryMaxAttempts { get; set; } = 5;

    public List<int> BusyRetryDelaysMs { get; set; } = new();

    public bool CompactReportDefault { get; set; }

    public List<string> InteropAssemblies { get; set; } = new();

    public List<ComSurfaceDefinition> Surfaces { get; set; } = new();

    public List<ComMemberAssignmentDefinition> ApplicationAssignments { get; set; } = new();

    public List<ComMemberAssignmentDefinition> VisibleApplicationAssignments { get; set; } = new();

    public List<ComMemberAssignmentDefinition> WarmupAssignments { get; set; } = new();

    public List<ComMemberAssignmentDefinition> RealtimeAssignments { get; set; } = new();

    public List<ComResultHintDefinition> ResultHints { get; set; } = new();

    public ComInvokeDescriptor Clone()
    {
        return new ComInvokeDescriptor
        {
            AdapterName = AdapterName,
            DisplayName = DisplayName,
            DispatcherName = DispatcherName,
            DefaultProgIds = DefaultProgIds.ToList(),
            ComErrorCode = ComErrorCode,
            InvokeErrorCode = InvokeErrorCode,
            UnavailableErrorCode = UnavailableErrorCode,
            NotFoundMessageFragment = NotFoundMessageFragment,
            NotFoundErrorCode = NotFoundErrorCode,
            UnavailableMessage = UnavailableMessage,
            ReuseApplication = ReuseApplication,
            ReuseDocumentContext = ReuseDocumentContext,
            BusyRetryMaxAttempts = BusyRetryMaxAttempts,
            BusyRetryDelaysMs = BusyRetryDelaysMs.ToList(),
            CompactReportDefault = CompactReportDefault,
            InteropAssemblies = InteropAssemblies.ToList(),
            Surfaces = Surfaces.Select(surface => surface.Clone()).ToList(),
            ApplicationAssignments = ApplicationAssignments.Select(assignment => assignment.Clone()).ToList(),
            VisibleApplicationAssignments = VisibleApplicationAssignments.Select(assignment => assignment.Clone()).ToList(),
            WarmupAssignments = WarmupAssignments.Select(assignment => assignment.Clone()).ToList(),
            RealtimeAssignments = RealtimeAssignments.Select(assignment => assignment.Clone()).ToList(),
            ResultHints = ResultHints.Select(hint => hint.Clone()).ToList(),
        };
    }
}

public sealed class ComSurfaceDefinition
{
    public string Name { get; set; } = string.Empty;

    public string? ClrTypeName { get; set; }

    public List<string> Aliases { get; set; } = new();

    public string? Iid { get; set; }

    public ComSurfaceDefinition Clone()
    {
        return new ComSurfaceDefinition
        {
            Name = Name,
            ClrTypeName = ClrTypeName,
            Aliases = Aliases.ToList(),
            Iid = Iid,
        };
    }
}

public sealed class ComMemberAssignmentDefinition
{
    public string Member { get; set; } = string.Empty;

    public JsonNode? Value { get; set; }

    public bool IgnoreErrors { get; set; } = true;

    public ComMemberAssignmentDefinition Clone()
    {
        return new ComMemberAssignmentDefinition
        {
            Member = Member,
            Value = Value?.DeepClone(),
            IgnoreErrors = IgnoreErrors,
        };
    }
}

public sealed class ComResultHintDefinition
{
    public string Name { get; set; } = string.Empty;

    public bool IsCollection { get; set; }

    public bool MatchCurrentApplicationReference { get; set; }

    public bool IncludeHandleId { get; set; } = true;

    public string? CollectionScalarMember { get; set; }

    public List<string> RequiredAllMembers { get; set; } = new();

    public List<string> RequiredAnyMembers { get; set; } = new();

    public List<ComResultFieldDefinition> Fields { get; set; } = new();

    public List<ComResultFieldDefinition> CompactFields { get; set; } = new();

    public ComResultHintDefinition Clone()
    {
        return new ComResultHintDefinition
        {
            Name = Name,
            IsCollection = IsCollection,
            MatchCurrentApplicationReference = MatchCurrentApplicationReference,
            IncludeHandleId = IncludeHandleId,
            CollectionScalarMember = CollectionScalarMember,
            RequiredAllMembers = RequiredAllMembers.ToList(),
            RequiredAnyMembers = RequiredAnyMembers.ToList(),
            Fields = Fields.Select(field => field.Clone()).ToList(),
            CompactFields = CompactFields.Select(field => field.Clone()).ToList(),
        };
    }
}

public sealed class ComResultFieldDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<string> MemberPaths { get; set; } = new();

    public JsonNode? StaticValue { get; set; }

    public bool UseRuntimeProgId { get; set; }

    public ComResultFieldDefinition Clone()
    {
        return new ComResultFieldDefinition
        {
            Name = Name,
            MemberPaths = MemberPaths.ToList(),
            StaticValue = StaticValue?.DeepClone(),
            UseRuntimeProgId = UseRuntimeProgId,
        };
    }
}

internal sealed class CachedComApplication
{
    public string ProgId { get; set; } = string.Empty;

    public object Application { get; set; } = null!;

    public string ApplicationAssignmentsSignature { get; set; } = string.Empty;

    public string VisibleAssignmentsSignature { get; set; } = string.Empty;

    public string WarmupAssignmentsSignature { get; set; } = string.Empty;

    public string RealtimeAssignmentsSignature { get; set; } = string.Empty;
}

internal sealed class ComSharedContext
{
    public string? ApplicationProgId { get; set; }

    public object? Application { get; set; }

    public string? LastHandleId { get; set; }

    public Dictionary<string, string> NamedHandleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}


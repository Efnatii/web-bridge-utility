using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using WebBridge.Utility.Protocol;
using Microsoft.VisualBasic;

namespace WebBridge.Utility.Core;

public abstract class ReflectiveInvokeSurfaceBase<TRuntime> : IAdapterInvokeSurface
    where TRuntime : IReflectiveInvokeRuntime
{
    protected ReflectiveInvokeSurfaceBase(TRuntime runtime)
    {
        Runtime = runtime;
    }

    protected TRuntime Runtime { get; }

    public string AdapterName => Runtime.AdapterName;

    public PreparedCommandPlan Compile(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Invoke is null)
        {
            throw new InvalidOperationException($"Command '{definition.CommandId}' does not define invoke metadata.");
        }

        ValidateInvoke(definition.CommandId, definition.Invoke);

        return new PreparedCommandPlan(
            AdapterName,
            definition.CommandId,
            (arguments, cancellationToken) => ExecuteAsync(definition.CommandId, definition.Invoke, arguments, cancellationToken));
    }

    protected virtual void ValidateInvoke(string commandId, InvokeDefinition invoke)
    {
        if (string.IsNullOrWhiteSpace(invoke.Root))
        {
            throw new InvalidOperationException($"Command '{commandId}' must define invoke.root.");
        }

        foreach (InvokeStepDefinition step in invoke.Chain)
        {
            string operation = step.Operation.Trim();
            if (!IsSupportedOperation(operation))
            {
                throw new InvalidOperationException(
                    $"Command '{commandId}' uses unsupported invoke operation '{step.Operation}'.");
            }

            if (!string.Equals(operation, "index", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(operation, "new", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(step.Member))
            {
                throw new InvalidOperationException(
                    $"Command '{commandId}' invoke operation '{step.Operation}' requires a member name.");
            }
        }
    }

    protected virtual bool IsSupportedOperation(string operation)
    {
        return string.Equals(operation, "get", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "set", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "index", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "new", StringComparison.OrdinalIgnoreCase) ||
            Runtime is IReflectiveInvokeCastRuntime &&
            (string.Equals(operation, "cast", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(operation, "tryCast", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(operation, "queryInterface", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CommandExecutionResult> ExecuteAsync(
        string commandId,
        InvokeDefinition invoke,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Runtime.RunAsync(
                () => ExecuteCore(commandId, invoke, arguments),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportVerbosity verbosity = ResolveReportVerbosity(arguments);
            JsonObject report = CreateBaseReport(commandId, invoke, arguments, verbosity);
            report["success"] = false;
            report["fatalError"] = CreateErrorNode(Unwrap(exception));
            return Runtime.MapInvokeException(commandId, Unwrap(exception), invoke)
                .WithReport(report);
        }
    }

    private CommandExecutionResult ExecuteCore(string commandId, InvokeDefinition invoke, JsonObject arguments)
    {
        ReportVerbosity verbosity = ResolveReportVerbosity(arguments);
        JsonObject report = CreateBaseReport(commandId, invoke, arguments, verbosity);
        JsonArray? steps = verbosity == ReportVerbosity.Full ? new JsonArray() : null;
        if (steps is not null)
        {
            report["steps"] = steps;
        }

        Dictionary<string, object?> storedValues = new(StringComparer.OrdinalIgnoreCase);
        object? current = null;
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            current = Runtime.AdaptValue(ResolveRoot(invoke.Root, arguments, storedValues));
            if (current is null)
            {
                throw new InvalidOperationException(
                    $"Command '{commandId}' resolved root '{invoke.Root}' to null.");
            }

            if (verbosity == ReportVerbosity.Full)
            {
                report["rootSnapshot"] = SafePreview(current, invoke, arguments);
            }

            for (int index = 0; index < invoke.Chain.Count; index++)
            {
                current = ApplyStep(current, invoke.Chain[index], arguments, invoke, steps, index, storedValues);
            }

            if (!string.IsNullOrWhiteSpace(invoke.ReturnPath))
            {
                object? beforeReturnPath = current;
                current = Runtime.AdaptValue(ResolvePath(current, invoke.ReturnPath!, storedValues));
                steps?.Add(new JsonObject
                {
                    ["index"] = steps?.Count ?? 0,
                    ["operation"] = "returnPath",
                    ["member"] = invoke.ReturnPath,
                    ["before"] = verbosity == ReportVerbosity.Full ? SafePreview(beforeReturnPath, invoke, arguments) : null,
                    ["after"] = verbosity == ReportVerbosity.Full ? SafePreview(current, invoke, arguments) : null,
                });
            }

            JsonNode? finalResult = Runtime.ConvertResult(current, invoke, arguments);
            report["success"] = true;
            report["durationMs"] = stopwatch.Elapsed.TotalMilliseconds;
            if (verbosity == ReportVerbosity.Full)
            {
                report["finalResult"] = finalResult?.DeepClone();
                report["storedValues"] = CreateStoredValuesPreview(storedValues, invoke, arguments);
            }
            else
            {
                report["finalResultKind"] = finalResult is null ? "null" : finalResult.GetType().Name;
                report["storedValueKeys"] = new JsonArray(storedValues.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
            }

            return CommandExecutionResult.Ok(finalResult, report);
        }
        catch (Exception exception)
        {
            Exception unwrapped = Unwrap(exception);
            report["success"] = false;
            report["durationMs"] = stopwatch.Elapsed.TotalMilliseconds;
            if (verbosity == ReportVerbosity.Full)
            {
                report["currentValue"] = SafePreview(current, invoke, arguments);
                report["storedValues"] = CreateStoredValuesPreview(storedValues, invoke, arguments);
            }
            else
            {
                report["storedValueKeys"] = new JsonArray(storedValues.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
            }

            report["error"] = CreateErrorNode(unwrapped);
            return Runtime.MapInvokeException(commandId, unwrapped, invoke)
                .WithReport(report);
        }
    }

    private object? ApplyStep(
        object? current,
        InvokeStepDefinition step,
        JsonObject arguments,
        InvokeDefinition invoke,
        JsonArray? steps,
        int stepIndex,
        Dictionary<string, object?> storedValues)
    {
        if (current is null)
        {
            throw new InvalidOperationException(
                $"Invoke operation '{step.Operation}' cannot run because the current value is null.");
        }

        object? before = current;
        ResolvedInvokeArgument[] resolvedArguments = ResolveArguments(step.Args, arguments, invoke, storedValues);
        object? after;
        JsonNode? capturedArgumentsPreview = null;
        bool captureDetailedStepReport = steps is not null;

        if (string.Equals(step.Operation, "cast", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.Operation, "tryCast", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.Operation, "queryInterface", StringComparison.OrdinalIgnoreCase))
        {
            if (Runtime is not IReflectiveInvokeCastRuntime castRuntime)
            {
                throw new InvalidOperationException(
                    $"Adapter '{AdapterName}' does not support invoke operation '{step.Operation}'.");
            }

            InvokeCastSemantics semantics = string.Equals(step.Operation, "cast", StringComparison.OrdinalIgnoreCase)
                ? InvokeCastSemantics.Cast
                : string.Equals(step.Operation, "tryCast", StringComparison.OrdinalIgnoreCase)
                ? InvokeCastSemantics.TryCast
                : InvokeCastSemantics.QueryInterface;
            after = castRuntime.CastValue(current, step.Member, semantics);
        }
        else if (string.Equals(step.Operation, "get", StringComparison.OrdinalIgnoreCase))
        {
            after = ReflectiveInvokeAccessor.GetMemberValue(current, step.Member);
        }
        else if (string.Equals(step.Operation, "set", StringComparison.OrdinalIgnoreCase))
        {
            object? value = ResolveSetValue(step, arguments, invoke, resolvedArguments, storedValues);
            ReflectiveInvokeAccessor.SetMemberValue(current, step.Member, value);
            after = current;
        }
        else if (string.Equals(step.Operation, "call", StringComparison.OrdinalIgnoreCase))
        {
            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.CallMember(current, step.Member, resolvedArguments);
            after = outcome.Value ?? current;
            CaptureStoredArguments(storedValues, outcome.CapturedArguments, Runtime);
            if (captureDetailedStepReport)
            {
                capturedArgumentsPreview = CreateCapturedArgumentsPreview(outcome.CapturedArguments, invoke, arguments);
            }
        }
        else if (string.Equals(step.Operation, "index", StringComparison.OrdinalIgnoreCase))
        {
            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.IndexValue(current, step.Member, resolvedArguments);
            after = outcome.Value;
            CaptureStoredArguments(storedValues, outcome.CapturedArguments, Runtime);
            if (captureDetailedStepReport)
            {
                capturedArgumentsPreview = CreateCapturedArgumentsPreview(outcome.CapturedArguments, invoke, arguments);
            }
        }
        else if (string.Equals(step.Operation, "new", StringComparison.OrdinalIgnoreCase))
        {
            InvokeCallOutcome outcome = ReflectiveInvokeAccessor.CreateInstance(current, resolvedArguments);
            after = outcome.Value;
            CaptureStoredArguments(storedValues, outcome.CapturedArguments, Runtime);
            if (captureDetailedStepReport)
            {
                capturedArgumentsPreview = CreateCapturedArgumentsPreview(outcome.CapturedArguments, invoke, arguments);
            }
        }
        else
        {
            throw new InvalidOperationException($"Unsupported invoke operation '{step.Operation}'.");
        }

        after = Runtime.AdaptValue(NormalizeAsyncResult(after));

        if (!string.IsNullOrWhiteSpace(step.StoreAs))
        {
            storedValues[step.StoreAs] = after;
        }

        if (steps is not null)
        {
            JsonObject stepNode = new()
            {
                ["index"] = stepIndex,
                ["operation"] = step.Operation,
                ["member"] = step.Member,
                ["arguments"] = CreateArgumentsPreview(resolvedArguments),
                ["before"] = SafePreview(before, invoke, arguments),
                ["after"] = SafePreview(after, invoke, arguments),
                ["storedAs"] = step.StoreAs,
            };
            if (capturedArgumentsPreview is not null)
            {
                stepNode["capturedArguments"] = capturedArgumentsPreview;
            }

            steps.Add(stepNode);
        }

        return after;
    }

    private static object? ResolveSetValue(
        InvokeStepDefinition step,
        JsonObject arguments,
        InvokeDefinition invoke,
        ResolvedInvokeArgument[] resolvedArguments,
        Dictionary<string, object?> storedValues)
    {
        if (!string.IsNullOrWhiteSpace(step.ValueArgument))
        {
            return ResolveValuePath(arguments, storedValues, step.ValueArgument!);
        }

        if (step.Args.Count > 0)
        {
            return resolvedArguments[0].Value;
        }

        throw new InvalidOperationException(
            $"Invoke set operation for member '{step.Member}' requires either valueArgument or args[0].");
    }

    private ResolvedInvokeArgument[] ResolveArguments(
        IReadOnlyList<InvokeArgumentDefinition> definitions,
        JsonObject arguments,
        InvokeDefinition invoke,
        Dictionary<string, object?> storedValues)
    {
        ResolvedInvokeArgument[] resolved = new ResolvedInvokeArgument[definitions.Count];
        for (int index = 0; index < definitions.Count; index++)
        {
            resolved[index] = ResolveArgumentValue(definitions[index], arguments, invoke, storedValues);
        }

        return resolved;
    }

    private JsonObject CreateBaseReport(string commandId, InvokeDefinition invoke, JsonObject arguments, ReportVerbosity verbosity)
    {
        JsonObject report = new()
        {
            ["mode"] = "invoke",
            ["adapter"] = AdapterName,
            ["commandId"] = commandId,
            ["root"] = invoke.Root,
            ["startedAtUtc"] = DateTimeOffset.UtcNow,
            ["reportVerbosity"] = verbosity.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(invoke.ReturnPath))
        {
            report["returnPath"] = invoke.ReturnPath;
        }

        if (verbosity == ReportVerbosity.Full)
        {
            report["arguments"] = JsonObjectMerge.Clone(arguments);
        }
        else
        {
            report["argumentKeys"] = new JsonArray(arguments.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
        }

        RuntimeExecutionContext? runtimeContext = RuntimeExecutionContextScope.Current;
        if (runtimeContext is not null)
        {
            report["runtime"] = new JsonObject
            {
                ["adapter"] = runtimeContext.AdapterName,
                ["dispatcher"] = runtimeContext.DispatcherName,
                ["queueDepth"] = runtimeContext.QueueDepth,
                ["queueWaitMs"] = runtimeContext.QueueWaitMilliseconds,
                ["busyRetryMaxAttempts"] = runtimeContext.BusyRetryMaxAttempts,
                ["busyRetryDelaysMs"] = new JsonArray(runtimeContext.BusyRetryDelaysMs.Select(delay => JsonValue.Create(delay)).ToArray()),
            };
        }

        return report;
    }

    private JsonObject CreateStoredValuesPreview(
        IReadOnlyDictionary<string, object?> storedValues,
        InvokeDefinition invoke,
        JsonObject arguments)
    {
        JsonObject preview = new();
        foreach ((string key, object? value) in storedValues)
        {
            preview[key] = SafePreview(value, invoke, arguments);
        }

        return preview;
    }

    private JsonObject CreateCapturedArgumentsPreview(
        IReadOnlyDictionary<string, object?> capturedArguments,
        InvokeDefinition invoke,
        JsonObject arguments)
    {
        JsonObject preview = new();
        foreach ((string key, object? value) in capturedArguments)
        {
            preview[key] = SafePreview(value, invoke, arguments);
        }

        return preview;
    }

    private static JsonArray CreateArgumentsPreview(IEnumerable<ResolvedInvokeArgument> arguments)
    {
        JsonArray array = new();
        foreach (ResolvedInvokeArgument argument in arguments)
        {
            array.Add(new JsonObject
            {
                ["name"] = argument.Name,
                ["byRef"] = argument.ByRef,
                ["captureAs"] = argument.CaptureAs,
                ["source"] = argument.Source,
                ["value"] = SafeConvertValue(argument.Value),
            });
        }

        return array;
    }

    private JsonNode? SafePreview(object? value, InvokeDefinition invoke, JsonObject arguments)
    {
        try
        {
            return Runtime.ConvertResult(value, invoke, arguments);
        }
        catch
        {
            return SafeConvertValue(value);
        }
    }

    private static JsonNode? SafeConvertValue(object? value)
    {
        try
        {
            return InvokeJsonNodeConverter.Convert(value);
        }
        catch
        {
            return JsonValue.Create(value?.ToString());
        }
    }

    private static JsonObject CreateErrorNode(Exception exception)
    {
        return new JsonObject
        {
            ["type"] = exception.GetType().FullName,
            ["message"] = exception.Message,
        };
    }

    private object? ResolveRoot(
        string root,
        JsonObject arguments,
        Dictionary<string, object?> storedValues)
    {
        if (root.StartsWith('$'))
        {
            return ResolveStoredPath(storedValues, root[1..]);
        }

        if (root.StartsWith("stored:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveStoredPath(storedValues, root["stored:".Length..]);
        }

        if (root.StartsWith("arg:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveArgumentPath(arguments, root["arg:".Length..]);
        }

        return Runtime.ResolveRoot(root, arguments);
    }

    private static void CaptureStoredArguments(
        IDictionary<string, object?> storedValues,
        IReadOnlyDictionary<string, object?> capturedArguments,
        IReflectiveInvokeRuntime runtime)
    {
        foreach ((string key, object? value) in capturedArguments)
        {
            storedValues[key] = runtime.AdaptValue(value);
        }
    }

    private ResolvedInvokeArgument ResolveArgumentValue(
        InvokeArgumentDefinition definition,
        JsonObject arguments,
        InvokeDefinition invoke,
        Dictionary<string, object?> storedValues)
    {
        object? rawValue;
        string source;
        if (definition.FromStored is not null)
        {
            rawValue = ResolveStoredPath(storedValues, definition.FromStored);
            source = "stored";
        }
        else if (definition.FromArgument is not null)
        {
            rawValue = ResolveArgumentPath(arguments, definition.FromArgument);
            source = "argument";
        }
        else
        {
            rawValue = definition.Literal?.DeepClone();
            source = "literal";
        }

        object? value = string.IsNullOrWhiteSpace(definition.Converter)
            ? ConvertJsonNodeLikeValue(rawValue)
            : ApplyConverter(definition.Converter!, rawValue, invoke);

        return new ResolvedInvokeArgument(
            definition.Name,
            value,
            definition.ByRef,
            definition.CaptureAs,
            source);
    }

    private object? ApplyConverter(string converter, object? rawValue, InvokeDefinition invoke)
    {
        if (invoke.Converters.TryGetValue(converter, out string? nestedConverter) &&
            !string.Equals(nestedConverter, converter, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyConverter(nestedConverter, rawValue, invoke);
        }

        JsonNode? node = rawValue as JsonNode;
        string normalized = converter.Trim();

        if (string.Equals(normalized, "json", StringComparison.OrdinalIgnoreCase))
        {
            if (node is not null)
            {
                return node.DeepClone();
            }

            return rawValue switch
            {
                null => null,
                string stringValue => JsonValue.Create(stringValue),
                bool boolValue => JsonValue.Create(boolValue),
                int intValue => JsonValue.Create(intValue),
                long longValue => JsonValue.Create(longValue),
                double doubleValue => JsonValue.Create(doubleValue),
                decimal decimalValue => JsonValue.Create(decimalValue),
                float floatValue => JsonValue.Create(floatValue),
                DateTimeOffset dateTimeOffsetValue => JsonValue.Create(dateTimeOffsetValue),
                DateTime dateTimeValue => JsonValue.Create(dateTimeValue),
                Guid guidValue => JsonValue.Create(guidValue.ToString()),
                _ => JsonValue.Create(rawValue.ToString()),
            };
        }

        if (string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(normalized, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return Type.Missing;
        }

        if (string.Equals(normalized, "dbnull", StringComparison.OrdinalIgnoreCase))
        {
            return DBNull.Value;
        }

        if (string.Equals(normalized, "enum", StringComparison.OrdinalIgnoreCase))
        {
            string key = ConvertJsonNodeLikeValue(rawValue)?.ToString()
                ?? throw new InvalidOperationException("Enum conversion requires a string-compatible value.");
            if (!invoke.EnumMap.TryGetValue(key, out string? mapped))
            {
                throw new InvalidOperationException($"Enum value '{key}' was not found in invoke.enumMap.");
            }

            return ParseMappedEnumValue(mapped);
        }

        object? value = ConvertJsonNodeLikeValue(rawValue);
        return normalized.ToLowerInvariant() switch
        {
            "handle" => ResolveHandleReference(rawValue, value),
            "string" => value?.ToString(),
            "path" => value is null ? null : NormalizePathValue(value),
            "stringarray" => ConvertToStringArray(rawValue),
            "objectarray" or "variantarray" => ConvertToObjectArray(rawValue),
            "matrix" or "objectmatrix" => ConvertToObjectMatrix(rawValue),
            "bytes" or "base64bytes" => ConvertToByteArray(value),
            "bool" or "boolean" => value is bool booleanValue
                ? booleanValue
                : bool.Parse(value?.ToString() ?? throw new InvalidOperationException("Boolean conversion requires a value.")),
            "byte" => Convert.ToByte(value, CultureInfo.InvariantCulture),
            "sbyte" => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            "short" or "int16" => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            "ushort" or "uint16" => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            "int" or "int32" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "uint" or "uint32" => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            "long" or "int64" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "ulong" or "uint64" => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            "float" or "single" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            "double" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            "decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            "datetime" => ConvertToDateTime(value),
            "datetimeoffset" => ConvertToDateTimeOffset(value),
            "timespan" => ConvertToTimeSpan(value),
            "uri" => new Uri(value?.ToString() ?? throw new InvalidOperationException("Uri conversion requires a value."), UriKind.RelativeOrAbsolute),
            "guid" => Guid.Parse(value?.ToString() ?? throw new InvalidOperationException("Guid conversion requires a value.")),
            _ => throw new InvalidOperationException($"Unknown invoke converter '{converter}'."),
        };
    }

    private string NormalizePathValue(object value)
    {
        string path = value.ToString()
            ?? throw new InvalidOperationException("Path conversion requires a string-compatible value.");
        if (Runtime is IPathArgumentNormalizer normalizer)
        {
            return normalizer.NormalizePathArgument(path);
        }

        return Path.GetFullPath(path);
    }

    private object ResolveHandleReference(object? rawValue, object? convertedValue)
    {
        string handleId = rawValue switch
        {
            JsonObject jsonObject when jsonObject["handleId"] is JsonNode handleNode => handleNode.ToString(),
            _ => convertedValue?.ToString()
                ?? throw new InvalidOperationException("handle conversion requires a handleId-compatible value."),
        };

        if (Runtime is not IHandleArgumentResolver resolver)
        {
            throw new InvalidOperationException($"Adapter '{AdapterName}' does not support handle argument conversion.");
        }

        return resolver.ResolveHandleArgument(handleId);
    }

    private static string[] ConvertToStringArray(object? rawValue)
    {
        if (rawValue is not JsonArray jsonArray)
        {
            throw new InvalidOperationException("stringArray conversion requires a JSON array.");
        }

        return jsonArray.Select(item => ConvertJsonNodeLikeValue(item)?.ToString() ?? string.Empty).ToArray();
    }

    private static object?[] ConvertToObjectArray(object? rawValue)
    {
        if (rawValue is not JsonArray jsonArray)
        {
            throw new InvalidOperationException("objectArray conversion requires a JSON array.");
        }

        return jsonArray.Select(ConvertJsonNodeLikeValue).ToArray();
    }

    private static object?[,] ConvertToObjectMatrix(object? rawValue)
    {
        if (rawValue is not JsonArray outerArray || outerArray.Count == 0)
        {
            throw new InvalidOperationException("matrix conversion requires a non-empty JSON array of arrays.");
        }

        JsonArray[] rows = outerArray.Select(node => node as JsonArray ?? throw new InvalidOperationException("matrix conversion requires each row to be a JSON array.")).ToArray();
        int columnCount = rows[0].Count;
        if (rows.Any(row => row.Count != columnCount))
        {
            throw new InvalidOperationException("matrix conversion requires a rectangular JSON array.");
        }

        object?[,] matrix = new object?[rows.Length, columnCount];
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                matrix[rowIndex, columnIndex] = ConvertJsonNodeLikeValue(rows[rowIndex][columnIndex]);
            }
        }

        return matrix;
    }

    private static byte[] ConvertToByteArray(object? value)
    {
        if (value is byte[] bytes)
        {
            return bytes;
        }

        string base64 = value?.ToString()
            ?? throw new InvalidOperationException("bytes conversion requires a Base64 string value.");
        return Convert.FromBase64String(base64);
    }

    private static DateTime ConvertToDateTime(object? value)
    {
        return value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
            _ => DateTime.Parse(value?.ToString() ?? throw new InvalidOperationException("DateTime conversion requires a value."), CultureInfo.InvariantCulture),
        };
    }

    private static DateTimeOffset ConvertToDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            _ => DateTimeOffset.Parse(value?.ToString() ?? throw new InvalidOperationException("DateTimeOffset conversion requires a value."), CultureInfo.InvariantCulture),
        };
    }

    private static TimeSpan ConvertToTimeSpan(object? value)
    {
        return value switch
        {
            TimeSpan timeSpan => timeSpan,
            _ => TimeSpan.Parse(value?.ToString() ?? throw new InvalidOperationException("TimeSpan conversion requires a value."), CultureInfo.InvariantCulture),
        };
    }

    private static object? ParseMappedEnumValue(string mapped)
    {
        if (bool.TryParse(mapped, out bool booleanValue))
        {
            return booleanValue;
        }

        if (int.TryParse(mapped, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
        {
            return intValue;
        }

        if (long.TryParse(mapped, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
        {
            return longValue;
        }

        if (double.TryParse(mapped, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
        {
            return doubleValue;
        }

        return mapped;
    }

    private static object? ResolveValuePath(
        JsonObject arguments,
        IReadOnlyDictionary<string, object?> storedValues,
        string path)
    {
        if (path.StartsWith('$'))
        {
            return ResolveStoredPath(storedValues, path[1..]);
        }

        if (path.StartsWith("stored:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveStoredPath(storedValues, path["stored:".Length..]);
        }

        return ResolveArgumentPath(arguments, path);
    }

    private static object? ResolveArgumentPath(JsonObject arguments, string path)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        object? current = arguments;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = ReflectiveInvokeAccessor.GetMemberValue(current, segment);
        }

        return ConvertJsonNodeLikeValue(current);
    }

    private static object? ResolveStoredPath(IReadOnlyDictionary<string, object?> storedValues, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Stored value path must not be empty.");
        }

        if (!storedValues.TryGetValue(segments[0], out object? current))
        {
            throw new InvalidOperationException($"Stored value '{segments[0]}' was not found.");
        }

        for (int index = 1; index < segments.Length; index++)
        {
            current = ReflectiveInvokeAccessor.GetMemberValue(current, segments[index]);
        }

        return current;
    }

    private static object? ResolvePath(
        object? current,
        string path,
        IReadOnlyDictionary<string, object?> storedValues)
    {
        if (path.StartsWith("stored:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveStoredPath(storedValues, path["stored:".Length..]);
        }

        object? value = current;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            value = ReflectiveInvokeAccessor.GetMemberValue(value, segment);
        }

        return value;
    }

    private static object? ConvertJsonNodeLikeValue(object? value)
    {
        if (value is not JsonNode node)
        {
            return value;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out string? text))
            {
                return text;
            }

            if (jsonValue.TryGetValue(out bool booleanValue))
            {
                return booleanValue;
            }

            if (jsonValue.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (jsonValue.TryGetValue(out long longValue))
            {
                return longValue;
            }

            if (jsonValue.TryGetValue(out double doubleValue))
            {
                return doubleValue;
            }

            if (jsonValue.TryGetValue(out decimal decimalValue))
            {
                return decimalValue;
            }

            if (jsonValue.TryGetValue(out DateTimeOffset dateTimeOffsetValue))
            {
                return dateTimeOffsetValue;
            }

            if (jsonValue.TryGetValue(out DateTime dateTimeValue))
            {
                return dateTimeValue;
            }

            return jsonValue.ToString();
        }

        if (node is JsonArray or JsonObject)
        {
            return node.DeepClone();
        }

        return node.ToJsonString();
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } targetInvocationException)
        {
            exception = targetInvocationException.InnerException!;
        }

        return exception;
    }

    private ReportVerbosity ResolveReportVerbosity(JsonObject arguments)
    {
        string? rawValue = arguments["__reportVerbosity"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(rawValue) &&
            Enum.TryParse(rawValue, ignoreCase: true, out ReportVerbosity parsed))
        {
            return parsed;
        }

        return Runtime.GetDefaultReportVerbosity(arguments);
    }

    private static object? NormalizeAsyncResult(object? value)
    {
        if (value is not Task task)
        {
            return value;
        }

        task.GetAwaiter().GetResult();
        Type taskType = task.GetType();
        if (!taskType.IsGenericType || taskType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return null;
        }

        return taskType.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
    }
}

internal sealed record ResolvedInvokeArgument(
    string? Name,
    object? Value,
    bool ByRef,
    string? CaptureAs,
    string Source);

internal sealed record InvokeCallOutcome(
    object? Value,
    IReadOnlyDictionary<string, object?> CapturedArguments);

internal interface IReflectiveInvocationProxy
{
    object? GetMemberValue(string member);

    void SetMemberValue(string member, object? value);

    InvokeCallOutcome CallMember(string member, ResolvedInvokeArgument[] arguments);

    InvokeCallOutcome IndexValue(string member, ResolvedInvokeArgument[] arguments);

    InvokeCallOutcome CreateInstance(ResolvedInvokeArgument[] arguments);
}

internal interface IReflectiveInvocationValueAdapter
{
    object? GetInvocationValue();
}

public static class InvokeJsonNodeConverter
{
    public static JsonNode? Convert(object? value)
    {
        return Convert(value, 0);
    }

    private static JsonNode? Convert(object? value, int depth)
    {
        if (depth > 8)
        {
            return JsonValue.Create(value?.ToString());
        }

        if (value is null)
        {
            return null;
        }

        if (value is JsonNode node)
        {
            return node.DeepClone();
        }

        if (value is string stringValue)
        {
            return JsonValue.Create(stringValue);
        }

        if (value is bool boolValue)
        {
            return JsonValue.Create(boolValue);
        }

        if (value is int intValue)
        {
            return JsonValue.Create(intValue);
        }

        if (value is long longValue)
        {
            return JsonValue.Create(longValue);
        }

        if (value is double doubleValue)
        {
            return JsonValue.Create(doubleValue);
        }

        if (value is decimal decimalValue)
        {
            return JsonValue.Create(decimalValue);
        }

        if (value is float floatValue)
        {
            return JsonValue.Create(floatValue);
        }

        if (value is DateTimeOffset dateTimeOffsetValue)
        {
            return JsonValue.Create(dateTimeOffsetValue);
        }

        if (value is DateTime dateTimeValue)
        {
            return JsonValue.Create(dateTimeValue);
        }

        if (value is Guid guidValue)
        {
            return JsonValue.Create(guidValue.ToString());
        }

        if (value is Enum enumValue)
        {
            return JsonValue.Create(enumValue.ToString());
        }

        if (value is IDictionary<string, object?> typedDictionary)
        {
            JsonObject objectNode = new();
            foreach ((string key, object? entryValue) in typedDictionary)
            {
                objectNode[key] = Convert(entryValue, depth + 1);
            }

            return objectNode;
        }

        if (value is IDictionary dictionary)
        {
            JsonObject objectNode = new();
            foreach (DictionaryEntry entry in dictionary)
            {
                objectNode[entry.Key.ToString() ?? string.Empty] = Convert(entry.Value, depth + 1);
            }

            return objectNode;
        }

        if (value is not string && value is IEnumerable enumerable)
        {
            JsonArray arrayNode = new();
            foreach (object? item in enumerable)
            {
                arrayNode.Add(Convert(item, depth + 1));
            }

            return arrayNode;
        }

        Type type = value.GetType();
        if (Marshal.IsComObject(value) || type.IsCOMObject)
        {
            return new JsonObject
            {
                ["typeName"] = type.Name,
                ["display"] = value.ToString(),
            };
        }

        PropertyInfo[] properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

        if (properties.Length == 0)
        {
            return JsonValue.Create(value.ToString());
        }

        JsonObject result = new();
        foreach (PropertyInfo property in properties)
        {
            result[property.Name] = Convert(property.GetValue(value), depth + 1);
        }

        return result;
    }
}

internal static class ReflectiveInvokeAccessor
{
    private const int RpcCallRejectedHResult = unchecked((int)0x80010001);
    private const int RpcServerBusyHResult = unchecked((int)0x8001010A);
    private static readonly int[] DefaultComRetryBackoffMilliseconds = [50, 100, 200, 400, 800];
    private static readonly Type? NewLateBindingType = Type.GetType(
        "Microsoft.VisualBasic.CompilerServices.NewLateBinding, Microsoft.VisualBasic.Core",
        throwOnError: false);
    private static readonly MethodInfo? NewLateBindingLateCall = FindNewLateBindingMethod("LateCall", 8);
    private static readonly MethodInfo? NewLateBindingLateGet = FindNewLateBindingMethod("LateGet", 7);
    private static readonly MethodInfo? NewLateBindingLateSet = FindNewLateBindingMethod("LateSet", 6);
    private static readonly MethodInfo? NewLateBindingLateIndexGet = FindNewLateBindingMethod("LateIndexGet", 3);

    public static object? GetMemberValue(object? target, string member)
    {
        if (target is null)
        {
            throw new InvalidOperationException($"Cannot read member '{member}' from a null value.");
        }

        if (target is IReflectiveInvocationProxy proxy)
        {
            return proxy.GetMemberValue(member);
        }

        if (target is JsonObject objectNode)
        {
            return objectNode.TryGetPropertyValue(member, out JsonNode? value)
                ? value
                : throw new MissingMemberException($"JSON object does not contain member '{member}'.");
        }

        if (target is JsonArray arrayNode && int.TryParse(member, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arrayIndex))
        {
            return arrayNode[arrayIndex];
        }

        if (TryGetDictionaryValue(target, member, out object? dictionaryValue))
        {
            return dictionaryValue;
        }

        if (target is IReflectiveMemberProvider memberProvider &&
            memberProvider.TryGetReflectiveMember(member, out object? providedValue))
        {
            return providedValue;
        }

        if (!IsComObject(target) && TryGetClrMemberValue(target, member, out object? clrValue))
        {
            return clrValue;
        }

        if (IsComObject(target))
        {
            return InvokeComGet(target, member);
        }

        return InvokeMember(target, member, BindingFlags.GetProperty | BindingFlags.GetField, (object?[]?)null);
    }

    public static void SetMemberValue(object target, string member, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is IReflectiveInvocationProxy proxy)
        {
            proxy.SetMemberValue(member, value);
            return;
        }

        if (target is JsonObject objectNode)
        {
            objectNode[member] = value as JsonNode ?? InvokeJsonNodeConverter.Convert(value);
            return;
        }

        if (TrySetDictionaryValue(target, member, value))
        {
            return;
        }

        if (!IsComObject(target) && TrySetClrMemberValue(target, member, value))
        {
            return;
        }

        if (IsComObject(target))
        {
            InvokeComSet(target, member, value);
            return;
        }

        _ = InvokeMember(target, member, BindingFlags.SetProperty | BindingFlags.SetField, [value]);
    }

    public static InvokeCallOutcome CallMember(object target, string member, ResolvedInvokeArgument[] arguments)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is IReflectiveInvocationProxy proxy)
        {
            return proxy.CallMember(member, arguments);
        }

        if (!IsComObject(target) && TryInvokeClrMethod(target, member, arguments, out object? result, out Dictionary<string, object?> captures))
        {
            return new InvokeCallOutcome(result, captures);
        }

        if (IsComObject(target) && CanUseSimpleComLateBinding(arguments))
        {
            object?[] invocationArguments = arguments.Select(argument => argument.Value).ToArray();
            object? comResult = InvokeComCall(target, member, invocationArguments);
            return new InvokeCallOutcome(comResult, CreateCaptureMap(arguments, invocationArguments));
        }

        return InvokeMember(target, member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, arguments);
    }

    public static InvokeCallOutcome IndexValue(object target, string member, ResolvedInvokeArgument[] arguments)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is IReflectiveInvocationProxy proxy)
        {
            return proxy.IndexValue(member, arguments);
        }

        if (target is JsonArray jsonArray)
        {
            int index = Convert.ToInt32(arguments.Single().Value, CultureInfo.InvariantCulture);
            return new InvokeCallOutcome(jsonArray[index], CreateCaptureMap(arguments, arguments.Select(argument => argument.Value).ToArray()));
        }

        object?[] argumentValues = arguments.Select(argument => argument.Value).ToArray();

        if (TryIndexDictionary(target, argumentValues, out object? dictionaryValue))
        {
            return new InvokeCallOutcome(dictionaryValue, CreateCaptureMap(arguments, argumentValues));
        }

        if (target is IList list)
        {
            int index = Convert.ToInt32(arguments.Single().Value, CultureInfo.InvariantCulture);
            return new InvokeCallOutcome(list[index], CreateCaptureMap(arguments, argumentValues));
        }

        if (!IsComObject(target) && TryGetClrIndexerValue(target, arguments, out object? clrValue, out Dictionary<string, object?> captures))
        {
            return new InvokeCallOutcome(clrValue, captures);
        }

        string actualMember = string.IsNullOrWhiteSpace(member) ? "Item" : member;
        if (IsComObject(target) && CanUseSimpleComLateBinding(arguments))
        {
            object? result = InvokeComIndex(target, actualMember, argumentValues);
            return new InvokeCallOutcome(result, CreateCaptureMap(arguments, argumentValues));
        }

        return InvokeMember(target, actualMember, BindingFlags.GetProperty | BindingFlags.InvokeMethod, arguments);
    }

    public static InvokeCallOutcome CreateInstance(object target, ResolvedInvokeArgument[] arguments)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is IReflectiveInvocationProxy proxy)
        {
            return proxy.CreateInstance(arguments);
        }

        throw new InvalidOperationException($"Invoke operation 'new' is not supported for target type '{target.GetType().FullName}'.");
    }

    internal static bool TryGetStaticMemberValue(Type targetType, string member, out object? value)
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase;
        PropertyInfo? property = targetType.GetProperty(member, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(null);
            return true;
        }

        FieldInfo? field = targetType.GetField(member, Flags);
        if (field is not null)
        {
            value = field.GetValue(null);
            return true;
        }

        value = null;
        return false;
    }

    internal static bool TrySetStaticMemberValue(Type targetType, string member, object? value)
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase;
        PropertyInfo? property = targetType.GetProperty(member, Flags);
        if (property is not null && property.CanWrite)
        {
            property.SetValue(null, CoerceValue(value, property.PropertyType));
            return true;
        }

        FieldInfo? field = targetType.GetField(member, Flags);
        if (field is not null)
        {
            field.SetValue(null, CoerceValue(value, field.FieldType));
            return true;
        }

        return false;
    }

    internal static bool TryCallStaticMember(
        Type targetType,
        string member,
        ResolvedInvokeArgument[] arguments,
        out InvokeCallOutcome outcome)
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public;
        MethodInfo[] methods = targetType
            .GetMethods(Flags)
            .Where(method => string.Equals(method.Name, member, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (!TryBindClrArguments(arguments, parameters, out object?[] coercedArguments, out List<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings))
            {
                continue;
            }

            object? result = method.Invoke(null, coercedArguments);
            outcome = new InvokeCallOutcome(result, CreateCaptureMap(bindings, coercedArguments));
            return true;
        }

        outcome = new InvokeCallOutcome(null, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        return false;
    }

    internal static InvokeCallOutcome CreateTypeInstance(Type targetType, ResolvedInvokeArgument[] arguments)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
        ConstructorInfo[] constructors = targetType.GetConstructors(Flags);
        foreach (ConstructorInfo constructor in constructors)
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (!TryBindClrArguments(arguments, parameters, out object?[] coercedArguments, out List<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings))
            {
                continue;
            }

            object? instance = constructor.Invoke(coercedArguments);
            return new InvokeCallOutcome(instance, CreateCaptureMap(bindings, coercedArguments));
        }

        if (arguments.Length == 0)
        {
            object? instance = Activator.CreateInstance(targetType)
                ?? throw new InvalidOperationException($"Failed to create instance of '{targetType.FullName}'.");
            return new InvokeCallOutcome(instance, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        }

        throw new MissingMethodException($"No constructor of '{targetType.FullName}' matches the provided arguments.");
    }

    private static bool IsComObject(object target)
    {
        return Marshal.IsComObject(target) || target.GetType().IsCOMObject;
    }

    private static bool CanUseSimpleComLateBinding(IEnumerable<ResolvedInvokeArgument> arguments)
    {
        foreach (ResolvedInvokeArgument argument in arguments)
        {
            if (!string.IsNullOrWhiteSpace(argument.Name) || argument.ByRef)
            {
                return false;
            }
        }

        return true;
    }

    private static object? InvokeComGet(object target, string member, object?[]? arguments = null)
    {
        return RetryBusyComCall(() =>
        {
            object?[] actualArguments = arguments ?? [];
            if (TryInvokeNewLateBindingGet(target, member, actualArguments, out object? result))
            {
                return result;
            }

            return Interaction.CallByName(target, member, CallType.Get, actualArguments);
        });
    }

    private static void InvokeComSet(object target, string member, object? value)
    {
        _ = RetryBusyComCall(() =>
        {
            if (TryInvokeNewLateBindingSet(target, member, [value]))
            {
                return true;
            }

            _ = Interaction.CallByName(target, member, CallType.Set, value);
            return true;
        });
    }

    private static object? InvokeComCall(object target, string member, object?[] arguments)
    {
        return RetryBusyComCall(() =>
        {
            if (TryInvokeNewLateBindingCall(target, member, arguments, out object? result))
            {
                return result;
            }

            try
            {
                return Interaction.CallByName(target, member, CallType.Method, arguments);
            }
            catch (COMException)
            {
                return InvokeComGet(target, member, arguments);
            }
        });
    }

    private static object? InvokeComIndex(object target, string member, object?[] arguments)
    {
        return RetryBusyComCall(() =>
        {
            if (string.Equals(member, "Item", StringComparison.OrdinalIgnoreCase) &&
                TryInvokeNewLateBindingIndexGet(target, arguments, out object? result))
            {
                return result;
            }

            if (TryInvokeNewLateBindingGet(target, member, arguments, out object? propertyResult))
            {
                return propertyResult;
            }

            return Interaction.CallByName(target, member, CallType.Get, arguments);
        });
    }

    private static T RetryBusyComCall<T>(Func<T> action)
    {
        RuntimeExecutionContext? runtimeContext = RuntimeExecutionContextScope.Current;
        IReadOnlyList<int> delays = runtimeContext?.BusyRetryDelaysMs?.Count > 0
            ? runtimeContext.BusyRetryDelaysMs
            : DefaultComRetryBackoffMilliseconds;
        int maxAttempts = runtimeContext?.BusyRetryMaxAttempts > 0
            ? runtimeContext.BusyRetryMaxAttempts
            : delays.Count;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                TryGetRetryableComException(exception, out _))
            {
                int delay = delays[Math.Min(attempt, delays.Count - 1)];
                Thread.Sleep(delay);
            }
        }
    }

    private static bool TryGetRetryableComException(Exception exception, out COMException comException)
    {
        Exception current = exception;
        while (current is TargetInvocationException targetInvocationException &&
            targetInvocationException.InnerException is not null)
        {
            current = targetInvocationException.InnerException;
        }

        if (current is COMException currentComException &&
            (currentComException.HResult == RpcCallRejectedHResult ||
             currentComException.HResult == RpcServerBusyHResult))
        {
            comException = currentComException;
            return true;
        }

        comException = null!;
        return false;
    }

    private static MethodInfo? FindNewLateBindingMethod(string name, int parameterCount)
    {
        return NewLateBindingType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal) &&
                method.GetParameters().Length == parameterCount);
    }

    private static bool TryInvokeNewLateBindingCall(object target, string member, object?[] arguments, out object? result)
    {
        if (NewLateBindingLateCall is null)
        {
            result = null;
            return false;
        }

        bool[] copyBack = new bool[arguments.Length];
        object?[] invokeArguments =
        [
            target,
            null,
            member,
            arguments,
            Array.Empty<string>(),
            null,
            copyBack,
            false,
        ];

        try
        {
            result = NewLateBindingLateCall.Invoke(null, invokeArguments);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private static bool TryInvokeNewLateBindingGet(object target, string member, object?[] arguments, out object? result)
    {
        if (NewLateBindingLateGet is null)
        {
            result = null;
            return false;
        }

        bool[] copyBack = new bool[arguments.Length];
        object?[] invokeArguments =
        [
            target,
            null,
            member,
            arguments,
            Array.Empty<string>(),
            null,
            copyBack,
        ];

        try
        {
            result = NewLateBindingLateGet.Invoke(null, invokeArguments);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private static bool TryInvokeNewLateBindingSet(object target, string member, object?[] arguments)
    {
        if (NewLateBindingLateSet is null)
        {
            return false;
        }

        object?[] invokeArguments =
        [
            target,
            null,
            member,
            arguments,
            Array.Empty<string>(),
            null,
        ];

        try
        {
            _ = NewLateBindingLateSet.Invoke(null, invokeArguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeNewLateBindingIndexGet(object target, object?[] arguments, out object? result)
    {
        if (NewLateBindingLateIndexGet is null)
        {
            result = null;
            return false;
        }

        object?[] invokeArguments =
        [
            target,
            arguments,
            Array.Empty<string>(),
        ];

        try
        {
            result = NewLateBindingLateIndexGet.Invoke(null, invokeArguments);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private static object? InvokeMember(object target, string member, BindingFlags bindingFlags, object?[]? arguments)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase | bindingFlags;
        return target.GetType().InvokeMember(
            member,
            flags,
            binder: null,
            target,
            arguments,
            CultureInfo.InvariantCulture);
    }

    private static InvokeCallOutcome InvokeMember(object target, string member, BindingFlags bindingFlags, ResolvedInvokeArgument[] arguments)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase | bindingFlags;
        ResolvedInvokeArgument[] orderedArguments = OrderArguments(arguments);
        object?[] invocationArguments = orderedArguments.Select(argument => argument.Value).ToArray();
        string[] namedParameters = orderedArguments
            .Where(argument => !string.IsNullOrWhiteSpace(argument.Name))
            .Select(argument => argument.Name!)
            .ToArray();
        ParameterModifier[]? modifiers = orderedArguments.Any(argument => argument.ByRef)
            ? [CreateParameterModifier(orderedArguments)]
            : null;

        object? result = target.GetType().InvokeMember(
            member,
            flags,
            binder: null,
            target,
            invocationArguments,
            modifiers,
            CultureInfo.InvariantCulture,
            namedParameters.Length == 0 ? null : namedParameters);

        return new InvokeCallOutcome(result, CreateCaptureMap(orderedArguments, invocationArguments));
    }

    private static bool TryGetClrMemberValue(object target, string member, out object? value)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        PropertyInfo? property = target.GetType().GetProperty(member, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(target);
            return true;
        }

        FieldInfo? field = target.GetType().GetField(member, Flags);
        if (field is not null)
        {
            value = field.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    private static bool TrySetClrMemberValue(object target, string member, object? value)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        PropertyInfo? property = target.GetType().GetProperty(member, Flags);
        if (property is not null && property.CanWrite)
        {
            property.SetValue(target, CoerceValue(value, property.PropertyType));
            return true;
        }

        FieldInfo? field = target.GetType().GetField(member, Flags);
        if (field is not null)
        {
            field.SetValue(target, CoerceValue(value, field.FieldType));
            return true;
        }

        return false;
    }

    private static bool TryInvokeClrMethod(
        object target,
        string member,
        ResolvedInvokeArgument[] arguments,
        out object? result,
        out Dictionary<string, object?> captures)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
        MethodInfo[] methods = target.GetType()
            .GetMethods(Flags)
            .Where(method => string.Equals(method.Name, member, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (!TryBindClrArguments(arguments, parameters, out object?[] coercedArguments, out List<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings))
            {
                continue;
            }

            result = method.Invoke(target, coercedArguments);
            captures = CreateCaptureMap(bindings, coercedArguments);
            return true;
        }

        result = null;
        captures = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryGetClrIndexerValue(
        object target,
        ResolvedInvokeArgument[] arguments,
        out object? value,
        out Dictionary<string, object?> captures)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (PropertyInfo property in target.GetType().GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                continue;
            }

            ParameterInfo[] parameters = property.GetIndexParameters();
            if (!TryBindClrArguments(arguments, parameters, out object?[] coercedArguments, out List<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings))
            {
                continue;
            }

            value = property.GetValue(target, coercedArguments);
            captures = CreateCaptureMap(bindings, coercedArguments);
            return true;
        }

        value = null;
        captures = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryBindClrArguments(
        ResolvedInvokeArgument[] arguments,
        ParameterInfo[] parameters,
        out object?[] coercedArguments,
        out List<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings)
    {
        coercedArguments = new object?[parameters.Length];
        bindings = new List<(ResolvedInvokeArgument Argument, int ParameterIndex)>(arguments.Length);
        bool[] usedArguments = new bool[arguments.Length];
        int positionalCursor = 0;

        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
        {
            int argumentIndex = FindArgumentIndex(arguments, parameters[parameterIndex], usedArguments, ref positionalCursor);
            if (argumentIndex < 0)
            {
                if (parameters[parameterIndex].IsOptional)
                {
                    coercedArguments[parameterIndex] = parameters[parameterIndex].DefaultValue is DBNull ? Type.Missing : parameters[parameterIndex].DefaultValue;
                    continue;
                }

                if (parameters[parameterIndex].IsOut)
                {
                    coercedArguments[parameterIndex] = null;
                    continue;
                }

                coercedArguments = Array.Empty<object?>();
                bindings = [];
                return false;
            }

            ResolvedInvokeArgument argument = arguments[argumentIndex];
            Type targetType = parameters[parameterIndex].ParameterType.IsByRef
                ? parameters[parameterIndex].ParameterType.GetElementType() ?? typeof(object)
                : parameters[parameterIndex].ParameterType;
            if (!TryCoerceValue(argument.Value, targetType, out object? coercedValue) &&
                !(parameters[parameterIndex].IsOut && argument.Value is null))
            {
                coercedArguments = Array.Empty<object?>();
                bindings = [];
                return false;
            }

            coercedArguments[parameterIndex] = coercedValue;
            usedArguments[argumentIndex] = true;
            bindings.Add((argument, parameterIndex));
        }

        if (usedArguments.Any(used => !used))
        {
            coercedArguments = Array.Empty<object?>();
            bindings = [];
            return false;
        }

        return true;
    }

    private static int FindArgumentIndex(
        IReadOnlyList<ResolvedInvokeArgument> arguments,
        ParameterInfo parameter,
        bool[] usedArguments,
        ref int positionalCursor)
    {
        int namedIndex = -1;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (usedArguments[index] || string.IsNullOrWhiteSpace(arguments[index].Name))
            {
                continue;
            }

            if (string.Equals(arguments[index].Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
            {
                namedIndex = index;
                break;
            }
        }

        if (namedIndex >= 0)
        {
            return namedIndex;
        }

        while (positionalCursor < arguments.Count)
        {
            int currentIndex = positionalCursor++;
            if (usedArguments[currentIndex] || !string.IsNullOrWhiteSpace(arguments[currentIndex].Name))
            {
                continue;
            }

            return currentIndex;
        }

        return -1;
    }

    private static ResolvedInvokeArgument[] OrderArguments(IEnumerable<ResolvedInvokeArgument> arguments)
    {
        ResolvedInvokeArgument[] materialized = arguments.ToArray();
        return materialized
            .Where(argument => string.IsNullOrWhiteSpace(argument.Name))
            .Concat(materialized.Where(argument => !string.IsNullOrWhiteSpace(argument.Name)))
            .ToArray();
    }

    private static ParameterModifier CreateParameterModifier(IReadOnlyList<ResolvedInvokeArgument> arguments)
    {
        ParameterModifier modifier = new(arguments.Count);
        for (int index = 0; index < arguments.Count; index++)
        {
            modifier[index] = arguments[index].ByRef;
        }

        return modifier;
    }

    private static Dictionary<string, object?> CreateCaptureMap(
        IReadOnlyList<ResolvedInvokeArgument> arguments,
        IReadOnlyList<object?> invocationArguments)
    {
        Dictionary<string, object?> captures = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < arguments.Count; index++)
        {
            ResolvedInvokeArgument argument = arguments[index];
            if (!argument.ByRef && string.IsNullOrWhiteSpace(argument.CaptureAs))
            {
                continue;
            }

            string key = argument.CaptureAs
                ?? argument.Name
                ?? $"arg{index}";
            captures[key] = invocationArguments[index];
        }

        return captures;
    }

    private static Dictionary<string, object?> CreateCaptureMap(
        IEnumerable<(ResolvedInvokeArgument Argument, int ParameterIndex)> bindings,
        IReadOnlyList<object?> invocationArguments)
    {
        Dictionary<string, object?> captures = new(StringComparer.OrdinalIgnoreCase);
        foreach ((ResolvedInvokeArgument argument, int parameterIndex) in bindings)
        {
            if (!argument.ByRef && string.IsNullOrWhiteSpace(argument.CaptureAs))
            {
                continue;
            }

            string key = argument.CaptureAs
                ?? argument.Name
                ?? $"arg{parameterIndex}";
            captures[key] = invocationArguments[parameterIndex];
        }

        return captures;
    }

    private static object? CoerceValue(object? value, Type targetType)
    {
        return TryCoerceValue(value, targetType, out object? coercedValue)
            ? coercedValue
            : value;
    }

    private static bool TryCoerceValue(object? value, Type targetType, out object? coercedValue)
    {
        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
        {
            coercedValue = null;
            return !effectiveType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }

        object? candidateValue = value;
        if (candidateValue is IReflectiveInvocationValueAdapter adapter)
        {
            candidateValue = adapter.GetInvocationValue();
        }

        if (candidateValue is JsonNode jsonNode)
        {
            if (effectiveType.IsInstanceOfType(jsonNode))
            {
                coercedValue = jsonNode;
                return true;
            }

            if (jsonNode is JsonValue jsonScalar)
            {
                if (jsonScalar.TryGetValue(out string? text))
                {
                    candidateValue = text;
                }
                else if (jsonScalar.TryGetValue(out bool boolValue))
                {
                    candidateValue = boolValue;
                }
                else if (jsonScalar.TryGetValue(out int intValue))
                {
                    candidateValue = intValue;
                }
                else if (jsonScalar.TryGetValue(out long longValue))
                {
                    candidateValue = longValue;
                }
                else if (jsonScalar.TryGetValue(out double doubleValue))
                {
                    candidateValue = doubleValue;
                }
                else if (jsonScalar.TryGetValue(out decimal decimalValue))
                {
                    candidateValue = decimalValue;
                }
                else
                {
                    candidateValue = jsonScalar.ToString();
                }
            }
            else
            {
                candidateValue = jsonNode;
            }

            if (ReferenceEquals(candidateValue, jsonNode))
            {
                coercedValue = null;
                return false;
            }
        }

        if (candidateValue is null)
        {
            coercedValue = null;
            return !effectiveType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;
        }

        if (effectiveType.IsInstanceOfType(candidateValue))
        {
            coercedValue = candidateValue;
            return true;
        }

        try
        {
            if (effectiveType.IsEnum)
            {
                coercedValue = candidateValue is string stringValue
                    ? Enum.Parse(effectiveType, stringValue, ignoreCase: true)
                    : Enum.ToObject(effectiveType, candidateValue);
                return true;
            }

            if (effectiveType == typeof(Guid))
            {
                coercedValue = Guid.Parse(candidateValue.ToString() ?? string.Empty);
                return true;
            }

            coercedValue = Convert.ChangeType(candidateValue, effectiveType, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            coercedValue = null;
            return false;
        }
    }

    private static bool TryGetDictionaryValue(object target, string member, out object? value)
    {
        if (target is IDictionary<string, object?> genericDictionary)
        {
            if (genericDictionary.TryGetValue(member, out value))
            {
                return true;
            }

            string? matchedKey = genericDictionary.Keys.FirstOrDefault(
                key => string.Equals(key, member, StringComparison.OrdinalIgnoreCase));
            if (matchedKey is not null)
            {
                value = genericDictionary[matchedKey];
                return true;
            }
        }

        if (target is IDictionary dictionary)
        {
            foreach (object key in dictionary.Keys)
            {
                if (string.Equals(key.ToString(), member, StringComparison.OrdinalIgnoreCase))
                {
                    value = dictionary[key];
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static bool TrySetDictionaryValue(object target, string member, object? value)
    {
        if (target is IDictionary<string, object?> genericDictionary)
        {
            string? matchedKey = genericDictionary.Keys.FirstOrDefault(
                key => string.Equals(key, member, StringComparison.OrdinalIgnoreCase));
            genericDictionary[matchedKey ?? member] = value;
            return true;
        }

        if (target is IDictionary dictionary)
        {
            object? matchedKey = dictionary.Keys.Cast<object>()
                .FirstOrDefault(key => string.Equals(key.ToString(), member, StringComparison.OrdinalIgnoreCase));
            dictionary[matchedKey ?? member] = value;
            return true;
        }

        return false;
    }

    private static bool TryIndexDictionary(object target, object?[] arguments, out object? value)
    {
        object? key = arguments.SingleOrDefault();
        if (key is null)
        {
            value = null;
            return false;
        }

        if (target is IDictionary<string, object?> genericDictionary)
        {
            string textKey = key.ToString() ?? string.Empty;
            if (genericDictionary.TryGetValue(textKey, out value))
            {
                return true;
            }

            string? matchedKey = genericDictionary.Keys.FirstOrDefault(
                candidate => string.Equals(candidate, textKey, StringComparison.OrdinalIgnoreCase));
            if (matchedKey is not null)
            {
                value = genericDictionary[matchedKey];
                return true;
            }
        }

        if (target is IDictionary dictionary && dictionary.Contains(key))
        {
            value = dictionary[key];
            return true;
        }

        value = null;
        return false;
    }
}


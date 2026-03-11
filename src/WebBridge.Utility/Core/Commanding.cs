using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

public sealed class CommandPlanCompiler : ICommandPlanCompiler
{
    private readonly IAdapterInvokeSurfaceRegistry _surfaceRegistry;
    private readonly IRuntimeConfigurationManager _configurationManager;
    private readonly ConcurrentDictionary<string, PreparedCommandPlan> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _observedGeneration = -1;

    public CommandPlanCompiler(
        IAdapterInvokeSurfaceRegistry surfaceRegistry,
        IRuntimeConfigurationManager configurationManager)
    {
        _surfaceRegistry = surfaceRegistry;
        _configurationManager = configurationManager;
    }

    public PreparedCommandPlan Compile(ProfileDefinition profile, CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(definition);

        long generation = _configurationManager.GetGeneration();
        if (generation != Interlocked.Read(ref _observedGeneration))
        {
            _cache.Clear();
            Interlocked.Exchange(ref _observedGeneration, generation);
        }

        string cacheKey = $"{generation}:{profile.ProfileId}:{profile.Checksum ?? "no-checksum"}:{definition.CommandId}";
        return _cache.GetOrAdd(cacheKey, _ =>
        {
            if (definition.Invoke is null)
            {
                throw new InvalidOperationException($"Command '{definition.CommandId}' does not define invoke metadata.");
            }

            if (!_surfaceRegistry.TryGetSurface(definition.Adapter, out IAdapterInvokeSurface? surface))
            {
                throw new InvalidOperationException($"Adapter invoke surface '{definition.Adapter}' is not registered.");
            }

            return surface?.Compile(definition)
                ?? throw new InvalidOperationException($"Adapter invoke surface '{definition.Adapter}' returned null.");
        });
    }
}

public sealed class CommandDispatcher : ICommandDispatcher
{
    private const string SharedContextIdArgument = "__sharedContextId";
    private const string ReportVerbosityArgument = "__reportVerbosity";
    private const string TimeoutMillisecondsArgument = "__timeoutMilliseconds";
    private readonly IProfileStore _profileStore;
    private readonly ICommandPlanCompiler _compiler;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        IProfileStore profileStore,
        ICommandPlanCompiler compiler,
        ILogger<CommandDispatcher> logger)
    {
        _profileStore = profileStore;
        _compiler = compiler;
        _logger = logger;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(ExecuteCommandRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteSingleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecuteBatchCommandResponse> ExecuteBatchAsync(ExecuteBatchCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string batchId = Guid.NewGuid().ToString("N");
        string? sharedContextId = string.IsNullOrWhiteSpace(request.SharedContextId)
            ? $"batch-{batchId}"
            : request.SharedContextId;
        List<ExecuteCommandResponse> results = new(request.Commands.Count);
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;

        foreach (ExecuteCommandRequest command in request.Commands)
        {
            ExecuteCommandRequest effectiveRequest = command with
            {
                SharedContextId = command.SharedContextId ?? sharedContextId,
                ReportVerbosity = command.ReportVerbosity ?? request.ReportVerbosity,
            };

            CommandExecutionResult execution = await ExecuteSingleAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
            results.Add(new ExecuteCommandResponse(
                execution.ExecutionId,
                execution.Success,
                execution.Result,
                execution.Error,
                execution.Report));

            if (!execution.Success && request.StopOnError)
            {
                break;
            }
        }

        int successCount = results.Count(result => result.Success);
        int failureCount = results.Count - successCount;
        JsonObject report = new()
        {
            ["batchId"] = batchId,
            ["sharedContextId"] = sharedContextId,
            ["startedAtUtc"] = startedAtUtc,
            ["completedAtUtc"] = DateTimeOffset.UtcNow,
            ["stopOnError"] = request.StopOnError,
            ["requestedCommandCount"] = request.Commands.Count,
            ["completedCommandCount"] = results.Count,
            ["successCount"] = successCount,
            ["failureCount"] = failureCount,
            ["reportVerbosity"] = (request.ReportVerbosity ?? ReportVerbosity.Full).ToString(),
        };

        return new ExecuteBatchCommandResponse(
            batchId,
            failureCount == 0 && results.Count == request.Commands.Count,
            request.Commands.Count,
            successCount,
            failureCount,
            results,
            report);
    }

    private async Task<CommandExecutionResult> ExecuteSingleAsync(ExecuteCommandRequest request, CancellationToken cancellationToken)
    {
        CoreLog.DispatchingCommand(_logger, request.ProfileId, request.CommandId);
        ProfileDefinition? profile = await _profileStore.GetProfileAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            CoreLog.ProfileNotFound(_logger, request.ProfileId, request.CommandId);
            return CommandExecutionResult.Fail("profile_not_found", $"Profile '{request.ProfileId}' was not found.");
        }

        if (!profile.Commands.TryGetValue(request.CommandId, out CommandDefinition? definition))
        {
            CoreLog.CommandNotFound(_logger, request.CommandId, request.ProfileId);
            return CommandExecutionResult.Fail("command_not_found", $"Command '{request.CommandId}' was not found in profile '{request.ProfileId}'.");
        }

        JsonObject arguments = JsonObjectMerge.Merge(definition.DefaultArguments, request.Arguments);
        ApplyExecutionArguments(arguments, request);

        if (definition.Invoke is null)
        {
            return CommandExecutionResult.Fail("command_invalid", $"Command '{definition.CommandId}' does not define invoke metadata.");
        }

        PreparedCommandPlan preparedPlan;
        try
        {
            preparedPlan = _compiler.Compile(profile, definition);
        }
        catch (Exception exception)
        {
            CoreLog.CommandCompileFailed(_logger, exception, request.ProfileId, request.CommandId);
            return CommandExecutionResult.Fail("command_compile_failed", exception.Message);
        }

        using CancellationTokenSource? timeoutCts = CreateTimeoutScope(request.TimeoutMilliseconds, cancellationToken);
        CancellationToken effectiveCancellationToken = timeoutCts?.Token ?? cancellationToken;
        CommandExecutionResult result = await preparedPlan.ExecuteAsync(arguments, effectiveCancellationToken).ConfigureAwait(false);
        result = EnrichReport(result, request, definition, arguments, mode: "invoke");
        CoreLog.InvokeFinished(_logger, request.ProfileId, request.CommandId, result.Success);
        return result;
    }

    private static CommandExecutionResult EnrichReport(
        CommandExecutionResult result,
        ExecuteCommandRequest request,
        CommandDefinition definition,
        JsonObject effectiveArguments,
        string mode)
    {
        JsonObject report = result.Report?.DeepClone()?.AsObject() ?? new JsonObject();
        ReportVerbosity verbosity = ResolveReportVerbosity(request, report);
        report["mode"] ??= mode;
        report["profileId"] ??= request.ProfileId;
        report["commandId"] ??= request.CommandId;
        report["adapter"] ??= definition.Adapter;
        report["success"] = result.Success;
        report["executionId"] = result.ExecutionId;
        report["reportVerbosity"] ??= verbosity.ToString();
        report["sharedContextId"] ??= request.SharedContextId;
        if (verbosity == ReportVerbosity.Full)
        {
            report["effectiveArguments"] = JsonObjectMerge.Clone(effectiveArguments);
            if (result.Result is not null)
            {
                report["result"] = result.Result.DeepClone();
            }
        }
        else
        {
            report["effectiveArgumentKeys"] = new JsonArray(effectiveArguments.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
        }

        if (result.Error is not null)
        {
            report["error"] = new JsonObject
            {
                ["code"] = result.Error.Code,
                ["message"] = result.Error.Message,
                ["details"] = result.Error.Details?.DeepClone(),
            };
        }

        return result.WithReport(report);
    }

    private static ReportVerbosity ResolveReportVerbosity(ExecuteCommandRequest request, JsonObject report)
    {
        if (request.ReportVerbosity is not null)
        {
            return request.ReportVerbosity.Value;
        }

        string? rawValue = report["reportVerbosity"]?.GetValue<string>();
        if (Enum.TryParse(rawValue, ignoreCase: true, out ReportVerbosity parsed))
        {
            return parsed;
        }

        return ReportVerbosity.Full;
    }

    private static void ApplyExecutionArguments(JsonObject arguments, ExecuteCommandRequest request)
    {
        if (request.ReportVerbosity is not null)
        {
            arguments[ReportVerbosityArgument] = request.ReportVerbosity.ToString();
        }

        if (!string.IsNullOrWhiteSpace(request.SharedContextId))
        {
            arguments[SharedContextIdArgument] = request.SharedContextId;
        }

        if (request.TimeoutMilliseconds is not null)
        {
            arguments[TimeoutMillisecondsArgument] = request.TimeoutMilliseconds.Value;
        }
    }

    private static CancellationTokenSource? CreateTimeoutScope(int? timeoutMilliseconds, CancellationToken cancellationToken)
    {
        if (timeoutMilliseconds is null || timeoutMilliseconds <= 0)
        {
            return null;
        }

        CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds.Value);
        return timeoutSource;
    }
}

internal static class JsonObjectMerge
{
    public static JsonObject Merge(JsonObject? defaults, JsonObject? overrides)
    {
        JsonObject merged = Clone(defaults);
        if (overrides is null)
        {
            return merged;
        }

        foreach ((string key, JsonNode? value) in overrides)
        {
            merged[key] = value?.DeepClone();
        }

        return merged;
    }

    public static JsonObject Clone(JsonObject? source)
    {
        if (source is null)
        {
            return new JsonObject();
        }

        JsonNode? clone = JsonNode.Parse(source.ToJsonString());
        return clone?.AsObject() ?? new JsonObject();
    }
}


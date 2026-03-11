using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WebBridge.Utility.Adapters.Com;

namespace WebBridge.Utility.Protocol;

public static class ProtocolConstants
{
    public const string PairingTokenHeader = "X-KWB-Pairing-Token";
    public const string CorrelationIdHeader = "X-Correlation-Id";
    public const string SessionWebSocketPath = "/ws/session";
    public const string SessionRegisterPath = "/session/register";
    public const string DefaultUtilityVersion = "1.0.1";
    public const string DefaultListenUrl = "http://127.0.0.1:38741";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenUiMode
{
    Auto,
    Always,
    Never,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShutdownPolicy
{
    WhenIdle,
    Manual,
    Never,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UtilityRuntimeState
{
    Starting,
    WaitingForSession,
    Active,
    IdleCountdown,
    Stopping,
    Stopped,
}

 [JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReportVerbosity
{
    Full,
    Compact,
}

public sealed record ApiError(string Code, string Message, JsonObject? Details = null);

public sealed record ApiEnvelope<TPayload>(
    string Id,
    string Type,
    DateTimeOffset Ts,
    string? CorrelationId,
    TPayload? Payload,
    ApiError? Error);

public sealed record WsEnvelope<TPayload>(
    string Id,
    string Type,
    DateTimeOffset Ts,
    string? CorrelationId,
    TPayload? Payload,
    ApiError? Error);

[JsonConverter(typeof(UtilitySettingsJsonConverter))]
public sealed class UtilitySettings
{
    public UtilityVersionsOptions Versions { get; set; } = new();
    public UtilityMetadata Metadata { get; set; } = new();
    public UtilityRuntimeOptions Runtime { get; set; } = new();
    public UtilityServerOptions Server { get; set; } = new();
    public UtilityUiOptions Ui { get; set; } = new();
    public UtilityLifecycleOptions Lifecycle { get; set; } = new();
    public UtilityLoggingOptions Logging { get; set; } = new();
    public UtilityStorageOptions Storage { get; set; } = new();
    public UtilityCatalogOptions Catalog { get; set; } = new();
    public UtilityAdapterOptions Adapters { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public SessionSettings Session { get; set; } = new();

    [JsonIgnore]
    public string UtilityVersion { get => Versions.UtilityVersion; set => Versions.UtilityVersion = value; }

    [JsonIgnore]
    public string ConfigVersion { get => Versions.ConfigVersion; set => Versions.ConfigVersion = value; }

    [JsonIgnore]
    public int ConfigSchemaVersion { get => Versions.ConfigSchemaVersion; set => Versions.ConfigSchemaVersion = value; }

    [JsonIgnore]
    public string EnvironmentName { get => Runtime.EnvironmentName; set => Runtime.EnvironmentName = value; }

    [JsonIgnore]
    public string ListenUrl { get => Server.ListenUrl; set => Server.ListenUrl = value; }

    [JsonIgnore]
    public string? UiUrl { get => Ui.Url; set => Ui.Url = value; }

    [JsonIgnore]
    public string? ManifestUrl { get => Catalog.Url; set => Catalog.Url = value; }

    [JsonIgnore]
    public OpenUiMode OpenUi { get => Ui.OpenMode; set => Ui.OpenMode = value; }

    [JsonIgnore]
    public ShutdownPolicy Shutdown { get => Lifecycle.ShutdownPolicy; set => Lifecycle.ShutdownPolicy = value; }

    [JsonIgnore]
    public int IdleSeconds { get => Lifecycle.IdleSeconds; set => Lifecycle.IdleSeconds = value; }

    [JsonIgnore]
    public int SessionWaitSeconds { get => Ui.SessionWaitSeconds; set => Ui.SessionWaitSeconds = value; }

    [JsonIgnore]
    public string LogLevel { get => Logging.Level; set => Logging.Level = value; }

    [JsonIgnore]
    public bool DebugMode { get => Logging.DebugMode; set => Logging.DebugMode = value; }

    [JsonIgnore]
    public string? LogFilePath { get => Logging.FilePath; set => Logging.FilePath = value; }

    [JsonIgnore]
    public string? ConfigPath { get; set; }

    [JsonIgnore]
    public string? ProfileDirectory { get => Storage.ProfileDirectory; set => Storage.ProfileDirectory = value; }

    [JsonIgnore]
    public string? CacheDirectory { get => Storage.CacheDirectory; set => Storage.CacheDirectory = value; }

    [JsonIgnore]
    public string? DiagnosticsDirectory { get => Storage.DiagnosticsDirectory; set => Storage.DiagnosticsDirectory = value; }

    [JsonIgnore]
    public bool DevMode { get => Runtime.DevMode; set => Runtime.DevMode = value; }

    [JsonIgnore]
    public bool NoBrowser { get => Runtime.NoBrowser; set => Runtime.NoBrowser = value; }

    [JsonIgnore]
    public Manifest? Manifest { get => Catalog.Manifest; set => Catalog.Manifest = value; }

    [JsonIgnore]
    public List<ProfileDefinition> Profiles { get => Catalog.Profiles; set => Catalog.Profiles = value; }

    [JsonIgnore]
    public List<ComInvokeDescriptor> ComAdapters { get => Adapters.Com; set => Adapters.Com = value; }

    [JsonIgnore]
    public SystemAdapterSettings SystemAdapter { get => Adapters.System; set => Adapters.System = value; }

    public UtilitySettings Clone()
    {
        EnsureInitialized();

        return new UtilitySettings
        {
            Versions = Versions.Clone(),
            Metadata = Metadata.Clone(),
            Runtime = Runtime.Clone(),
            Server = Server.Clone(),
            Ui = Ui.Clone(),
            Lifecycle = Lifecycle.Clone(),
            Logging = Logging.Clone(),
            Storage = Storage.Clone(),
            Catalog = Catalog.Clone(),
            Adapters = Adapters.Clone(),
            ConfigPath = ConfigPath,
            Security = Security.Clone(),
            Session = Session.Clone(),
        };
    }

    public void ApplyFrom(UtilitySettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureInitialized();
        source.EnsureInitialized();

        Versions = source.Versions.Clone();
        Metadata = source.Metadata.Clone();
        Runtime = source.Runtime.Clone();
        Server = source.Server.Clone();
        Ui = source.Ui.Clone();
        Lifecycle = source.Lifecycle.Clone();
        Logging = source.Logging.Clone();
        Storage = source.Storage.Clone();
        Catalog = source.Catalog.Clone();
        Adapters = source.Adapters.Clone();
        ConfigPath = source.ConfigPath;
        Security = source.Security.Clone();
        Session = source.Session.Clone();
    }

    private void EnsureInitialized()
    {
        Versions ??= new UtilityVersionsOptions();
        Metadata ??= new UtilityMetadata();
        Runtime ??= new UtilityRuntimeOptions();
        Server ??= new UtilityServerOptions();
        Ui ??= new UtilityUiOptions();
        Lifecycle ??= new UtilityLifecycleOptions();
        Logging ??= new UtilityLoggingOptions();
        Storage ??= new UtilityStorageOptions();
        Catalog ??= new UtilityCatalogOptions();
        Adapters ??= new UtilityAdapterOptions();
        Security ??= new SecuritySettings();
        Session ??= new SessionSettings();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityVersionsOptions
{
    public string UtilityVersion { get; set; } = ProtocolConstants.DefaultUtilityVersion;
    public string ConfigVersion { get; set; } = "1.0.1";
    public int ConfigSchemaVersion { get; set; } = 2;

    public UtilityVersionsOptions Clone()
    {
        return new UtilityVersionsOptions
        {
            UtilityVersion = UtilityVersion,
            ConfigVersion = ConfigVersion,
            ConfigSchemaVersion = ConfigSchemaVersion,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityRuntimeOptions
{
    public string EnvironmentName { get; set; } = "Production";
    public bool DevMode { get; set; }
    public bool NoBrowser { get; set; }

    public UtilityRuntimeOptions Clone()
    {
        return new UtilityRuntimeOptions
        {
            EnvironmentName = EnvironmentName,
            DevMode = DevMode,
            NoBrowser = NoBrowser,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityServerOptions
{
    public string ListenUrl { get; set; } = ProtocolConstants.DefaultListenUrl;

    public UtilityServerOptions Clone()
    {
        return new UtilityServerOptions
        {
            ListenUrl = ListenUrl,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityUiOptions
{
    public string? Url { get; set; }
    public OpenUiMode OpenMode { get; set; } = OpenUiMode.Auto;
    public int SessionWaitSeconds { get; set; } = 5;

    public UtilityUiOptions Clone()
    {
        return new UtilityUiOptions
        {
            Url = Url,
            OpenMode = OpenMode,
            SessionWaitSeconds = SessionWaitSeconds,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityLifecycleOptions
{
    public ShutdownPolicy ShutdownPolicy { get; set; } = ShutdownPolicy.WhenIdle;
    public int IdleSeconds { get; set; } = 120;

    public UtilityLifecycleOptions Clone()
    {
        return new UtilityLifecycleOptions
        {
            ShutdownPolicy = ShutdownPolicy,
            IdleSeconds = IdleSeconds,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityLoggingOptions
{
    public string Level { get; set; } = "Information";
    public bool DebugMode { get; set; }
    public string? FilePath { get; set; }

    public UtilityLoggingOptions Clone()
    {
        return new UtilityLoggingOptions
        {
            Level = Level,
            DebugMode = DebugMode,
            FilePath = FilePath,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityStorageOptions
{
    public string? ProfileDirectory { get; set; }
    public string? CacheDirectory { get; set; }
    public string? DiagnosticsDirectory { get; set; }

    public UtilityStorageOptions Clone()
    {
        return new UtilityStorageOptions
        {
            ProfileDirectory = ProfileDirectory,
            CacheDirectory = CacheDirectory,
            DiagnosticsDirectory = DiagnosticsDirectory,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityCatalogOptions
{
    public string? Url { get; set; }
    public Manifest? Manifest { get; set; }
    public List<ProfileDefinition> Profiles { get; set; } = new();

    public UtilityCatalogOptions Clone()
    {
        return new UtilityCatalogOptions
        {
            Url = Url,
            Manifest = Manifest?.Clone(),
            Profiles = Profiles.Select(profile => profile.Clone()).ToList(),
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityAdapterOptions
{
    public List<ComInvokeDescriptor> Com { get; set; } = new();
    public SystemAdapterSettings System { get; set; } = new();

    public UtilityAdapterOptions Clone()
    {
        return new UtilityAdapterOptions
        {
            Com = Com.Select(adapter => adapter.Clone()).ToList(),
            System = System.Clone(),
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UtilityMetadata
{
    public string ProductName { get; set; } = "WebBridge.Utility";
    public string ProductCode { get; set; } = "WebBridge.Utility";
    public string Author { get; set; } = "Гороховицкий Егор Русланович";
    public string? Company { get; set; }
    public string Description { get; set; } = "Локальная headless-утилита для связи веб-страницы с Excel, KOMPAS-3D и системными API.";
    public string? RepositoryUrl { get; set; } = "https://github.com/Efnatii/web-bridge-utility";

    public UtilityMetadata Clone()
    {
        return new UtilityMetadata
        {
            ProductName = ProductName,
            ProductCode = ProductCode,
            Author = Author,
            Company = Company,
            Description = Description,
            RepositoryUrl = RepositoryUrl,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SecuritySettings
{
    public bool LoopbackOnly { get; set; } = true;
    public string PairingToken { get; set; } = "change-me-local-token";
    public List<string> AllowedOrigins { get; set; } = new();

    public SecuritySettings Clone()
    {
        return new SecuritySettings
        {
            LoopbackOnly = LoopbackOnly,
            PairingToken = PairingToken,
            AllowedOrigins = AllowedOrigins.ToList(),
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SessionSettings
{
    public int HeartbeatIntervalSeconds { get; set; } = 10;
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public int PresenceTimeoutSeconds { get; set; } = 60;
    public bool SuppressAutoOpenOnPresenceSessions { get; set; } = true;
    public int SweepIntervalSeconds { get; set; } = 2;

    public SessionSettings Clone()
    {
        return new SessionSettings
        {
            HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
            HeartbeatTimeoutSeconds = HeartbeatTimeoutSeconds,
            PresenceTimeoutSeconds = PresenceTimeoutSeconds,
            SuppressAutoOpenOnPresenceSessions = SuppressAutoOpenOnPresenceSessions,
            SweepIntervalSeconds = SweepIntervalSeconds,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SystemAdapterSettings
{
    public List<string> RootAliases { get; set; } = new();

    public List<string> AllowedRoots { get; set; } = new();

    public List<SystemSurfaceBinding> Members { get; set; } = new();

    public List<string> AllowedTypeNames { get; set; } = new();

    public List<string> DeniedTypeNames { get; set; } = new();

    public List<string> AllowedNamespaces { get; set; } = new();

    public List<string> DeniedNamespaces { get; set; } = new();

    public List<string> DeniedInvocations { get; set; } = new();

    public List<string> AllowedProcessExecutables { get; set; } = new();

    public List<string> DeniedProcessExecutables { get; set; } = new();

    public List<string> AllowedUrlPrefixes { get; set; } = new();

    public List<string> DeniedUrlPrefixes { get; set; } = new();

    public List<string> AllowedRegistryHives { get; set; } = new();

    public List<string> DeniedRegistryHives { get; set; } = new();

    public SystemAdapterSettings Clone()
    {
        return new SystemAdapterSettings
        {
            RootAliases = RootAliases.ToList(),
            AllowedRoots = AllowedRoots.ToList(),
            Members = Members.Select(member => member.Clone()).ToList(),
            AllowedTypeNames = AllowedTypeNames.ToList(),
            DeniedTypeNames = DeniedTypeNames.ToList(),
            AllowedNamespaces = AllowedNamespaces.ToList(),
            DeniedNamespaces = DeniedNamespaces.ToList(),
            DeniedInvocations = DeniedInvocations.ToList(),
            AllowedProcessExecutables = AllowedProcessExecutables.ToList(),
            DeniedProcessExecutables = DeniedProcessExecutables.ToList(),
            AllowedUrlPrefixes = AllowedUrlPrefixes.ToList(),
            DeniedUrlPrefixes = DeniedUrlPrefixes.ToList(),
            AllowedRegistryHives = AllowedRegistryHives.ToList(),
            DeniedRegistryHives = DeniedRegistryHives.ToList(),
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SystemSurfaceBinding
{
    public string Name { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    public string? TypeName { get; set; }

    public SystemSurfaceBinding Clone()
    {
        return new SystemSurfaceBinding
        {
            Name = Name,
            Target = Target,
            TypeName = TypeName,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Manifest
{
    public int ConfigSchemaVersion { get; set; } = 1;
    public string ManifestVersion { get; set; } = "1.0.1";
    public string MinUtilityVersion { get; set; } = "1.0.1";
    public string? RecommendedUtilityVersion { get; set; }
    public string? UiVersion { get; set; }
    public string? Checksum { get; set; }
    public List<ManifestProfileReference> Profiles { get; set; } = new();

    public Manifest Clone()
    {
        return new Manifest
        {
            ConfigSchemaVersion = ConfigSchemaVersion,
            ManifestVersion = ManifestVersion,
            MinUtilityVersion = MinUtilityVersion,
            RecommendedUtilityVersion = RecommendedUtilityVersion,
            UiVersion = UiVersion,
            Checksum = Checksum,
            Profiles = Profiles.Select(profile => profile.Clone()).ToList(),
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ManifestProfileReference
{
    public string ProfileId { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? RelativePath { get; set; }
    public string? Checksum { get; set; }
    public string? LocalFileName { get; set; }

    public ManifestProfileReference Clone()
    {
        return new ManifestProfileReference
        {
            ProfileId = ProfileId,
            Url = Url,
            RelativePath = RelativePath,
            Checksum = Checksum,
            LocalFileName = LocalFileName,
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileDefinition
{
    public string ProfileId { get; set; } = string.Empty;
    public int ConfigSchemaVersion { get; set; } = 1;
    public string? Description { get; set; }
    public string? Checksum { get; set; }
    public Dictionary<string, CommandDefinition> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ProfileDefinition Clone()
    {
        return new ProfileDefinition
        {
            ProfileId = ProfileId,
            ConfigSchemaVersion = ConfigSchemaVersion,
            Description = Description,
            Checksum = Checksum,
            Commands = Commands.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase),
        };
    }
}

public sealed class CommandDefinition
{
    public string CommandId { get; set; } = string.Empty;
    public string Adapter { get; set; } = string.Empty;
    public InvokeDefinition? Invoke { get; set; }
    public JsonObject? DefaultArguments { get; set; }

    public CommandDefinition Clone()
    {
        return new CommandDefinition
        {
            CommandId = CommandId,
            Adapter = Adapter,
            Invoke = Invoke?.Clone(),
            DefaultArguments = DefaultArguments?.DeepClone()?.AsObject(),
        };
    }
}

public sealed class InvokeDefinition
{
    public string Root { get; set; } = string.Empty;
    public List<InvokeStepDefinition> Chain { get; set; } = new();
    public string? ReturnPath { get; set; }
    public Dictionary<string, string> EnumMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Converters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ErrorMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public InvokeDefinition Clone()
    {
        return new InvokeDefinition
        {
            Root = Root,
            Chain = Chain.Select(step => step.Clone()).ToList(),
            ReturnPath = ReturnPath,
            EnumMap = new Dictionary<string, string>(EnumMap, StringComparer.OrdinalIgnoreCase),
            Converters = new Dictionary<string, string>(Converters, StringComparer.OrdinalIgnoreCase),
            ErrorMap = new Dictionary<string, string>(ErrorMap, StringComparer.OrdinalIgnoreCase),
        };
    }
}

public sealed class InvokeStepDefinition
{
    public string Operation { get; set; } = string.Empty;
    public string Member { get; set; } = string.Empty;
    public List<InvokeArgumentDefinition> Args { get; set; } = new();
    public string? ValueArgument { get; set; }
    public string? StoreAs { get; set; }

    public InvokeStepDefinition Clone()
    {
        return new InvokeStepDefinition
        {
            Operation = Operation,
            Member = Member,
            Args = Args.Select(argument => argument.Clone()).ToList(),
            ValueArgument = ValueArgument,
            StoreAs = StoreAs,
        };
    }
}

public sealed class InvokeArgumentDefinition
{
    public string? FromArgument { get; set; }
    public string? FromStored { get; set; }
    public JsonNode? Literal { get; set; }
    public string? Converter { get; set; }
    public string? Name { get; set; }
    public bool ByRef { get; set; }
    public string? CaptureAs { get; set; }

    public InvokeArgumentDefinition Clone()
    {
        return new InvokeArgumentDefinition
        {
            FromArgument = FromArgument,
            FromStored = FromStored,
            Literal = Literal?.DeepClone(),
            Converter = Converter,
            Name = Name,
            ByRef = ByRef,
            CaptureAs = CaptureAs,
        };
    }
}

public sealed record RegisterSessionRequest(string ClientName, string UiVersion, string? DesiredSessionId);

public sealed record RegisterSessionResponse(
    string SessionId,
    string WsUrl,
    int HeartbeatIntervalSeconds,
    UtilityRuntimeState RuntimeState,
    string UtilityVersion,
    int ActiveSessionCount,
    int PresenceSessionCount);

public sealed record ClosingSessionRequest(string SessionId, string? Reason);

public sealed record ExecuteCommandRequest(
    string ProfileId,
    string CommandId,
    JsonObject Arguments,
    ReportVerbosity? ReportVerbosity = null,
    string? SharedContextId = null,
    int? TimeoutMilliseconds = null);

public sealed record ExecuteCommandResponse(
    string ExecutionId,
    bool Success,
    JsonNode? Result,
    ApiError? Error,
    JsonObject? Report);

public sealed record ExecuteBatchCommandRequest(
    IReadOnlyList<ExecuteCommandRequest> Commands,
    string? SharedContextId = null,
    ReportVerbosity? ReportVerbosity = null,
    bool StopOnError = true);

public sealed record ExecuteBatchCommandResponse(
    string BatchId,
    bool Success,
    int CommandCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<ExecuteCommandResponse> Results,
    JsonObject? Report);

public sealed record HealthResponse(
    string Status,
    UtilityRuntimeState RuntimeState,
    DateTimeOffset UtcNow,
    int ActiveSessionCount,
    int PresenceSessionCount);

public sealed record ManifestStatusResponse(
    string Source,
    bool UsingCache,
    string? ManifestVersion,
    string? Checksum,
    DateTimeOffset? LastUpdatedUtc,
    ApiError? LastError);

public sealed record EffectiveConfigResponse(UtilitySettings Settings);

public sealed record ProfileVersionInfo(string ProfileId, string? Checksum, int CommandCount);

public sealed record ConfigVersionResponse(
    string ConfigVersion,
    int ConfigSchemaVersion,
    string UtilityVersion,
    string? ManifestVersion,
    string? ManifestChecksum,
    int ProfileCount,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyCollection<ProfileVersionInfo> Profiles);

public sealed record LoadConfigRequest(UtilitySettings Settings, bool Persist);

public sealed record ConfigUpdateResponse(
    bool Applied,
    bool Persisted,
    bool RestartRequired,
    string Message,
    ConfigVersionResponse Version);

public sealed record InfoResponse(
    UtilityMetadata Metadata,
    string UtilityVersion,
    UtilityRuntimeState RuntimeState,
    string ListenUrl,
    string? UiUrl,
    string EnvironmentName,
    int ActiveSessionCount,
    int PresenceSessionCount,
    int ActivationCount,
    DateTimeOffset StartedAtUtc);

public sealed record ActivationRequest(
    string[] Args,
    DateTimeOffset RequestedAtUtc,
    bool RequestBrowserOpen,
    bool RequestManifestRefresh,
    bool RequestShutdown);

public sealed record UtilityControlRequest(string? Reason, bool Force);

public sealed record UtilityControlResponse(
    string Action,
    bool Accepted,
    string Message,
    UtilityRuntimeState RuntimeState,
    int ActiveSessionCount);

public sealed class CommandExecutionResult
{
    public string ExecutionId { get; init; } = Guid.NewGuid().ToString("N");
    public bool Success { get; init; }
    public JsonNode? Result { get; init; }
    public ApiError? Error { get; init; }
    public JsonObject? Report { get; init; }

    public static CommandExecutionResult Ok(JsonNode? result, JsonObject? report = null)
    {
        return new CommandExecutionResult
        {
            Success = true,
            Result = result,
            Report = report,
        };
    }

    public static CommandExecutionResult Fail(string code, string message, JsonObject? details = null, JsonObject? report = null)
    {
        return new CommandExecutionResult
        {
            Success = false,
            Error = new ApiError(code, message, details),
            Report = report,
        };
    }

    public CommandExecutionResult WithReport(JsonObject? report)
    {
        return new CommandExecutionResult
        {
            ExecutionId = ExecutionId,
            Success = Success,
            Result = Result?.DeepClone(),
            Error = Error is null
                ? null
                : new ApiError(Error.Code, Error.Message, Error.Details?.DeepClone()?.AsObject()),
            Report = report?.DeepClone()?.AsObject(),
        };
    }
}

public static class ProtocolEnvelope
{
    public static ApiEnvelope<TPayload> Api<TPayload>(string type, TPayload? payload, string? correlationId = null, ApiError? error = null)
    {
        return new ApiEnvelope<TPayload>(
            Guid.NewGuid().ToString("N"),
            type,
            DateTimeOffset.UtcNow,
            correlationId,
            payload,
            error);
    }

    public static WsEnvelope<TPayload> Ws<TPayload>(string type, TPayload? payload, string? correlationId = null, ApiError? error = null)
    {
        return new WsEnvelope<TPayload>(
            Guid.NewGuid().ToString("N"),
            type,
            DateTimeOffset.UtcNow,
            correlationId,
            payload,
            error);
    }
}


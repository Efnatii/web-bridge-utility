using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Configuration;

namespace WebBridge.Utility;

public sealed class UtilityCommandLineOptions
{
    public string[] RawArgs { get; init; } = Array.Empty<string>();
    public string? ConfigPath { get; init; }
    public bool Shutdown { get; init; }
    public bool PrintEffectiveConfig { get; init; }
    public bool HealthcheckOnly { get; init; }
    public bool Version { get; init; }
}

public sealed record UtilityCliParseResult(bool Success, UtilityCommandLineOptions? Options, IReadOnlyCollection<string> Errors);

public static class UtilityCli
{
    private static readonly JsonSerializerOptions EffectiveSettingsSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ConfigFileSerializerOptions = new(JsonSerializerDefaults.Web);

    public static UtilityCliParseResult Parse(string[] args)
    {
        Option<string?> configOption = new(["--config", "-c"]);
        Option<bool> shutdownOption = new("--shutdown");
        Option<bool> printEffectiveConfigOption = new("--print-effective-config");
        Option<bool> healthcheckOnlyOption = new("--healthcheck-only");
        Option<bool> versionOption = new("--version");

        RootCommand rootCommand = new("WebBridge.Utility");
        rootCommand.AddOption(configOption);
        rootCommand.AddOption(shutdownOption);
        rootCommand.AddOption(printEffectiveConfigOption);
        rootCommand.AddOption(healthcheckOnlyOption);
        rootCommand.AddOption(versionOption);

        ParseResult parseResult = rootCommand.Parse(args);
        List<string> errors = parseResult.Errors.Select(error => error.Message).ToList();
        UtilityCommandLineOptions options = new()
        {
            RawArgs = args,
            ConfigPath = parseResult.GetValueForOption(configOption),
            Shutdown = parseResult.GetValueForOption(shutdownOption),
            PrintEffectiveConfig = parseResult.GetValueForOption(printEffectiveConfigOption),
            HealthcheckOnly = parseResult.GetValueForOption(healthcheckOnlyOption),
            Version = parseResult.GetValueForOption(versionOption),
        };

        errors.AddRange(Validate(options));
        return new UtilityCliParseResult(errors.Count == 0, options, errors);
    }

    public static IConfigurationRoot BuildConfiguration(UtilityCommandLineOptions options)
    {
        ConfigurationBuilder builder = new();

        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            builder.AddJsonFile(Path.GetFullPath(options.ConfigPath), optional: false, reloadOnChange: false);
        }

        builder.AddEnvironmentVariables("KWB_");
        return builder.Build();
    }

    public static UtilitySettings BuildEffectiveSettings(UtilityCommandLineOptions options, IConfiguration configuration)
    {
        UtilitySettings? fileSettings = LoadStructuredConfig(options.ConfigPath);
        UtilitySettings settings = fileSettings?.Clone() ?? configuration.Get<UtilitySettings>() ?? new UtilitySettings();
        NormalizeEffectiveSettings(settings, options.ConfigPath);
        return settings;
    }

    public static UtilitySettings NormalizeEffectiveSettings(UtilitySettings settings, string? configPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.ConfigPath = !string.IsNullOrWhiteSpace(configPath)
            ? Path.GetFullPath(configPath)
            : settings.ConfigPath;
        settings.Versions ??= new UtilityVersionsOptions();
        settings.Metadata ??= new UtilityMetadata();
        settings.Runtime ??= new UtilityRuntimeOptions();
        settings.Server ??= new UtilityServerOptions();
        settings.Ui ??= new UtilityUiOptions();
        settings.Lifecycle ??= new UtilityLifecycleOptions();
        settings.Logging ??= new UtilityLoggingOptions();
        settings.Storage ??= new UtilityStorageOptions();
        settings.Catalog ??= new UtilityCatalogOptions();
        settings.Adapters ??= new UtilityAdapterOptions();
        settings.Security ??= new SecuritySettings();
        settings.Session ??= new SessionSettings();
        if (settings.SystemAdapter.Members.Count == 0)
        {
            settings.SystemAdapter.Members.AddRange(
            [
                new SystemSurfaceBinding { Name = "process", Target = "process" },
                new SystemSurfaceBinding { Name = "command", Target = "command" },
                new SystemSurfaceBinding { Name = "http", Target = "http" },
                new SystemSurfaceBinding { Name = "registry", Target = "registry" },
                new SystemSurfaceBinding { Name = "zip", Target = "zip" },
                new SystemSurfaceBinding { Name = "hash", Target = "hash" },
                new SystemSurfaceBinding { Name = "drive", Target = "drive" },
            ]);
        }

        settings.SystemAdapter.DeniedNamespaces = MergeDefaults(
            settings.SystemAdapter.DeniedNamespaces,
            ["System.Reflection", "System.Runtime.InteropServices"]);
        settings.SystemAdapter.DeniedTypeNames = MergeDefaults(
            settings.SystemAdapter.DeniedTypeNames,
            ["System.Type", "System.Activator", "System.AppDomain"]);

        settings.EnvironmentName = string.IsNullOrWhiteSpace(settings.EnvironmentName)
            ? "Production"
            : settings.EnvironmentName;
        settings.UiUrl ??= settings.DevMode ? "http://127.0.0.1:5510/" : null;

        string baseDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebBridge.Utility");
        settings.CacheDirectory ??= Path.Combine(baseDataDirectory, "cache");
        settings.ProfileDirectory ??= Path.Combine(baseDataDirectory, "profiles");
        settings.DiagnosticsDirectory ??= Path.Combine(baseDataDirectory, "diagnostics");
        settings.CacheDirectory = ResolveConfiguredPath(settings.CacheDirectory, settings.ConfigPath);
        settings.ProfileDirectory = ResolveConfiguredPath(settings.ProfileDirectory, settings.ConfigPath);
        settings.DiagnosticsDirectory = ResolveConfiguredPath(settings.DiagnosticsDirectory, settings.ConfigPath);
        foreach (ComInvokeDescriptor adapter in settings.ComAdapters)
        {
            adapter.InteropAssemblies = adapter.InteropAssemblies
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => ResolveConfiguredPath(path, settings.ConfigPath) ?? path)
                .ToList();
        }

        settings.LogFilePath = ResolveLogFilePath(settings);
        return settings;
    }

    public static string SerializeEffectiveSettings(UtilitySettings settings)
    {
        return JsonSerializer.Serialize(
            UtilitySettingsSanitizer.Redact(settings),
            EffectiveSettingsSerializerOptions);
    }

    private static List<string> Validate(UtilityCommandLineOptions options)
    {
        List<string> errors = new();
        bool requiresConfig = !options.Version && !options.Shutdown;

        if (requiresConfig && string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            errors.Add("Обычный запуск агента требует обязательный аргумент --config <path>.");
        }

        if (!string.IsNullOrWhiteSpace(options.ConfigPath) && !File.Exists(options.ConfigPath))
        {
            errors.Add($"--config file was not found: {options.ConfigPath}");
        }

        return errors;
    }

    private static string? ResolveLogFilePath(UtilitySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LogFilePath))
        {
            return ResolveConfiguredPath(settings.LogFilePath, settings.ConfigPath);
        }

        if (!settings.DebugMode || string.IsNullOrWhiteSpace(settings.DiagnosticsDirectory))
        {
            return null;
        }

        return Path.Combine(Path.GetFullPath(settings.DiagnosticsDirectory), "utility-debug.log");
    }

    private static string? ResolveConfiguredPath(string? path, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string baseDirectory = !string.IsNullOrWhiteSpace(configPath)
            ? Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    public static UtilitySettings? LoadStructuredConfig(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<UtilitySettings>(json, ConfigFileSerializerOptions);
    }

    private static List<string> MergeDefaults(IEnumerable<string> configured, IEnumerable<string> defaults)
    {
        return configured
            .Concat(defaults)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}


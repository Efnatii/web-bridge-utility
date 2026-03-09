using System.Text.Json;
using WebBridge.Utility;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

public sealed class ConfigCompatibilityTests
{
    private static readonly JsonSerializerOptions WebJsonSerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LoadStructuredConfig_rejects_legacy_flat_fields()
    {
        using TemporaryDirectory temp = new();
        string configPath = temp.WriteText(
            "legacy.flat.json",
            """
            {
              "UtilityVersion": "1.2.3",
              "ConfigVersion": "legacy-flat",
              "ConfigSchemaVersion": 1,
              "EnvironmentName": "Legacy",
              "ListenUrl": "http://127.0.0.1:4100",
              "UiUrl": "https://example.test/ui"
            }
            """);

        Assert.Throws<JsonException>(() => UtilityCli.LoadStructuredConfig(configPath));
    }

    [Fact]
    public void LoadConfigRequest_rejects_legacy_flat_settings_payload()
    {
        const string payload =
            """
            {
              "persist": false,
              "settings": {
                "configVersion": "runtime-legacy",
                "EnvironmentName": "RuntimeLegacy",
                "UiUrl": "https://example.test/runtime"
              }
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoadConfigRequest>(payload, WebJsonSerializerOptions));
    }

    [Fact]
    public void BuildEffectiveSettings_reads_restructured_production_sample()
    {
        string configPath = TestPaths.RepoFile("configs", "config.production.sample.json");
        UtilityCommandLineOptions options = new() { ConfigPath = configPath };

        UtilitySettings settings = UtilityCli.BuildEffectiveSettings(options, UtilityCli.BuildConfiguration(options));

        Assert.Equal("1.0.0", settings.UtilityVersion);
        Assert.Equal("1.0.0-production-bootstrap", settings.ConfigVersion);
        Assert.Equal(2, settings.ConfigSchemaVersion);
        Assert.Equal("Production", settings.EnvironmentName);
        Assert.Equal("http://127.0.0.1:38741", settings.ListenUrl);
        Assert.Equal("https://efnatii.github.io/web-bridge-utility/", settings.UiUrl);
        Assert.Equal(OpenUiMode.Auto, settings.OpenUi);
        Assert.Equal(ShutdownPolicy.WhenIdle, settings.Shutdown);
        Assert.Empty(settings.Catalog.Profiles);
        Assert.Empty(settings.Adapters.Com);
        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, "local", "diagnostics")),
            settings.DiagnosticsDirectory);
    }

    [Fact]
    public void BuildEffectiveSettings_reads_restructured_development_sample()
    {
        string configPath = TestPaths.RepoFile("configs", "config.development.sample.json");
        UtilityCommandLineOptions options = new() { ConfigPath = configPath };

        UtilitySettings settings = UtilityCli.BuildEffectiveSettings(options, UtilityCli.BuildConfiguration(options));

        Assert.Equal("1.0.0-dev", settings.ConfigVersion);
        Assert.Equal(2, settings.ConfigSchemaVersion);
        Assert.Equal("Development", settings.EnvironmentName);
        Assert.True(settings.DevMode);
        Assert.Equal("http://127.0.0.1:5510/", settings.UiUrl);
        Assert.Equal("Debug", settings.LogLevel);
        Assert.True(settings.DebugMode);
        Assert.NotNull(settings.Catalog.Manifest);
        Assert.Single(settings.Catalog.Profiles);
        Assert.Equal(2, settings.Adapters.Com.Count);
        Assert.Equal(7, settings.Adapters.System.Members.Count);
        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, "local", "profiles")),
            settings.ProfileDirectory);
        Assert.Equal(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, "local", "cache")),
            settings.CacheDirectory);
    }

    [Fact]
    public void NormalizeEffectiveSettings_populates_defaults_for_debug_runtime()
    {
        using TemporaryDirectory temp = new();
        string configPath = temp.WriteText("config.json", "{ }");
        UtilitySettings settings = new()
        {
            Runtime = new UtilityRuntimeOptions { DevMode = true },
            Logging = new UtilityLoggingOptions { DebugMode = true },
        };

        UtilityCli.NormalizeEffectiveSettings(settings, configPath);

        Assert.Equal(2, settings.ConfigSchemaVersion);
        Assert.Equal("Production", settings.EnvironmentName);
        Assert.Equal("http://127.0.0.1:5510/", settings.UiUrl);
        Assert.NotNull(settings.CacheDirectory);
        Assert.NotNull(settings.ProfileDirectory);
        Assert.NotNull(settings.DiagnosticsDirectory);
        Assert.True(System.IO.Path.IsPathRooted(settings.CacheDirectory));
        Assert.True(System.IO.Path.IsPathRooted(settings.ProfileDirectory));
        Assert.True(System.IO.Path.IsPathRooted(settings.DiagnosticsDirectory));
        Assert.Equal("utility-debug.log", System.IO.Path.GetFileName(settings.LogFilePath));
        Assert.True(System.IO.Path.IsPathRooted(settings.LogFilePath));
    }
}

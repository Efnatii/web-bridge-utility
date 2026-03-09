using System.Text.Json;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

public sealed class RuntimeConfigurationManagerTests
{
    [Fact]
    public async Task ApplyAsync_persists_versions_catalog_and_adapters_blocks()
    {
        using TemporaryDirectory temp = new();
        string configPath = temp.WriteText(
            "runtime.config.json",
            """
            {
              "Versions": {
                "UtilityVersion": "1.0.0",
                "ConfigVersion": "nested-start",
                "ConfigSchemaVersion": 2
              },
              "Runtime": {
                "EnvironmentName": "Nested"
              },
              "Server": {
                "ListenUrl": "http://127.0.0.1:4100"
              },
              "Ui": {
                "Url": "https://example.test/start",
                "OpenMode": "Auto"
              },
              "Catalog": {
                "Profiles": []
              },
              "Adapters": {
                "Com": [],
                "System": {}
              },
              "Security": {
                "PairingToken": "start-token"
              }
            }
            """);

        UtilitySettings settings = TestSettings.LoadAndNormalize(configPath);
        RuntimeConfigurationManager manager = TestSettings.CreateRuntimeConfigurationManager(settings);

        UtilitySettings next = settings.Clone();
        next.ConfigVersion = "nested-saved";
        next.UiUrl = "https://example.test/next";
        next.Security.PairingToken = "next-token";
        next.Catalog.Profiles.Add(new ProfileDefinition
        {
            ProfileId = "runtime",
            ConfigSchemaVersion = 1,
            Commands = new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase),
        });

        ConfigUpdateResponse response = await manager.ApplyAsync(
            new LoadConfigRequest(next, Persist: true),
            CancellationToken.None);

        Assert.True(response.Applied);
        Assert.True(response.Persisted);
        Assert.False(response.RestartRequired);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        JsonElement root = document.RootElement;

        Assert.True(root.TryGetProperty("versions", out JsonElement versions));
        Assert.Equal("nested-saved", versions.GetProperty("configVersion").GetString());
        Assert.Equal(2, versions.GetProperty("configSchemaVersion").GetInt32());

        Assert.True(root.TryGetProperty("catalog", out JsonElement catalog));
        Assert.Equal(1, catalog.GetProperty("profiles").GetArrayLength());

        Assert.True(root.TryGetProperty("adapters", out JsonElement adapters));
        Assert.True(adapters.TryGetProperty("com", out JsonElement comAdapters));
        Assert.Equal(0, comAdapters.GetArrayLength());
        Assert.True(adapters.TryGetProperty("system", out _));

        Assert.False(root.TryGetProperty("ConfigVersion", out _));
        Assert.False(root.TryGetProperty("Profiles", out _));
        Assert.False(root.TryGetProperty("ComAdapters", out _));
        Assert.False(root.TryGetProperty("SystemAdapter", out _));
        Assert.Equal("next-token", root.GetProperty("security").GetProperty("pairingToken").GetString());
    }

    [Fact]
    public async Task ReloadFromDiskAsync_reloads_restructured_config_from_disk()
    {
        using TemporaryDirectory temp = new();
        string configPath = temp.WriteText(
            "runtime.reload.json",
            """
            {
              "Versions": {
                "UtilityVersion": "1.0.0",
                "ConfigVersion": "reload-start",
                "ConfigSchemaVersion": 2
              },
              "Runtime": {
                "EnvironmentName": "Start"
              },
              "Server": {
                "ListenUrl": "http://127.0.0.1:4200"
              },
              "Ui": {
                "Url": "https://example.test/start"
              },
              "Logging": {
                "Level": "Information"
              },
              "Catalog": {
                "Profiles": []
              },
              "Adapters": {
                "Com": [],
                "System": {}
              },
              "Security": {
                "PairingToken": "start-token"
              }
            }
            """);

        UtilitySettings settings = TestSettings.LoadAndNormalize(configPath);
        RuntimeConfigurationManager manager = TestSettings.CreateRuntimeConfigurationManager(settings);

        temp.WriteText(
            "runtime.reload.json",
            """
            {
              "Versions": {
                "UtilityVersion": "1.0.0",
                "ConfigVersion": "reload-next",
                "ConfigSchemaVersion": 2
              },
              "Runtime": {
                "EnvironmentName": "Reloaded"
              },
              "Server": {
                "ListenUrl": "http://127.0.0.1:4200"
              },
              "Ui": {
                "Url": "https://example.test/reloaded"
              },
              "Logging": {
                "Level": "Debug"
              },
              "Catalog": {
                "Profiles": []
              },
              "Adapters": {
                "Com": [],
                "System": {}
              },
              "Security": {
                "PairingToken": "reloaded-token"
              }
            }
            """);

        ConfigUpdateResponse response = await manager.ReloadFromDiskAsync(CancellationToken.None);
        UtilitySettings snapshot = manager.GetSettingsSnapshot();

        Assert.True(response.Applied);
        Assert.False(response.Persisted);
        Assert.True(response.RestartRequired);
        Assert.Equal("reload-next", snapshot.ConfigVersion);
        Assert.Equal("Reloaded", snapshot.EnvironmentName);
        Assert.Equal("https://example.test/reloaded", snapshot.UiUrl);
        Assert.Equal("Debug", snapshot.LogLevel);
        Assert.Equal("reloaded-token", snapshot.Security.PairingToken);
    }
}

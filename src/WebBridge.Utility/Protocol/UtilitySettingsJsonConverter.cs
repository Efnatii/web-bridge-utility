using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WebBridge.Utility.Protocol;

public sealed class UtilitySettingsJsonConverter : JsonConverter<UtilitySettings>
{
    private static readonly string[] AllowedTopLevelProperties =
    [
        nameof(UtilitySettings.Versions),
        nameof(UtilitySettings.Metadata),
        nameof(UtilitySettings.Runtime),
        nameof(UtilitySettings.Server),
        nameof(UtilitySettings.Ui),
        nameof(UtilitySettings.Lifecycle),
        nameof(UtilitySettings.Logging),
        nameof(UtilitySettings.Storage),
        nameof(UtilitySettings.Catalog),
        nameof(UtilitySettings.Adapters),
        nameof(UtilitySettings.Security),
        nameof(UtilitySettings.Session),
    ];

    public override UtilitySettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonNode? node = JsonNode.Parse(ref reader);
        JsonObject root = node as JsonObject ?? throw new JsonException("UtilitySettings JSON payload must be an object.");

        foreach ((string key, _) in root)
        {
            if (!AllowedTopLevelProperties.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
            {
                throw new JsonException($"Unsupported config field '{key}'. Use the nested schema blocks only.");
            }
        }

        return new UtilitySettings
        {
            Versions = DeserializeOrDefault(root, nameof(UtilitySettings.Versions), new UtilityVersionsOptions(), options),
            Metadata = DeserializeOrDefault(root, nameof(UtilitySettings.Metadata), new UtilityMetadata(), options),
            Runtime = DeserializeOrDefault(root, nameof(UtilitySettings.Runtime), new UtilityRuntimeOptions(), options),
            Server = DeserializeOrDefault(root, nameof(UtilitySettings.Server), new UtilityServerOptions(), options),
            Ui = DeserializeOrDefault(root, nameof(UtilitySettings.Ui), new UtilityUiOptions(), options),
            Lifecycle = DeserializeOrDefault(root, nameof(UtilitySettings.Lifecycle), new UtilityLifecycleOptions(), options),
            Logging = DeserializeOrDefault(root, nameof(UtilitySettings.Logging), new UtilityLoggingOptions(), options),
            Storage = DeserializeOrDefault(root, nameof(UtilitySettings.Storage), new UtilityStorageOptions(), options),
            Catalog = DeserializeOrDefault(root, nameof(UtilitySettings.Catalog), new UtilityCatalogOptions(), options),
            Adapters = DeserializeOrDefault(root, nameof(UtilitySettings.Adapters), new UtilityAdapterOptions(), options),
            Security = DeserializeOrDefault(root, nameof(UtilitySettings.Security), new SecuritySettings(), options),
            Session = DeserializeOrDefault(root, nameof(UtilitySettings.Session), new SessionSettings(), options),
        };
    }

    public override void Write(Utf8JsonWriter writer, UtilitySettings value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(
            writer,
            new
            {
                value.Versions,
                value.Metadata,
                value.Runtime,
                value.Server,
                value.Ui,
                value.Lifecycle,
                value.Logging,
                value.Storage,
                value.Catalog,
                value.Adapters,
                value.Security,
                value.Session,
            },
            options);
    }

    private static T DeserializeOrDefault<T>(JsonObject root, string propertyName, T fallback, JsonSerializerOptions options)
    {
        return TryGetProperty(root, propertyName, out JsonNode? value) && value is not null
            ? value.Deserialize<T>(options) ?? fallback
            : fallback;
    }

    private static bool TryGetProperty(JsonObject node, string propertyName, out JsonNode? value)
    {
        foreach ((string key, JsonNode? child) in node)
        {
            if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = child;
                return true;
            }
        }

        value = null;
        return false;
    }
}

using System.Text.Json;
using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Core;

public sealed class ManifestService : IManifestService
{
    private readonly HttpClient _httpClient;
    private readonly UtilitySettings _settings;
    private readonly IVersionCompatibilityService _compatibilityService;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _cacheDirectory;
    private readonly string _manifestCachePath;
    private readonly ILogger<ManifestService> _logger;
    private ManifestStatusSnapshot _status = new("uninitialized", false, null, null, null, null);

    public ManifestService(
        HttpClient httpClient,
        UtilitySettings settings,
        IVersionCompatibilityService compatibilityService,
        ILogger<ManifestService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _compatibilityService = compatibilityService;
        _logger = logger;
        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        _cacheDirectory = settings.CacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebBridge.Utility",
            "cache");
        _manifestCachePath = Path.Combine(_cacheDirectory, "manifest.json");

        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            _status = settings.Manifest is not null
                ? CreateEmbeddedStatus(null)
                : File.Exists(_manifestCachePath)
                ? new ManifestStatusSnapshot("cache", true, null, null, File.GetLastWriteTimeUtc(_manifestCachePath), null)
                : new ManifestStatusSnapshot("none", false, null, null, null, null);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_settings.Manifest is not null && string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            _status = CreateEmbeddedStatus(null);
            CoreLog.ManifestInitializedEmbedded(_logger);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            _status = await TryLoadCachedManifestAsync(null, cancellationToken).ConfigureAwait(false);
            CoreLog.ManifestInitializedWithoutRemote(_logger, _status.Source);
            return;
        }

        _ = await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManifestRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl))
        {
            _status = _settings.Manifest is not null
                ? CreateEmbeddedStatus(null)
                : new ManifestStatusSnapshot("local-only", false, null, null, null, null);
            CoreLog.ManifestRefreshSkipped(_logger, _status.Source);
            return new ManifestRefreshResult(true, _status);
        }

        try
        {
            CoreLog.ManifestRefreshStarting(_logger, _settings.ManifestUrl);
            string manifestJson = await _httpClient.GetStringAsync(_settings.ManifestUrl, cancellationToken).ConfigureAwait(false);
            Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, _serializerOptions)
                ?? throw new InvalidOperationException("Manifest payload is empty.");

            CompatibilityEvaluation compatibility = _compatibilityService.Evaluate(manifest, _settings.UtilityVersion);
            if (!compatibility.IsCompatible)
            {
                CoreLog.ManifestIncompatible(_logger, _settings.UtilityVersion);
                _status = await TryLoadLocalManifestFallbackAsync(
                    new ApiError("manifest_incompatible", compatibility.Error ?? "Manifest is incompatible."),
                    cancellationToken).ConfigureAwait(false);
                return new ManifestRefreshResult(false, _status);
            }

            _ = Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(_manifestCachePath, manifestJson, cancellationToken).ConfigureAwait(false);
            await DownloadProfilesAsync(manifest, cancellationToken).ConfigureAwait(false);

            _status = new ManifestStatusSnapshot(
                "remote",
                false,
                manifest.ManifestVersion,
                manifest.Checksum,
                DateTimeOffset.UtcNow,
                compatibility.Warning is null ? null : new ApiError("recommended_utility_version", compatibility.Warning));

            CoreLog.ManifestRefreshCompleted(_logger, manifest.ManifestVersion);
            return new ManifestRefreshResult(true, _status);
        }
        catch (Exception exception)
        {
            CoreLog.ManifestRefreshFailed(_logger, exception);
            _status = await TryLoadLocalManifestFallbackAsync(
                new ApiError("manifest_refresh_failed", exception.Message),
                cancellationToken).ConfigureAwait(false);
            return new ManifestRefreshResult(false, _status);
        }
    }

    public ManifestStatusSnapshot GetStatus() => _status;

    private async Task DownloadProfilesAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Profiles.Count == 0)
        {
            return;
        }

        string profileCacheDirectory = Path.Combine(_cacheDirectory, "profiles");
        _ = Directory.CreateDirectory(profileCacheDirectory);

        foreach (ManifestProfileReference profileReference in manifest.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profileReference.Url))
            {
                continue;
            }

            string profileJson = await _httpClient.GetStringAsync(profileReference.Url, cancellationToken).ConfigureAwait(false);
            string fileName = $"{profileReference.ProfileId}.json";
            string outputPath = Path.Combine(profileCacheDirectory, fileName);
            await File.WriteAllTextAsync(outputPath, profileJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ManifestStatusSnapshot> TryLoadCachedManifestAsync(ApiError? fallbackError, CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestCachePath))
        {
            return new ManifestStatusSnapshot("none", false, null, null, null, fallbackError);
        }

        string manifestJson = await File.ReadAllTextAsync(_manifestCachePath, cancellationToken).ConfigureAwait(false);
        Manifest? manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, _serializerOptions);
        return new ManifestStatusSnapshot(
            "cache",
            true,
            manifest?.ManifestVersion,
            manifest?.Checksum,
            File.GetLastWriteTimeUtc(_manifestCachePath),
            fallbackError);
    }

    private async Task<ManifestStatusSnapshot> TryLoadLocalManifestFallbackAsync(ApiError? fallbackError, CancellationToken cancellationToken)
    {
        if (_settings.Manifest is not null)
        {
            return CreateEmbeddedStatus(fallbackError);
        }

        return await TryLoadCachedManifestAsync(fallbackError, cancellationToken).ConfigureAwait(false);
    }

    private ManifestStatusSnapshot CreateEmbeddedStatus(ApiError? fallbackError)
    {
        Manifest manifest = _settings.Manifest!;
        return new ManifestStatusSnapshot(
            "embedded",
            false,
            manifest.ManifestVersion,
            manifest.Checksum,
            null,
            fallbackError);
    }
}

public sealed class ProfileStore : IProfileStore
{
    private readonly UtilitySettings _settings;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public ProfileStore(UtilitySettings settings)
    {
        _settings = settings;
    }

    public async Task<ProfileDefinition?> GetProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        Dictionary<string, ProfileDefinition> embeddedProfiles = GetEmbeddedProfiles();
        if (embeddedProfiles.TryGetValue(profileId, out ProfileDefinition? embeddedProfile))
        {
            return embeddedProfile.Clone();
        }

        foreach (string directory in GetCandidateDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string path = Path.Combine(directory, $"{profileId}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ProfileDefinition>(json, _serializerOptions);
        }

        return null;
    }

    public async Task<IReadOnlyCollection<ProfileDefinition>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        List<ProfileDefinition> profiles = GetEmbeddedProfiles().Values.Select(profile => profile.Clone()).ToList();
        foreach (string directory in GetCandidateDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                ProfileDefinition? profile = JsonSerializer.Deserialize<ProfileDefinition>(json, _serializerOptions);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
        }

        return profiles
            .GroupBy(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private Dictionary<string, ProfileDefinition> GetEmbeddedProfiles()
    {
        return _settings.Profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.ProfileId))
            .ToDictionary(profile => profile.ProfileId, profile => profile.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetCandidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ProfileDirectory))
        {
            yield return _settings.ProfileDirectory;
        }

        string cacheDirectory = _settings.CacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebBridge.Utility",
            "cache");
        yield return Path.Combine(cacheDirectory, "profiles");
    }
}


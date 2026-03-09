using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using WebBridge.Utility;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Xunit;

namespace WebBridge.Utility.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WebBridge.Utility.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string GetPath(params string[] segments)
    {
        string current = Path;
        foreach (string segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
        }

        return current;
    }

    public string WriteText(string relativePath, string contents)
    {
        string fullPath = GetPath(relativePath);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class TestPaths
{
    private static readonly Lazy<string> RepoRootPath = new(FindRepoRoot);

    public static string RepoRoot => RepoRootPath.Value;

    public static string RepoFile(params string[] segments)
    {
        string current = RepoRoot;
        foreach (string segment in segments)
        {
            current = System.IO.Path.Combine(current, segment);
        }

        return current;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "WebBridge.Utility.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("WebBridge.Utility.sln was not found from the test output directory.");
    }
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IUtilityClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        UtcNow = UtcNow.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class FakeManifestService : IManifestService
{
    private ManifestStatusSnapshot _status = new(
        Source: "test",
        UsingCache: false,
        ManifestVersion: null,
        Checksum: null,
        LastUpdatedUtc: null,
        LastError: null);

    public int InitializeCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task<ManifestRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new ManifestRefreshResult(true, _status));
    }

    public ManifestStatusSnapshot GetStatus() => _status;
}

internal static class TestSettings
{
    public static UtilitySettings LoadAndNormalize(string configPath)
    {
        UtilitySettings? settings = UtilityCli.LoadStructuredConfig(configPath);
        Assert.NotNull(settings);
        return UtilityCli.NormalizeEffectiveSettings(settings!, configPath);
    }

    public static RuntimeConfigurationManager CreateRuntimeConfigurationManager(
        UtilitySettings settings,
        FakeManifestService? manifestService = null,
        FakeClock? clock = null)
    {
        manifestService ??= new FakeManifestService();
        clock ??= new FakeClock(DateTimeOffset.Parse("2026-03-10T00:00:00+00:00", CultureInfo.InvariantCulture));
        return new RuntimeConfigurationManager(
            settings,
            manifestService,
            clock,
            NullLogger<RuntimeConfigurationManager>.Instance);
    }
}

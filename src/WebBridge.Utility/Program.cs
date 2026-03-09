using System.Threading.Channels;
using WebBridge.Utility.Core;
using WebBridge.Utility.Infrastructure;
using WebBridge.Utility.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace WebBridge.Utility;

public static partial class Program
{
    private const string InstanceName = "Local\\WebBridge.Utility";

    public static async Task<int> Main(string[] args)
    {
        UtilityCliParseResult parseResult = UtilityCli.Parse(args);
        if (!parseResult.Success || parseResult.Options is null)
        {
            ConsoleRuntimeInfo.WriteBanner(new UtilitySettings(), configPath: null, mode: "Ошибка запуска");
            foreach (string error in parseResult.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        UtilityCommandLineOptions cliOptions = parseResult.Options;

        if (cliOptions.Shutdown)
        {
            ConsoleRuntimeInfo.WriteBanner(new UtilitySettings(), cliOptions.ConfigPath, "shutdown");
            return await SendShutdownSignalAsync(cliOptions).ConfigureAwait(false);
        }

        IConfigurationRoot configuration = UtilityCli.BuildConfiguration(cliOptions);
        UtilitySettings settings = UtilityCli.BuildEffectiveSettings(cliOptions, configuration);
        ConsoleRuntimeInfo.WriteBanner(settings, settings.ConfigPath, ResolveMode(cliOptions));

        if (cliOptions.Version)
        {
            Console.WriteLine($"Версия: {settings.UtilityVersion}");
            return 0;
        }

        if (cliOptions.PrintEffectiveConfig)
        {
            Console.WriteLine(UtilityCli.SerializeEffectiveSettings(settings));
            return 0;
        }

        if (cliOptions.HealthcheckOnly)
        {
            return await RunHealthCheckAsync(settings).ConfigureAwait(false);
        }

        Directory.CreateDirectory(settings.CacheDirectory!);
        Directory.CreateDirectory(settings.ProfileDirectory!);
        Directory.CreateDirectory(settings.DiagnosticsDirectory!);
        if (!string.IsNullOrWhiteSpace(settings.LogFilePath))
        {
            string? logDirectory = Path.GetDirectoryName(settings.LogFilePath);
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
        }

        Channel<ActivationRequest> activationChannel = Channel.CreateUnbounded<ActivationRequest>();
        await using NamedPipeSingleInstanceCoordinator coordinator = new();
        SingleInstanceInitializationResult initialization = await coordinator.InitializeAsync(
            InstanceName,
            request => activationChannel.Writer.WriteAsync(request).AsTask(),
            CancellationToken.None).ConfigureAwait(false);

        if (!initialization.IsPrimary)
        {
            bool sent = await coordinator.SendActivationAsync(
                InstanceName,
                new ActivationRequest(
                    cliOptions.RawArgs,
                    DateTimeOffset.UtcNow,
                    settings.OpenUi is not OpenUiMode.Never && !settings.NoBrowser,
                    !string.IsNullOrWhiteSpace(settings.ManifestUrl),
                    RequestShutdown: false),
                CancellationToken.None).ConfigureAwait(false);
            ConsoleRuntimeInfo.WriteMessage(sent
                ? "Команда передана уже работающему экземпляру."
                : "Не удалось передать команду работающему экземпляру.");
            return sent ? 0 : 3;
        }

        WebApplication app = UtilityApp.BuildWebApplication(settings, activationChannel);
        UtilityLog.UtilityStarted(app.Logger, settings.ListenUrl, settings.DebugMode, settings.LogFilePath, settings.ConfigPath);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static string ResolveMode(UtilityCommandLineOptions options)
    {
        if (options.Version)
        {
            return "version";
        }

        if (options.PrintEffectiveConfig)
        {
            return "print-effective-config";
        }

        if (options.HealthcheckOnly)
        {
            return "healthcheck-only";
        }

        if (options.Shutdown)
        {
            return "shutdown";
        }

        return "utility";
    }

    private static async Task<int> RunHealthCheckAsync(UtilitySettings settings)
    {
        using HttpClient client = new();
        try
        {
            HttpResponseMessage response = await client.GetAsync(new Uri(new Uri(settings.ListenUrl), "/health")).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task<int> SendShutdownSignalAsync(UtilityCommandLineOptions options)
    {
        await using NamedPipeSingleInstanceCoordinator coordinator = new();
        bool sent = await coordinator.SendActivationAsync(
            InstanceName,
            new ActivationRequest(
                options.RawArgs,
                DateTimeOffset.UtcNow,
                RequestBrowserOpen: false,
                RequestManifestRefresh: false,
                RequestShutdown: true),
            CancellationToken.None).ConfigureAwait(false);
        return sent ? 0 : 3;
    }
}


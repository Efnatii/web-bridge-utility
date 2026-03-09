using WebBridge.Utility.Infrastructure;
using WebBridge.Utility.Protocol;
using Microsoft.Extensions.Logging;

namespace WebBridge.Utility;

internal static class UtilityLogging
{
    public static void Configure(ILoggingBuilder logging, UtilitySettings settings)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(ParseLogLevel(settings.LogLevel));

        if (settings.DebugMode)
        {
            logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = false;
            });
        }

        if (!string.IsNullOrWhiteSpace(settings.LogFilePath))
        {
            logging.AddUtilityFileLogger(settings.LogFilePath);
        }
    }

    private static LogLevel ParseLogLevel(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out LogLevel logLevel)
            ? logLevel
            : LogLevel.Information;
    }
}


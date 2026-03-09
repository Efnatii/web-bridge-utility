using Microsoft.Extensions.Logging;

namespace WebBridge.Utility.Infrastructure;

public static class UtilityFileLoggingExtensions
{
    public static ILoggingBuilder AddUtilityFileLogger(this ILoggingBuilder builder, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return builder.AddProvider(new UtilityFileLoggerProvider(Path.GetFullPath(filePath)));
    }
}

internal sealed class UtilityFileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public UtilityFileLoggerProvider(string filePath)
    {
        _filePath = filePath;
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new UtilityFileLogger(categoryName, _filePath, _sync);
    }

    public void Dispose()
    {
    }
}

internal sealed class UtilityFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _filePath;
    private readonly object _sync;

    public UtilityFileLogger(string categoryName, string filePath, object sync)
    {
        _categoryName = categoryName;
        _filePath = filePath;
        _sync = sync;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        string line = $"{DateTimeOffset.UtcNow:O} [{logLevel}] {_categoryName}";
        if (eventId.Id != 0)
        {
            line += $" ({eventId.Id})";
        }

        line += $": {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}


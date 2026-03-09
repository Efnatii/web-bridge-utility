using System.Runtime.InteropServices;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility;

internal static class ConsoleRuntimeInfo
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static bool _initialized;
    private static bool _available;

    public static void WriteBanner(UtilitySettings settings, string? configPath, string? mode)
    {
        EnsureConsole();

        try
        {
            Console.WriteLine($"{settings.Metadata.ProductName} {settings.UtilityVersion}");
            Console.WriteLine($"Автор: {settings.Metadata.Author}");
            if (!string.IsNullOrWhiteSpace(settings.Metadata.Description))
            {
                Console.WriteLine(settings.Metadata.Description);
            }

            if (!string.IsNullOrWhiteSpace(mode))
            {
                Console.WriteLine($"Режим: {mode}");
            }

            if (!string.IsNullOrWhiteSpace(configPath))
            {
                Console.WriteLine($"Config: {configPath}");
            }

            Console.WriteLine();
        }
        catch
        {
        }
    }

    public static void WriteMessage(string message)
    {
        EnsureConsole();

        try
        {
            Console.WriteLine(message);
        }
        catch
        {
        }
    }

    private static bool EnsureConsole()
    {
        if (_initialized)
        {
            return _available;
        }

        _initialized = true;
        if (Console.IsOutputRedirected || Console.IsErrorRedirected || GetConsoleWindow() != IntPtr.Zero)
        {
            _available = true;
            return true;
        }

        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                RebindStandardStreams();
            }
        }
        catch
        {
        }

        _available = true;
        return true;
    }

    private static void RebindStandardStreams()
    {
        try
        {
            StreamWriter stdout = new(Console.OpenStandardOutput()) { AutoFlush = true };
            StreamWriter stderr = new(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch
        {
            _available = false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}


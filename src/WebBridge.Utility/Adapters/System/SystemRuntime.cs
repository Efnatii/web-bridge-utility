using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;
using Microsoft.Win32;

namespace WebBridge.Utility.Adapters.SystemAccess;

#pragma warning disable CA1416
#pragma warning disable CA1822
#pragma warning disable CA5350
#pragma warning disable CA5351

public sealed class SystemRuntime : IReflectiveInvokeRuntime, IPathArgumentNormalizer, IDisposable
{
    private readonly UtilitySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly SystemInvocationPolicy _policy;
    private readonly Dictionary<string, object> _controlledRoots;

    public SystemRuntime(UtilitySettings settings)
    {
        _settings = settings;
        HttpClientHandler handler = new()
        {
            UseProxy = false,
        };
        _httpClient = new HttpClient(handler, disposeHandler: true);
        _policy = new SystemInvocationPolicy(settings);
        _controlledRoots = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["process"] = new ProcessApi(this),
            ["command"] = new CommandApi(this),
            ["http"] = new HttpApi(this),
            ["registry"] = new RegistryApi(this),
            ["zip"] = new ZipApi(this),
            ["hash"] = new HashApi(this),
            ["drive"] = new DriveApi(),
        };
    }

    public string AdapterName => "system";

    public object ResolveRoot(string rootName, JsonObject arguments)
    {
        if (rootName.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
        {
            string typeName = rootName["type:".Length..];
            return CreateTypeSurface(typeName, fallbackTarget: null, displayName: typeName);
        }

        if (TryResolveControlledRoot(rootName, out object? root))
        {
            return root!;
        }

        throw new InvalidOperationException($"System root '{rootName}' is not supported.");
    }

    public ReportVerbosity GetDefaultReportVerbosity(JsonObject arguments) => ReportVerbosity.Full;

    public object? AdaptValue(object? value) => value;

    public JsonNode? ConvertResult(object? value, InvokeDefinition definition, JsonObject arguments)
        => InvokeJsonNodeConverter.Convert(value);

    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RuntimeExecutionContextScope _ = RuntimeExecutionContextScope.Push(new RuntimeExecutionContext
        {
            AdapterName = AdapterName,
            DispatcherName = "system-inline",
            QueueDepth = 0,
            QueueWaitMilliseconds = 0,
            BusyRetryDelaysMs = Array.Empty<int>(),
            BusyRetryMaxAttempts = 0,
        });
        return Task.FromResult(action());
    }

    public CommandExecutionResult MapInvokeException(string commandId, Exception exception, InvokeDefinition definition)
    {
        return exception switch
        {
            JsonException jsonException => CommandExecutionResult.Fail("system_invalid_arguments", jsonException.Message),
            IOException ioException => CommandExecutionResult.Fail("system_io_error", ioException.Message),
            UnauthorizedAccessException unauthorizedException => CommandExecutionResult.Fail("system_access_denied", unauthorizedException.Message),
            InvalidOperationException invalidOperationException => CommandExecutionResult.Fail("system_invoke_failed", invalidOperationException.Message),
            Win32Exception win32Exception => CommandExecutionResult.Fail("system_platform_error", win32Exception.Message),
            HttpRequestException httpException => CommandExecutionResult.Fail("system_http_error", httpException.Message),
            _ => CommandExecutionResult.Fail("system_invoke_failed", exception.Message),
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    internal HttpClient HttpClient => _httpClient;

    internal SystemInvocationPolicy Policy => _policy;

    public string NormalizePathArgument(string path) => NormalizePath(path);

    internal string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        HashSet<string> allowedRoots = GetAllowedRoots();
        if (allowedRoots.Count == 0)
        {
            return fullPath;
        }

        bool allowed = allowedRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidOperationException($"Path '{fullPath}' is outside allowed roots.");
        }

        return fullPath;
    }

    internal static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }
    }

    private static string NormalizeRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private HashSet<string> GetAllowedRoots()
    {
        return _settings.SystemAdapter.AllowedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRoot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal object CreateTypeSurface(string typeName, object? fallbackTarget, string displayName)
    {
        Type resolvedType = _policy.ResolveAllowedType(typeName);
        return new SystemSurfaceProxy(this, displayName, resolvedType, fallbackTarget);
    }

    private bool TryResolveControlledRoot(string rootName, out object? root)
    {
        if (_controlledRoots.TryGetValue(rootName, out root))
        {
            return true;
        }

        foreach (SystemSurfaceBinding binding in _settings.SystemAdapter.Members)
        {
            if (!string.Equals(binding.Name, rootName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(binding.Target, rootName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(binding.Target))
            {
                root = null;
                return false;
            }

            if (!_controlledRoots.TryGetValue(binding.Target, out root))
            {
                throw new InvalidOperationException(
                    $"System controlled root '{binding.Target}' is not registered for binding '{binding.Name}'.");
            }

            return true;
        }

        root = null;
        return false;
    }

    public sealed class ProcessApi
    {
        private readonly SystemRuntime _owner;

        internal ProcessApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public JsonObject Start(
            string fileName,
            string? arguments = null,
            string? workingDirectory = null,
            bool shellExecute = false,
            bool createNoWindow = false,
            bool waitForExit = false,
            int timeoutMilliseconds = 0)
        {
            using Process process = StartProcess(fileName, arguments, workingDirectory, shellExecute, createNoWindow);
            if (waitForExit)
            {
                bool exited = timeoutMilliseconds > 0
                    ? process.WaitForExit(timeoutMilliseconds)
                    : process.WaitForExit(int.MaxValue);
                return CreateProcessView(process, exited, timeoutMilliseconds > 0 && !exited);
            }

            return CreateProcessView(process, process.HasExited, timedOut: false);
        }

        public JsonObject ShellOpen(string target, string? arguments = null, string? workingDirectory = null)
        {
            using Process process = StartProcess(target, arguments, workingDirectory, shellExecute: true, createNoWindow: false);
            return CreateProcessView(process, process.HasExited, timedOut: false);
        }

        public JsonArray List(string? processName = null)
        {
            IEnumerable<Process> processes = Process.GetProcesses()
                .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                processes = processes.Where(process => string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            }

            JsonArray array = new();
            foreach (Process process in processes)
            {
                using (process)
                {
                    array.Add(CreateProcessView(process, process.HasExited, timedOut: false));
                }
            }

            return array;
        }

        public JsonObject Get(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            return CreateProcessView(process, process.HasExited, timedOut: false);
        }

        public JsonObject Kill(int processId, bool entireProcessTree = false)
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree);
            return new JsonObject
            {
                ["processId"] = processId,
                ["killed"] = true,
                ["entireProcessTree"] = entireProcessTree,
            };
        }

        public JsonObject WaitForExit(int processId, int timeoutMilliseconds = 0)
        {
            using Process process = Process.GetProcessById(processId);
            bool exited = timeoutMilliseconds > 0
                ? process.WaitForExit(timeoutMilliseconds)
                : process.WaitForExit(int.MaxValue);
            return CreateProcessView(process, exited, timeoutMilliseconds > 0 && !exited);
        }

        public JsonObject CloseMainWindow(int processId)
        {
            using Process process = Process.GetProcessById(processId);
            return new JsonObject
            {
                ["processId"] = processId,
                ["result"] = process.CloseMainWindow(),
            };
        }

        private Process StartProcess(string fileName, string? arguments, string? workingDirectory, bool shellExecute, bool createNoWindow)
        {
            _owner.Policy.EnsureProcessExecutableAllowed(fileName);
            string resolvedWorkingDirectory = ResolveWorkingDirectory(fileName, workingDirectory, shellExecute);
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = resolvedWorkingDirectory,
                UseShellExecute = shellExecute,
                CreateNoWindow = createNoWindow,
            };

            if (shellExecute && System.IO.File.Exists(fileName))
            {
                startInfo.Verb = "open";
            }

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        private string ResolveWorkingDirectory(string fileName, string? workingDirectory, bool shellExecute)
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                return _owner.NormalizePath(workingDirectory);
            }

            if (shellExecute && System.IO.File.Exists(fileName))
            {
                string? directory = System.IO.Path.GetDirectoryName(fileName);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }

            return AppContext.BaseDirectory;
        }

        private static JsonObject CreateProcessView(Process process, bool exited, bool timedOut)
        {
            int? exitCode = null;
            if (exited)
            {
                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                }
            }

            return new JsonObject
            {
                ["processId"] = process.Id,
                ["processName"] = TryReadProcessString(process, static current => current.ProcessName),
                ["hasExited"] = exited,
                ["exitCode"] = exitCode,
                ["timedOut"] = timedOut,
                ["startTimeUtc"] = TryReadProcessDate(process, static current => current.StartTime.ToUniversalTime()),
                ["mainWindowTitle"] = TryReadProcessString(process, static current => current.MainWindowTitle),
            };
        }

        private static DateTimeOffset? TryReadProcessDate(Process process, Func<Process, DateTime> accessor)
        {
            try
            {
                return accessor(process);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryReadProcessString(Process process, Func<Process, string?> accessor)
        {
            try
            {
                return accessor(process);
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class CommandApi
    {
        private readonly SystemRuntime _owner;

        internal CommandApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public JsonObject Run(
            string fileName,
            string? arguments = null,
            string? workingDirectory = null,
            int timeoutMilliseconds = 0,
            string? standardInput = null,
            JsonObject? environment = null)
        {
            _owner.Policy.EnsureProcessExecutableAllowed(fileName);
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? AppContext.BaseDirectory
                    : _owner.NormalizePath(workingDirectory),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true,
            };

            if (environment is not null)
            {
                foreach ((string key, JsonNode? value) in environment)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    startInfo.Environment[key] = value.ToString();
                }
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start command '{fileName}'.");

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = timeoutMilliseconds > 0
                ? process.WaitForExit(timeoutMilliseconds)
                : process.WaitForExit(int.MaxValue);

            return new JsonObject
            {
                ["processId"] = process.Id,
                ["fileName"] = fileName,
                ["arguments"] = arguments,
                ["timedOut"] = timeoutMilliseconds > 0 && !exited,
                ["hasExited"] = exited,
                ["exitCode"] = exited ? process.ExitCode : null,
                ["stdout"] = exited ? stdoutTask.GetAwaiter().GetResult() : null,
                ["stderr"] = exited ? stderrTask.GetAwaiter().GetResult() : null,
            };
        }
    }

    public sealed class HttpApi
    {
        private readonly SystemRuntime _owner;

        internal HttpApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public string GetString(string url)
        {
            _owner.Policy.EnsureUrlAllowed(url);
            return _owner.HttpClient.GetStringAsync(url).GetAwaiter().GetResult();
        }

        public JsonNode? GetJson(string url)
        {
            string content = GetString(url);
            return JsonNode.Parse(content);
        }

        public JsonObject DownloadFile(string url, string path)
        {
            _owner.Policy.EnsureUrlAllowed(url);
            string fullPath = _owner.NormalizePath(path);
            EnsureParentDirectory(fullPath);
            byte[] bytes = _owner.HttpClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
            System.IO.File.WriteAllBytes(fullPath, bytes);
            return new JsonObject
            {
                ["url"] = url,
                ["path"] = fullPath,
                ["byteLength"] = bytes.Length,
            };
        }

        public JsonObject Head(string url)
        {
            _owner.Policy.EnsureUrlAllowed(url);
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            using HttpResponseMessage response = _owner.HttpClient.Send(request);
            return CreateHttpResponseView(response, content: null);
        }

        public JsonObject PostJson(string url, JsonNode? payload)
        {
            return Send("POST", url, payload, headers: null);
        }

        public JsonObject Send(string method, string url, JsonNode? payload, JsonObject? headers)
        {
            _owner.Policy.EnsureUrlAllowed(url);
            using HttpRequestMessage request = new(new HttpMethod(method), url);
            if (payload is not null)
            {
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            }

            if (headers is not null)
            {
                foreach ((string key, JsonNode? value) in headers)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    if (!request.Headers.TryAddWithoutValidation(key, value.ToString()) && request.Content is not null)
                    {
                        _ = request.Content.Headers.TryAddWithoutValidation(key, value.ToString());
                    }
                }
            }

            using HttpResponseMessage response = _owner.HttpClient.Send(request);
            string? content = response.Content is null
                ? null
                : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateHttpResponseView(response, content);
        }

        private static JsonObject CreateHttpResponseView(HttpResponseMessage response, string? content)
        {
            JsonObject headers = new();
            foreach ((string key, IEnumerable<string> value) in response.Headers)
            {
                headers[key] = string.Join(", ", value);
            }

            if (response.Content is not null)
            {
                foreach ((string key, IEnumerable<string> value) in response.Content.Headers)
                {
                    headers[key] = string.Join(", ", value);
                }
            }

            JsonNode? body = content is null
                ? null
                : response.Content?.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                ? JsonNode.Parse(content)
                : JsonValue.Create(content);

            return new JsonObject
            {
                ["statusCode"] = (int)response.StatusCode,
                ["reasonPhrase"] = response.ReasonPhrase,
                ["isSuccessStatusCode"] = response.IsSuccessStatusCode,
                ["headers"] = headers,
                ["content"] = body,
            };
        }
    }

    public sealed class RegistryApi
    {
        private readonly SystemRuntime _owner;

        internal RegistryApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public JsonNode? GetValue(string hive, string keyPath, string? valueName = null)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.OpenSubKey(keyPath)
                ?? throw new InvalidOperationException($"Registry key '{hive}\\{keyPath}' was not found.");
            return InvokeJsonNodeConverter.Convert(key.GetValue(valueName));
        }

        public JsonObject SetValue(string hive, string keyPath, string valueName, JsonNode? value, string? valueKind = null)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException($"Failed to create registry key '{hive}\\{keyPath}'.");

            RegistryValueKind kind = string.IsNullOrWhiteSpace(valueKind)
                ? RegistryValueKind.String
                : Enum.TryParse(valueKind, ignoreCase: true, out RegistryValueKind parsedKind)
                ? parsedKind
                : throw new InvalidOperationException($"Unknown RegistryValueKind '{valueKind}'.");
            object? rawValue = value switch
            {
                null => null,
                JsonValue jsonValue when jsonValue.TryGetValue(out string? text) => text,
                JsonValue jsonValue when jsonValue.TryGetValue(out int intValue) => intValue,
                JsonValue jsonValue when jsonValue.TryGetValue(out long longValue) => longValue,
                JsonValue jsonValue when jsonValue.TryGetValue(out bool boolValue) => boolValue ? 1 : 0,
                JsonArray jsonArray => jsonArray.Select(item => item?.ToString() ?? string.Empty).ToArray(),
                _ => value.ToJsonString(),
            };

            key.SetValue(valueName, rawValue ?? string.Empty, kind);
            return new JsonObject
            {
                ["hive"] = hive,
                ["keyPath"] = keyPath,
                ["valueName"] = valueName,
                ["valueKind"] = kind.ToString(),
            };
        }

        public JsonObject DeleteValue(string hive, string keyPath, string valueName, bool throwOnMissingValue = false)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.OpenSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException($"Registry key '{hive}\\{keyPath}' was not found.");
            key.DeleteValue(valueName, throwOnMissingValue);
            return new JsonObject
            {
                ["hive"] = hive,
                ["keyPath"] = keyPath,
                ["valueName"] = valueName,
            };
        }

        public JsonObject CreateKey(string hive, string keyPath)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException($"Failed to create registry key '{hive}\\{keyPath}'.");
            return new JsonObject
            {
                ["hive"] = hive,
                ["keyPath"] = key.Name,
            };
        }

        public JsonObject DeleteKey(string hive, string keyPath, bool recursive = false)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            if (recursive)
            {
                baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
            else
            {
                baseKey.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
            }

            return new JsonObject
            {
                ["hive"] = hive,
                ["keyPath"] = keyPath,
                ["recursive"] = recursive,
            };
        }

        public string[] GetSubKeyNames(string hive, string keyPath)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.OpenSubKey(keyPath)
                ?? throw new InvalidOperationException($"Registry key '{hive}\\{keyPath}' was not found.");
            return key.GetSubKeyNames();
        }

        public string[] GetValueNames(string hive, string keyPath)
        {
            using RegistryKey baseKey = OpenBaseKey(hive);
            using RegistryKey key = baseKey.OpenSubKey(keyPath)
                ?? throw new InvalidOperationException($"Registry key '{hive}\\{keyPath}' was not found.");
            return key.GetValueNames();
        }

        private RegistryKey OpenBaseKey(string hive)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Registry access is available only on Windows.");
            }

            _owner.Policy.EnsureRegistryHiveAllowed(hive);
            RegistryHive parsed = Enum.TryParse(hive, ignoreCase: true, out RegistryHive registryHive)
                ? registryHive
                : throw new InvalidOperationException($"Unknown RegistryHive '{hive}'.");
            return RegistryKey.OpenBaseKey(parsed, RegistryView.Default);
        }
    }

    public sealed class ZipApi
    {
        private readonly SystemRuntime _owner;

        internal ZipApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public JsonObject CreateFromDirectory(string sourceDirectory, string destinationArchiveFileName, bool includeBaseDirectory = false)
        {
            string source = _owner.NormalizePath(sourceDirectory);
            string destination = _owner.NormalizePath(destinationArchiveFileName);
            EnsureParentDirectory(destination);
            if (System.IO.File.Exists(destination))
            {
                System.IO.File.Delete(destination);
            }

            ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Optimal, includeBaseDirectory);
            return new JsonObject
            {
                ["sourceDirectory"] = source,
                ["archivePath"] = destination,
            };
        }

        public JsonObject ExtractToDirectory(string sourceArchiveFileName, string destinationDirectoryName, bool overwriteFiles = true)
        {
            string archive = _owner.NormalizePath(sourceArchiveFileName);
            string destination = _owner.NormalizePath(destinationDirectoryName);
            _ = System.IO.Directory.CreateDirectory(destination);
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles);
            return new JsonObject
            {
                ["archivePath"] = archive,
                ["destinationDirectory"] = destination,
                ["overwriteFiles"] = overwriteFiles,
            };
        }

        public JsonArray ListEntries(string archivePath)
        {
            string fullPath = _owner.NormalizePath(archivePath);
            using ZipArchive archive = ZipFile.OpenRead(fullPath);
            JsonArray array = new();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                array.Add(new JsonObject
                {
                    ["fullName"] = entry.FullName,
                    ["length"] = entry.Length,
                    ["compressedLength"] = entry.CompressedLength,
                    ["lastWriteTimeUtc"] = entry.LastWriteTime.UtcDateTime,
                });
            }

            return array;
        }

        public JsonObject CreateFromFiles(string archivePath, string[] files, string? baseDirectory = null)
        {
            string fullArchivePath = _owner.NormalizePath(archivePath);
            EnsureParentDirectory(fullArchivePath);
            if (System.IO.File.Exists(fullArchivePath))
            {
                System.IO.File.Delete(fullArchivePath);
            }

            string? fullBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? null : _owner.NormalizePath(baseDirectory);
            using ZipArchive archive = ZipFile.Open(fullArchivePath, ZipArchiveMode.Create);
            foreach (string file in files)
            {
                string fullFile = _owner.NormalizePath(file);
                string entryName = fullBaseDirectory is null
                    ? System.IO.Path.GetFileName(fullFile)
                    : System.IO.Path.GetRelativePath(fullBaseDirectory, fullFile);
                archive.CreateEntryFromFile(fullFile, entryName, CompressionLevel.Optimal);
            }

            return new JsonObject
            {
                ["archivePath"] = fullArchivePath,
                ["fileCount"] = files.Length,
            };
        }
    }

    public sealed class HashApi
    {
        private readonly SystemRuntime _owner;

        internal HashApi(SystemRuntime owner)
        {
            _owner = owner;
        }

        public JsonObject ComputeText(string algorithm, string text, string? encoding = null)
        {
            Encoding resolvedEncoding = ResolveEncoding(encoding);
            byte[] bytes = resolvedEncoding.GetBytes(text);
            return CreateHashResult(algorithm, bytes, source: "text");
        }

        public JsonObject ComputeFile(string algorithm, string path)
        {
            string fullPath = _owner.NormalizePath(path);
            byte[] bytes = System.IO.File.ReadAllBytes(fullPath);
            JsonObject result = CreateHashResult(algorithm, bytes, source: "file");
            result["path"] = fullPath;
            return result;
        }

        public JsonObject ComputeBytes(string algorithm, byte[] bytes)
        {
            return CreateHashResult(algorithm, bytes, source: "bytes");
        }

        private static JsonObject CreateHashResult(string algorithm, byte[] bytes, string source)
        {
            using HashAlgorithm hashAlgorithm = CreateHashAlgorithm(algorithm);
            byte[] hash = hashAlgorithm.ComputeHash(bytes);
            return new JsonObject
            {
                ["algorithm"] = hashAlgorithm.GetType().Name.Replace("Managed", string.Empty, StringComparison.OrdinalIgnoreCase),
                ["source"] = source,
                ["hex"] = Convert.ToHexString(hash),
                ["base64"] = Convert.ToBase64String(hash),
            };
        }

        private static HashAlgorithm CreateHashAlgorithm(string algorithm)
        {
            return algorithm.ToUpperInvariant() switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => throw new InvalidOperationException($"Unknown hash algorithm '{algorithm}'."),
            };
        }

        private static Encoding ResolveEncoding(string? encoding)
        {
            return string.IsNullOrWhiteSpace(encoding)
                ? Encoding.UTF8
                : Encoding.GetEncoding(encoding);
        }
    }

    public sealed class DriveApi
    {
        public JsonArray List()
        {
            JsonArray array = new();
            foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
            {
                array.Add(CreateDriveInfo(drive));
            }

            return array;
        }

        public JsonObject GetInfo(string nameOrRoot)
        {
            DriveInfo drive = DriveInfo.GetDrives()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, nameOrRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.RootDirectory.FullName, nameOrRoot, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Drive '{nameOrRoot}' was not found.");
            return CreateDriveInfo(drive);
        }

        private static JsonObject CreateDriveInfo(DriveInfo drive)
        {
            return new JsonObject
            {
                ["name"] = drive.Name,
                ["root"] = drive.RootDirectory.FullName,
                ["type"] = drive.DriveType.ToString(),
                ["format"] = drive.IsReady ? drive.DriveFormat : null,
                ["label"] = drive.IsReady ? drive.VolumeLabel : null,
                ["availableFreeSpace"] = drive.IsReady ? drive.AvailableFreeSpace : null,
                ["totalFreeSpace"] = drive.IsReady ? drive.TotalFreeSpace : null,
                ["totalSize"] = drive.IsReady ? drive.TotalSize : null,
                ["isReady"] = drive.IsReady,
            };
        }
    }
}

public sealed class SystemInvokeSurface : ReflectiveInvokeSurfaceBase<SystemRuntime>
{
    public SystemInvokeSurface(SystemRuntime runtime)
        : base(runtime)
    {
    }
}

public sealed record SystemEntryView(string Name, string FullPath, bool IsDirectory);

#pragma warning restore CA5351
#pragma warning restore CA5350
#pragma warning restore CA1822
#pragma warning restore CA1416


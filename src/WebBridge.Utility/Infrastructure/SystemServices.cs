using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Infrastructure;

public sealed class SystemUtilityClock : IUtilityClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}

public sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    public Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });

        return Task.CompletedTask;
    }
}

public sealed class NamedPipeSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellationSource;
    private Task? _listenerTask;

    public async Task<SingleInstanceInitializationResult> InitializeAsync(
        string instanceName,
        Func<ActivationRequest, Task> onActivation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(onActivation);

        bool createdNew;
        _mutex = new Mutex(initiallyOwned: false, name: instanceName, createdNew: out createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return new SingleInstanceInitializationResult(false, instanceName);
        }

        string pipeName = ToPipeName(instanceName);
        _listenerCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenerTask = ListenAsync(pipeName, onActivation, _listenerCancellationSource.Token);
        await Task.Yield();
        return new SingleInstanceInitializationResult(true, instanceName);
    }

    public async Task<bool> SendActivationAsync(string instanceName, ActivationRequest request, CancellationToken cancellationToken)
    {
        string pipeName = ToPipeName(instanceName);

        try
        {
            await using NamedPipeClientStream client = new(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(client, request, _serializerOptions, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_listenerCancellationSource is not null)
        {
            await _listenerCancellationSource.CancelAsync().ConfigureAwait(false);
            _listenerCancellationSource.Dispose();
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_mutex is not null)
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private async Task ListenAsync(
        string pipeName,
        Func<ActivationRequest, Task> onActivation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream server = new(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            ActivationRequest? request = await JsonSerializer.DeserializeAsync<ActivationRequest>(
                server,
                _serializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (request is not null)
            {
                await onActivation(request).ConfigureAwait(false);
            }
        }
    }

    private static string ToPipeName(string instanceName)
    {
        return instanceName.Replace("\\", "-", StringComparison.Ordinal);
    }
}


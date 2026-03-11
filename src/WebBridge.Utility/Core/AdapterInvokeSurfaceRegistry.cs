using WebBridge.Utility.Adapters.Com;
using WebBridge.Utility.Adapters.SystemAccess;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Core;

public sealed class AdapterInvokeSurfaceRegistry : IAdapterInvokeSurfaceRegistry, IDisposable
{
    private readonly object _sync = new();
    private readonly IRuntimeConfigurationManager _configurationManager;
    private readonly SystemInvokeSurface _systemSurface;
    private readonly IComInvokeSurfaceFactory _comSurfaceFactory;
    private Dictionary<string, IAdapterInvokeSurface> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private long _generation = -1;

    public AdapterInvokeSurfaceRegistry(
        IRuntimeConfigurationManager configurationManager,
        SystemInvokeSurface systemSurface,
        IComInvokeSurfaceFactory comSurfaceFactory)
    {
        _configurationManager = configurationManager;
        _systemSurface = systemSurface;
        _comSurfaceFactory = comSurfaceFactory;
    }

    public bool TryGetSurface(string adapterName, out IAdapterInvokeSurface? surface)
    {
        EnsureCurrent();
        return _surfaces.TryGetValue(adapterName, out surface);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            DisposeSurfaces(_surfaces.Values);
            _surfaces = new Dictionary<string, IAdapterInvokeSurface>(StringComparer.OrdinalIgnoreCase);
            _generation = -1;
        }
    }

    private void EnsureCurrent()
    {
        long generation = _configurationManager.GetGeneration();
        if (generation == Interlocked.Read(ref _generation))
        {
            return;
        }

        lock (_sync)
        {
            if (generation == _generation)
            {
                return;
            }

            UtilitySettings snapshot = _configurationManager.GetSettingsSnapshot();
            Dictionary<string, IAdapterInvokeSurface> next = new(StringComparer.OrdinalIgnoreCase)
            {
                [_systemSurface.AdapterName] = _systemSurface,
            };

            foreach (ComInvokeDescriptor descriptor in snapshot.ComAdapters
                         .Where(candidate => !string.IsNullOrWhiteSpace(candidate.AdapterName))
                         .GroupBy(candidate => candidate.AdapterName, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First().Clone()))
            {
                next[descriptor.AdapterName] = _comSurfaceFactory.Create(descriptor);
            }

            Dictionary<string, IAdapterInvokeSurface> previous = _surfaces;
            _surfaces = next;
            _generation = generation;
            DisposeSurfaces(previous.Values.Where(surface => !ReferenceEquals(surface, _systemSurface)));
        }
    }

    private static void DisposeSurfaces(IEnumerable<IAdapterInvokeSurface> surfaces)
    {
        foreach (IAdapterInvokeSurface surface in surfaces.Distinct())
        {
            if (surface is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

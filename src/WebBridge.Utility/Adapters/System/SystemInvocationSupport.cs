using System.Reflection;
using WebBridge.Utility.Core;
using WebBridge.Utility.Protocol;

namespace WebBridge.Utility.Adapters.SystemAccess;

internal sealed class SystemInvocationPolicy
{
    private readonly SystemAdapterSettings _settings;

    public SystemInvocationPolicy(UtilitySettings settings)
    {
        _settings = settings.SystemAdapter;
    }

    public Type ResolveAllowedType(string typeName)
    {
        Type resolved = ResolveType(typeName);
        EnsureTypeAllowed(resolved);
        return resolved;
    }

    public void EnsureTypeAllowed(Type type)
    {
        string typeName = type.FullName ?? type.Name;
        string? ns = type.Namespace;

        if (MatchesAny(typeName, _settings.DeniedTypeNames) ||
            (!string.IsNullOrWhiteSpace(ns) && MatchesAny(ns, _settings.DeniedNamespaces)))
        {
            throw new InvalidOperationException($"System type '{typeName}' is blocked by policy.");
        }

        if (_settings.AllowedTypeNames.Count > 0 && !MatchesAny(typeName, _settings.AllowedTypeNames))
        {
            throw new InvalidOperationException($"System type '{typeName}' is not in the allowed type list.");
        }

        if (_settings.AllowedNamespaces.Count > 0 &&
            (string.IsNullOrWhiteSpace(ns) || !MatchesAny(ns, _settings.AllowedNamespaces)))
        {
            throw new InvalidOperationException($"System type '{typeName}' is outside allowed namespaces.");
        }
    }

    public void EnsureInvocationAllowed(Type type, string member, string operation)
    {
        EnsureTypeAllowed(type);

        string typeName = type.FullName ?? type.Name;
        string effectiveMember = string.IsNullOrWhiteSpace(member) ? ".ctor" : member;
        string[] candidates =
        [
            $"{typeName}::{effectiveMember}",
            $"{typeName}::{operation}:{effectiveMember}",
        ];

        if (candidates.Any(candidate => MatchesAny(candidate, _settings.DeniedInvocations)))
        {
            throw new InvalidOperationException(
                $"System invocation '{typeName}::{effectiveMember}' is blocked by policy.");
        }
    }

    public void EnsureProcessExecutableAllowed(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Process executable must not be empty.");
        }

        string normalized = NormalizeProcessTarget(fileName);
        if (MatchesAny(normalized, _settings.DeniedProcessExecutables))
        {
            throw new InvalidOperationException($"Process target '{normalized}' is blocked by policy.");
        }

        if (_settings.AllowedProcessExecutables.Count > 0 &&
            !MatchesAny(normalized, _settings.AllowedProcessExecutables))
        {
            throw new InvalidOperationException($"Process target '{normalized}' is outside the allowed list.");
        }
    }

    public void EnsureUrlAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("URL must not be empty.");
        }

        if (MatchesAny(url, _settings.DeniedUrlPrefixes))
        {
            throw new InvalidOperationException($"URL '{url}' is blocked by policy.");
        }

        if (_settings.AllowedUrlPrefixes.Count > 0 &&
            !MatchesAny(url, _settings.AllowedUrlPrefixes))
        {
            throw new InvalidOperationException($"URL '{url}' is outside the allowed prefixes.");
        }
    }

    public void EnsureRegistryHiveAllowed(string hive)
    {
        if (string.IsNullOrWhiteSpace(hive))
        {
            throw new InvalidOperationException("Registry hive must not be empty.");
        }

        if (MatchesAny(hive, _settings.DeniedRegistryHives))
        {
            throw new InvalidOperationException($"Registry hive '{hive}' is blocked by policy.");
        }

        if (_settings.AllowedRegistryHives.Count > 0 &&
            !MatchesAny(hive, _settings.AllowedRegistryHives))
        {
            throw new InvalidOperationException($"Registry hive '{hive}' is outside the allowed list.");
        }
    }

    private static Type ResolveType(string typeName)
    {
        Type? direct = Type.GetType(typeName, throwOnError: false, ignoreCase: true);
        if (direct is not null)
        {
            return direct;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: true);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new InvalidOperationException($"System type '{typeName}' could not be resolved.");
    }

    private static string NormalizeProcessTarget(string fileName)
    {
        if (File.Exists(fileName))
        {
            string extension = Path.GetExtension(fileName);
            if (!IsExecutableExtension(extension))
            {
                return Path.GetFileName(fileName);
            }
        }

        return Path.GetFileName(fileName);
    }

    private static bool IsExecutableExtension(string extension)
    {
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".msc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAny(string value, IEnumerable<string> rules)
    {
        return rules.Any(rule => MatchesRule(value, rule));
    }

    private static bool MatchesRule(string value, string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        string normalizedRule = rule.Trim();
        if (normalizedRule.EndsWith('*'))
        {
            return value.StartsWith(normalizedRule[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(value, normalizedRule, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class SystemSurfaceProxy : IReflectiveInvocationProxy
{
    private readonly SystemRuntime _runtime;
    private readonly string _displayName;
    private readonly Type _type;
    private readonly object? _fallbackTarget;

    public SystemSurfaceProxy(SystemRuntime runtime, string displayName, Type type, object? fallbackTarget)
    {
        _runtime = runtime;
        _displayName = displayName;
        _type = type;
        _fallbackTarget = fallbackTarget;
    }

    public object? GetMemberValue(string member)
    {
        if (_fallbackTarget is not null && HasReadableFallbackMember(member))
        {
            return ReflectiveInvokeAccessor.GetMemberValue(_fallbackTarget, member);
        }

        _runtime.Policy.EnsureInvocationAllowed(_type, member, "get");
        if (ReflectiveInvokeAccessor.TryGetStaticMemberValue(_type, member, out object? value))
        {
            return value;
        }

        throw new MissingMemberException($"System surface '{_displayName}' does not contain member '{member}'.");
    }

    public void SetMemberValue(string member, object? value)
    {
        if (_fallbackTarget is not null && HasWritableFallbackMember(member))
        {
            ReflectiveInvokeAccessor.SetMemberValue(_fallbackTarget, member, value);
            return;
        }

        _runtime.Policy.EnsureInvocationAllowed(_type, member, "set");
        if (ReflectiveInvokeAccessor.TrySetStaticMemberValue(_type, member, value))
        {
            return;
        }

        throw new MissingMemberException($"System surface '{_displayName}' does not allow setting member '{member}'.");
    }

    public InvokeCallOutcome CallMember(string member, ResolvedInvokeArgument[] arguments)
    {
        if (_fallbackTarget is not null && HasCallableFallbackMember(member))
        {
            return ReflectiveInvokeAccessor.CallMember(_fallbackTarget, member, arguments);
        }

        _runtime.Policy.EnsureInvocationAllowed(_type, member, "call");
        if (ReflectiveInvokeAccessor.TryCallStaticMember(_type, member, arguments, out InvokeCallOutcome outcome))
        {
            return outcome;
        }

        throw new MissingMethodException($"System surface '{_displayName}' does not expose method '{member}'.");
    }

    public InvokeCallOutcome IndexValue(string member, ResolvedInvokeArgument[] arguments)
    {
        if (_fallbackTarget is not null)
        {
            return ReflectiveInvokeAccessor.IndexValue(_fallbackTarget, member, arguments);
        }

        throw new InvalidOperationException($"System surface '{_displayName}' does not support indexing.");
    }

    public InvokeCallOutcome CreateInstance(ResolvedInvokeArgument[] arguments)
    {
        _runtime.Policy.EnsureInvocationAllowed(_type, ".ctor", "new");
        return ReflectiveInvokeAccessor.CreateTypeInstance(_type, arguments);
    }

    private bool HasReadableFallbackMember(string member)
    {
        if (_fallbackTarget is IReflectiveMemberProvider provider &&
            provider.TryGetReflectiveMember(member, out _))
        {
            return true;
        }

        if (_fallbackTarget is null)
        {
            return false;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        return _fallbackTarget.GetType().GetProperty(member, Flags) is not null ||
            _fallbackTarget.GetType().GetField(member, Flags) is not null;
    }

    private bool HasWritableFallbackMember(string member)
    {
        if (_fallbackTarget is null)
        {
            return false;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        PropertyInfo? property = _fallbackTarget.GetType().GetProperty(member, Flags);
        if (property?.CanWrite == true)
        {
            return true;
        }

        return _fallbackTarget.GetType().GetField(member, Flags) is not null;
    }

    private bool HasCallableFallbackMember(string member)
    {
        if (_fallbackTarget is null)
        {
            return false;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
        return _fallbackTarget.GetType()
            .GetMethods(Flags)
            .Any(method => string.Equals(method.Name, member, StringComparison.OrdinalIgnoreCase));
    }
}


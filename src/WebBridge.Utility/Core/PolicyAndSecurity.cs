using WebBridge.Utility.Protocol;
using NuGet.Versioning;

namespace WebBridge.Utility.Core;

public sealed class VersionCompatibilityService : IVersionCompatibilityService
{
    public CompatibilityEvaluation Evaluate(Manifest manifest, string utilityVersion)
    {
        NuGetVersion current = NuGetVersion.Parse(utilityVersion);
        NuGetVersion minimum = NuGetVersion.Parse(manifest.MinUtilityVersion);
        NuGetVersion? recommended = string.IsNullOrWhiteSpace(manifest.RecommendedUtilityVersion)
            ? null
            : NuGetVersion.Parse(manifest.RecommendedUtilityVersion);

        if (current < minimum)
        {
            return new CompatibilityEvaluation(
                false,
                null,
                $"Manifest requires utility version {minimum} or newer. Current utility version is {current}.");
        }

        if (recommended is not null && current < recommended)
        {
            return new CompatibilityEvaluation(
                true,
                $"Manifest recommends utility version {recommended}. Current utility version is {current}.",
                null);
        }

        return new CompatibilityEvaluation(true, null, null);
    }
}

public sealed class SecurityValidator : ISecurityValidator
{
    private readonly UtilitySettings _settings;

    public SecurityValidator(UtilitySettings settings)
    {
        _settings = settings;
    }

    public SecurityValidationResult ValidateLoopback(string? remoteIpAddress)
    {
        if (!_settings.Security.LoopbackOnly)
        {
            return SecurityValidationResult.Allowed();
        }

        if (string.IsNullOrWhiteSpace(remoteIpAddress))
        {
            return SecurityValidationResult.Allowed();
        }

        if (string.Equals(remoteIpAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(remoteIpAddress, "::1", StringComparison.OrdinalIgnoreCase))
        {
            return SecurityValidationResult.Allowed();
        }

        return SecurityValidationResult.Rejected("loopback_required", "Only loopback clients are allowed.");
    }

    public SecurityValidationResult ValidateOrigin(string? origin)
    {
        HashSet<string> allowedOrigins = _settings.Security.AllowedOrigins
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(NormalizeOrigin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedOrigins.Count == 0)
        {
            return SecurityValidationResult.Rejected("origin_not_allowed", "Origin allowlist is empty.");
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return SecurityValidationResult.Rejected("origin_missing", "Origin header is required.");
        }

        return allowedOrigins.Contains(NormalizeOrigin(origin))
            ? SecurityValidationResult.Allowed()
            : SecurityValidationResult.Rejected("origin_not_allowed", $"Origin '{origin}' is not allowed.");
    }

    public SecurityValidationResult ValidatePairingToken(string? token)
    {
        return string.Equals(token, _settings.Security.PairingToken, StringComparison.Ordinal)
            ? SecurityValidationResult.Allowed()
            : SecurityValidationResult.Rejected("pairing_token_invalid", "Pairing token is missing or invalid.");
    }

    private static string NormalizeOrigin(string origin)
    {
        return origin.Trim().TrimEnd('/');
    }
}

public sealed class BrowserLaunchDecider : IBrowserLaunchDecider
{
    public bool ShouldLaunchBrowser(UtilitySettings settings, bool hasPresenceSession, bool hasActiveSession)
    {
        if (settings.NoBrowser || string.IsNullOrWhiteSpace(settings.UiUrl))
        {
            return false;
        }

        return settings.OpenUi switch
        {
            OpenUiMode.Always => true,
            OpenUiMode.Auto => settings.Session.SuppressAutoOpenOnPresenceSessions
                ? !hasPresenceSession
                : !hasActiveSession,
            OpenUiMode.Never => false,
            _ => false,
        };
    }
}

public static class UtilitySettingsSanitizer
{
    public static UtilitySettings Redact(UtilitySettings settings)
    {
        UtilitySettings clone = settings.Clone();
        clone.Security.PairingToken = "***redacted***";
        return clone;
    }
}


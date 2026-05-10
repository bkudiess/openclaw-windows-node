using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Resolves operator credentials for user-facing surfaces such as chat.
/// </summary>
public static class InteractiveGatewayCredentialResolver
{
    public static bool TryResolve(
        SettingsManager settings,
        GatewayRegistry? registry,
        string settingsDirectory,
        IDeviceIdentityReader identityReader,
        out InteractiveGatewayCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        ArgumentNullException.ThrowIfNull(identityReader);

        var resolver = new CredentialResolver(identityReader);
        var active = registry?.GetActive();
        if (active != null && !string.IsNullOrWhiteSpace(active.Url))
        {
            var resolved = resolver.ResolveOperator(active, registry!.GetIdentityDirectory(active.Id));
            if (resolved != null)
            {
                credential = new InteractiveGatewayCredential(
                    active.Url,
                    resolved.Token,
                    resolved.IsBootstrapToken,
                    resolved.Source);
                return true;
            }

            if (!string.Equals(active.Url, settings.GetEffectiveGatewayUrl(), StringComparison.OrdinalIgnoreCase))
            {
                credential = null;
                return false;
            }
        }

        var gatewayUrl = settings.GetEffectiveGatewayUrl();
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            credential = null;
            return false;
        }

        var legacyRecord = new GatewayRecord
        {
            Id = "legacy-settings",
            Url = gatewayUrl,
            SharedGatewayToken = settings.LegacyToken,
            BootstrapToken = settings.LegacyBootstrapToken
        };
        var legacyCredential = resolver.ResolveOperator(legacyRecord, settingsDirectory);
        if (legacyCredential == null)
        {
            credential = null;
            return false;
        }

        credential = new InteractiveGatewayCredential(
            gatewayUrl,
            legacyCredential.Token,
            legacyCredential.IsBootstrapToken,
            legacyCredential.Source);
        return true;
    }
}

public sealed record InteractiveGatewayCredential(
    string GatewayUrl,
    string Token,
    bool IsBootstrapToken,
    string Source);

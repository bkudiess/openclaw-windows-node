using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class StartupSetupState
{
    /// <summary>
    /// Default loopback gateway URL. Matches <c>OnboardingExistingConfigGuard.DefaultGatewayUrl</c>;
    /// kept in sync intentionally so both startup and onboarding decisions use the same signal
    /// for "user has a real (non-default) gateway target configured".
    /// </summary>
    private const string DefaultGatewayUrl = "ws://localhost:18789";

    public static bool HasStoredNodeDeviceToken(string dataPath) =>
        DeviceIdentity.HasStoredDeviceTokenForRole(dataPath, "node", NullLogger.Instance);

    /// <summary>
    /// True if the user has previously paired this device as an operator
    /// (root device token present) AND has a non-default gateway URL configured.
    /// Both signals together indicate a working operator config — guards against
    /// orphan tokens or default-URL-only configs that wouldn't actually connect.
    /// </summary>
    public static bool HasUsableOperatorConfiguration(SettingsManager settings, string dataPath) =>
        DeviceIdentity.HasStoredDeviceToken(dataPath, NullLogger.Instance)
        && HasNonDefaultGatewayUrl(settings);

    private static bool HasNonDefaultGatewayUrl(SettingsManager settings) =>
        !string.IsNullOrWhiteSpace(settings.GatewayUrl)
        && !string.Equals(settings.GatewayUrl, DefaultGatewayUrl, StringComparison.OrdinalIgnoreCase);

    public static bool CanStartNodeGateway(SettingsManager settings, string dataPath)
    {
        if (!settings.EnableNodeMode)
        {
            return false;
        }

        return HasStoredNodeDeviceToken(dataPath);
    }

    public static bool RequiresSetup(SettingsManager settings, string dataPath)
    {
        // Node mode: needs a paired node device token to operate.
        if (settings.EnableNodeMode)
        {
            return !HasStoredNodeDeviceToken(dataPath);
        }

        // MCP-only mode doesn't require an authenticated gateway.
        if (settings.EnableMcpServer)
        {
            return false;
        }

        // Operator mode: returning users with a stored operator device token AND
        // a non-default gateway URL already have a working configuration — don't
        // auto-launch the wizard (Scott Hanselman repro: remote-gateway operator
        // saw the wizard pop on every launch).
        if (HasUsableOperatorConfiguration(settings, dataPath))
        {
            return false;
        }

        return true;
    }
}

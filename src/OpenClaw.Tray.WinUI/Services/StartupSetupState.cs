using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class StartupSetupState
{
    public static bool HasStoredNodeDeviceToken(string dataPath) =>
        DeviceIdentity.HasStoredDeviceToken(dataPath, NullLogger.Instance);

    public static bool CanStartNodeGateway(SettingsManager settings, string dataPath)
    {
        if (!settings.EnableNodeMode)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(settings.Token) ||
               !string.IsNullOrWhiteSpace(settings.BootstrapToken) ||
               HasStoredNodeDeviceToken(dataPath);
    }

    public static bool RequiresSetup(SettingsManager settings, string dataPath)
    {
        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            return false;
        }

        if (settings.EnableNodeMode &&
            (!string.IsNullOrWhiteSpace(settings.BootstrapToken) || HasStoredNodeDeviceToken(dataPath)))
        {
            return false;
        }

        return !settings.EnableMcpServer;
    }
}

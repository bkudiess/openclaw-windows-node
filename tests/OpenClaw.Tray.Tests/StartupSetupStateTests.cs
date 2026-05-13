using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class StartupSetupStateTests
{
    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenNodeHasStoredDeviceToken()
    {
        using var temp = TempSettings.Create();
        StoreNodeDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.True(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenOnlyOperatorTokenExistsForNodeMode()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { EnableNodeMode = true };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenMcpOnlyModeIsEnabled()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { EnableMcpServer = true };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNoAuthOrLocalServerModeExists()
    {
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path);

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
        Assert.False(StartupSetupState.CanStartNodeGateway(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsFalse_WhenOperatorPairedWithRemoteGateway()
    {
        // Scott Hanselman repro: operator mode with a non-default (remote) gateway URL
        // and a stored operator device token — wizard must NOT auto-launch on next start.
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com:443" };

        Assert.False(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenOperatorTokenExistsButGatewayUrlIsDefault()
    {
        // Stale-token guard: a stored operator token alone is not enough. Without a
        // configured non-default gateway URL the app has no target to connect to,
        // so first-run setup should still be offered.
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "ws://localhost:18789" };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void RequiresSetup_ReturnsTrue_WhenNonDefaultGatewayUrlButNoOperatorToken()
    {
        // Inverse guard: a non-default URL alone (no pairing yet) still needs setup.
        using var temp = TempSettings.Create();
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "wss://remote.example.com:443" };

        Assert.True(StartupSetupState.RequiresSetup(settings, temp.Path));
    }

    [Fact]
    public void HasUsableOperatorConfiguration_ReturnsFalse_WhenGatewayUrlIsNullOrWhitespace()
    {
        using var temp = TempSettings.Create();
        StoreDeviceToken(temp.Path);
        var settings = new SettingsManager(temp.Path) { GatewayUrl = "   " };

        Assert.False(StartupSetupState.HasUsableOperatorConfiguration(settings, temp.Path));
    }

    private static void StoreDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceToken("stored-device-token");
    }

    private static void StoreNodeDeviceToken(string dataPath)
    {
        var identity = new DeviceIdentity(dataPath);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("node", "stored-node-token");
    }

    private sealed class TempSettings : IDisposable
    {
        public string Path { get; }

        private TempSettings(string path)
        {
            Path = path;
        }

        public static TempSettings Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openclaw-tray-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempSettings(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}

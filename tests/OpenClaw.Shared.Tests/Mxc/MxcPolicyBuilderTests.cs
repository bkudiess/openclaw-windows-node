using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcPolicyBuilderTests
{
    [Fact]
    public void ForSystemRun_DefaultSettings_DefaultDenyAcrossTheBoard()
    {
        var settings = new SettingsData(); // all defaults
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.Equal(MxcPolicyBuilder.SupportedPolicyVersion, policy.Version);

        Assert.NotNull(policy.Network);
        Assert.False(policy.Network!.AllowOutbound);
        Assert.False(policy.Network.AllowLocalNetwork);

        Assert.NotNull(policy.Ui);
        Assert.False(policy.Ui!.AllowWindows);
        Assert.Equal(ClipboardPolicy.None, policy.Ui.Clipboard);
        Assert.False(policy.Ui.AllowInputInjection);
    }

    [Fact]
    public void ForSystemRun_DeniesSettingsDirectoryPath()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\Users\\test\\AppData\\OpenClawTray");

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        Assert.Contains("C:\\Users\\test\\AppData\\OpenClawTray", policy.Filesystem.DeniedPaths!);
    }

    [Fact]
    public void ForSystemRun_DeniesSshDirectoryByDefault()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        // .ssh path is the home-relative one; verify it's present and ends with ".ssh".
        Assert.Contains(policy.Filesystem.DeniedPaths!, p => p.EndsWith(".ssh"));
    }

    [Fact]
    public void ForSystemRun_AllowOutbound_SetsNetworkFlag()
    {
        var settings = new SettingsData { SystemRunAllowOutbound = true };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Network!.AllowOutbound);
        Assert.False(policy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void ForSystemRun_AllowLocalNetwork_SetsNetworkFlag()
    {
        var settings = new SettingsData { SystemRunAllowLocalNetwork = true };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.False(policy.Network!.AllowOutbound);
        Assert.True(policy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void ForSystemRun_BothNetworkFlags_SetIndependently()
    {
        var settings = new SettingsData
        {
            SystemRunAllowOutbound = true,
            SystemRunAllowLocalNetwork = true,
        };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Network!.AllowOutbound);
        Assert.True(policy.Network.AllowLocalNetwork);
    }

    [Fact]
    public void ForSystemRun_ClearPolicyOnExit_True()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\settings");

        Assert.True(policy.Filesystem!.ClearPolicyOnExit);
    }

    [Fact]
    public void ForSystemRun_NullSettingsDirectory_StillBuildsPolicy()
    {
        var settings = new SettingsData();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDirectoryPath: "");

        // Empty settings dir is filtered; should NOT show up in deniedPaths.
        Assert.NotNull(policy.Filesystem);
        Assert.DoesNotContain(policy.Filesystem!.DeniedPaths!, p => p == string.Empty);
    }
}

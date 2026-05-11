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

    [Fact]
    public void ForSystemRun_ClipboardMode_MapsToClipboardPolicy()
    {
        var none = new SettingsData { SandboxClipboard = SandboxClipboardMode.None };
        var read = new SettingsData { SandboxClipboard = SandboxClipboardMode.Read };
        var write = new SettingsData { SandboxClipboard = SandboxClipboardMode.Write };
        var both = new SettingsData { SandboxClipboard = SandboxClipboardMode.Both };

        Assert.Equal(ClipboardPolicy.None, MxcPolicyBuilder.ForSystemRun(none, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.Read, MxcPolicyBuilder.ForSystemRun(read, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.Write, MxcPolicyBuilder.ForSystemRun(write, "C:\\s").Ui!.Clipboard);
        Assert.Equal(ClipboardPolicy.All, MxcPolicyBuilder.ForSystemRun(both, "C:\\s").Ui!.Clipboard);
    }

    [Fact]
    public void ForSystemRun_DocumentsReadOnly_AppearsInReadonlyPaths()
    {
        var settings = new SettingsData { SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.NotNull(policy.Filesystem!.ReadonlyPaths);
        Assert.Empty(policy.Filesystem.ReadwritePaths!);
        Assert.Contains(policy.Filesystem.ReadonlyPaths!,
            p => p.EndsWith("Documents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DesktopReadWrite_AppearsInReadwritePaths()
    {
        var settings = new SettingsData { SandboxDesktopAccess = SandboxFolderAccess.ReadWrite };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.NotNull(policy.Filesystem!.ReadwritePaths);
        Assert.Contains(policy.Filesystem.ReadwritePaths!,
            p => p.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_DownloadsReadOnly_AppearsInReadonlyPaths()
    {
        var settings = new SettingsData { SandboxDownloadsAccess = SandboxFolderAccess.ReadOnly };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Contains(policy.Filesystem!.ReadonlyPaths!,
            p => p.EndsWith("Downloads", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForSystemRun_CustomFolders_PlacedInRequestedBucket()
    {
        var settings = new SettingsData
        {
            SandboxCustomFolders = new List<SandboxCustomFolder>
            {
                new() { Path = "C:\\Code\\repo", Access = SandboxFolderAccess.ReadOnly },
                new() { Path = "C:\\Scratch", Access = SandboxFolderAccess.ReadWrite },
            }
        };

        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Contains("C:\\Code\\repo", policy.Filesystem!.ReadonlyPaths!);
        Assert.Contains("C:\\Scratch", policy.Filesystem.ReadwritePaths!);
    }

    [Fact]
    public void ForSystemRun_TimeoutMs_PassedThrough()
    {
        var settings = new SettingsData { SandboxTimeoutMs = 60_000 };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Equal(60_000, policy.TimeoutMs);
    }

    [Fact]
    public void ForSystemRun_TimeoutMsZero_TreatedAsUnset()
    {
        var settings = new SettingsData { SandboxTimeoutMs = 0 };
        var policy = MxcPolicyBuilder.ForSystemRun(settings, "C:\\s");

        Assert.Null(policy.TimeoutMs);
    }
}

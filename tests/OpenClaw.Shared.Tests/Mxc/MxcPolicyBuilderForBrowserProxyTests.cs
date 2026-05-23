using System.Linq;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcPolicyBuilderForBrowserProxyTests
{
    [Fact]
    public void ForBrowserProxy_UsesSupportedSchemaVersion()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.Equal(MxcPolicyBuilder.SupportedPolicyVersion, policy.Version);
    }

    [Fact]
    public void ForBrowserProxy_NetworkPolicy_LoopbackOnly()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Network);
        Assert.False(policy.Network!.AllowOutbound);
        Assert.False(policy.Network.AllowLocalNetwork);
        Assert.Equal(8442, policy.Network.LoopbackProxyPort);
    }

    [Fact]
    public void ForBrowserProxy_Ui_AllDeny()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Ui);
        Assert.False(policy.Ui!.AllowWindows);
        Assert.Equal(ClipboardPolicy.None, policy.Ui.Clipboard);
        Assert.False(policy.Ui.AllowInputInjection);
    }

    [Fact]
    public void ForBrowserProxy_Filesystem_AllowedFileRoots_LandInReadonlyPaths()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw", @"C:\downloads" });

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.ReadonlyPaths);
        Assert.Contains(@"C:\Temp\openclaw", policy.Filesystem.ReadonlyPaths!);
        Assert.Contains(@"C:\downloads", policy.Filesystem.ReadonlyPaths!);
    }

    [Fact]
    public void ForBrowserProxy_Filesystem_NoReadwritePathsByDefault()
    {
        // The scratch dir is added by MxcConfigBuilder (per-invocation), not by
        // the policy. Policy itself grants no rw paths — keeps the user's FS
        // strictly read-only from the worker's perspective.
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.ReadwritePaths);
        Assert.Empty(policy.Filesystem.ReadwritePaths!);
    }

    [Fact]
    public void ForBrowserProxy_Filesystem_DeniesSettingsDirectory()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            @"C:\Users\test\AppData\OpenClawTray",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Filesystem);
        Assert.NotNull(policy.Filesystem!.DeniedPaths);
        Assert.Contains(@"C:\Users\test\AppData\OpenClawTray", policy.Filesystem.DeniedPaths!);
    }

    [Fact]
    public void ForBrowserProxy_Filesystem_DeniesSshFolder()
    {
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Filesystem);
        Assert.Contains(policy.Filesystem!.DeniedPaths!, p => p.EndsWith(".ssh"));
    }

    [Fact]
    public void ForBrowserProxy_FiltersAllowedRootThatOverlapsDeny()
    {
        // Defense-in-depth: if a caller supplies an allowedFileRoot that lives
        // under a denied path, the policy strips it so we don't bleed access
        // through. Same invariant ForSystemRun enforces.
        var settingsDir = @"C:\Users\test\AppData\OpenClawTray";
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            settingsDir,
            controlPort: 8442,
            allowedFileRoots: new[] { @"C:\Users\test\AppData\OpenClawTray\sub", @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Filesystem);
        Assert.DoesNotContain(@"C:\Users\test\AppData\OpenClawTray\sub", policy.Filesystem!.ReadonlyPaths!);
        Assert.Contains(@"C:\Temp\openclaw", policy.Filesystem.ReadonlyPaths!);
    }

    [Fact]
    public void ForBrowserProxy_ZeroPort_LeavesProxyUnset()
    {
        // Defensive: caller that passes 0 ends up with no proxy declaration.
        // wxc-exec will then refuse loopback — preferred over silently routing
        // to a default that may not be the control host.
        var policy = MxcPolicyBuilder.ForBrowserProxy(
            new SettingsData(),
            "C:\\settings",
            controlPort: 0,
            allowedFileRoots: new[] { @"C:\Temp\openclaw" });

        Assert.NotNull(policy.Network);
        Assert.Null(policy.Network!.LoopbackProxyPort);
    }
}

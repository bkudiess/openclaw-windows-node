using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the level → settings mapping that the Capabilities page exposes
/// to users. If a future commit wants to change what "Locked Down" means
/// (e.g., re-enabling Run Programs), this test should fail loudly so the
/// behavior change is intentional, not accidental.
/// </summary>
public sealed class SecurityLevelResolverTests : IDisposable
{
    private readonly string _isolatedDir;

    public SecurityLevelResolverTests()
    {
        _isolatedDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Default_FreshSettings_IsRecommended()
    {
        var s = new SettingsManager(_isolatedDir);
        Assert.Equal(SecurityLevel.Recommended, s.SecurityLevel);
    }

    [Fact]
    public void LockedDown_TurnsRunProgramsOff_AndKeepsSandboxOn()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);

        Assert.False(s.NodeSystemRunEnabled);
        Assert.True(s.SystemRunSandboxEnabled);
        Assert.False(s.SystemRunAllowOutbound);
        Assert.False(s.ScreenRecordingConsentGiven);
        Assert.False(s.CameraRecordingConsentGiven);
        Assert.False(s.EnableMcpServer);
        Assert.Equal(SecurityLevel.LockedDown, s.SecurityLevel);
    }

    [Fact]
    public void Recommended_RunProgramsOn_SandboxOn_McpOff()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);

        Assert.True(s.NodeSystemRunEnabled);
        Assert.True(s.SystemRunSandboxEnabled);
        Assert.False(s.SystemRunAllowOutbound);
        Assert.False(s.ScreenRecordingConsentGiven);
        Assert.False(s.CameraRecordingConsentGiven);
        Assert.False(s.EnableMcpServer);
        Assert.Equal(SecurityLevel.Recommended, s.SecurityLevel);
    }

    [Fact]
    public void Trusted_RunProgramsDirect_PreApproved_McpOn()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);

        Assert.True(s.NodeSystemRunEnabled);
        Assert.False(s.SystemRunSandboxEnabled);
        Assert.True(s.SystemRunAllowOutbound);
        Assert.True(s.ScreenRecordingConsentGiven);
        Assert.True(s.CameraRecordingConsentGiven);
        Assert.True(s.EnableMcpServer);
        Assert.Equal(SecurityLevel.Trusted, s.SecurityLevel);
    }

    [Fact]
    public void Custom_DoesNotAlterSettings()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);
        Assert.True(s.SystemRunAllowOutbound);

        // Apply Custom — should be a no-op
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Custom);
        Assert.True(s.SystemRunAllowOutbound);
        // SecurityLevel itself was not changed back to Custom by the no-op
        Assert.Equal(SecurityLevel.Trusted, s.SecurityLevel);
    }

    [Fact]
    public void DriftCount_IsZero_ImmediatelyAfterApply()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));
    }

    [Fact]
    public void DriftCount_IncrementsWhenUserMutatesSetting()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));

        // Single override
        s.NodeSystemRunEnabled = false;
        Assert.Equal(1, SecurityLevelResolver.DriftCount(s));

        // Two overrides
        s.EnableMcpServer = true;
        Assert.Equal(2, SecurityLevelResolver.DriftCount(s));

        // Reset both — drift goes back to zero
        s.NodeSystemRunEnabled = true;
        s.EnableMcpServer = false;
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));
    }

    [Fact]
    public void DriftCount_TreatsCustomAsRecommendedBaseline()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);
        s.SecurityLevel = SecurityLevel.Custom;
        s.NodeSystemRunEnabled = false;

        // Drift is computed against Recommended even though stored level is Custom
        Assert.Equal(1, SecurityLevelResolver.DriftCount(s));
    }

    [Fact]
    public void SecurityLevel_RoundTripsThroughSave()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        s.Save();

        var reloaded = new SettingsManager(_isolatedDir);
        Assert.Equal(SecurityLevel.LockedDown, reloaded.SecurityLevel);
        Assert.False(reloaded.NodeSystemRunEnabled);
    }
}

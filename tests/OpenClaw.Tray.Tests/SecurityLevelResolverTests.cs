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

        // Run programs & sandbox
        Assert.False(s.NodeSystemRunEnabled);
        Assert.True(s.SystemRunSandboxEnabled);
        Assert.False(s.SystemRunAllowOutbound);

        // Every capability off — nothing is exposed remotely
        Assert.False(s.NodeCameraEnabled);
        Assert.False(s.NodeScreenEnabled);
        Assert.False(s.NodeSttEnabled);
        Assert.False(s.NodeLocationEnabled);
        Assert.False(s.NodeBrowserProxyEnabled);
        Assert.False(s.NodeCanvasEnabled);

        // "Always allow" consent flags off
        Assert.False(s.ScreenRecordingConsentGiven);
        Assert.False(s.CameraRecordingConsentGiven);

        // MCP off
        Assert.False(s.EnableMcpServer);

        // Folder access all blocked
        Assert.Null(s.SandboxDocumentsAccess);
        Assert.Null(s.SandboxDownloadsAccess);
        Assert.Null(s.SandboxDesktopAccess);
        Assert.Equal(SandboxClipboardMode.None, s.SandboxClipboard);

        // Timeout and max-output pinned at the conservative defaults
        Assert.Equal(30_000, s.SandboxTimeoutMs);
        Assert.Equal(4L * 1024 * 1024, s.SandboxMaxOutputBytes);

        Assert.Equal(SecurityLevel.LockedDown, s.SecurityLevel);
    }

    [Fact]
    public void Recommended_RunProgramsOn_SandboxOn_McpOff()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);

        // Run programs in container, no outbound
        Assert.True(s.NodeSystemRunEnabled);
        Assert.True(s.SystemRunSandboxEnabled);
        Assert.False(s.SystemRunAllowOutbound);

        // Capabilities on (camera/screen ask each time)
        Assert.True(s.NodeCameraEnabled);
        Assert.True(s.NodeScreenEnabled);
        Assert.True(s.NodeSttEnabled);
        Assert.True(s.NodeLocationEnabled);
        Assert.True(s.NodeBrowserProxyEnabled);
        Assert.True(s.NodeCanvasEnabled);
        Assert.False(s.ScreenRecordingConsentGiven);
        Assert.False(s.CameraRecordingConsentGiven);

        // MCP server off (opt in)
        Assert.False(s.EnableMcpServer);

        // Folder access: all three read only on Recommended — agent can read
        // context but never write to user folders by default.
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDownloadsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDesktopAccess);
        Assert.Equal(SandboxClipboardMode.Read, s.SandboxClipboard);

        // Same conservative timeout and max-output as Locked down
        Assert.Equal(30_000, s.SandboxTimeoutMs);
        Assert.Equal(4L * 1024 * 1024, s.SandboxMaxOutputBytes);

        Assert.Equal(SecurityLevel.Recommended, s.SecurityLevel);
    }

    [Fact]
    public void Trusted_RunProgramsDirect_PreApproved_McpOn()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);

        // Run programs directly with outbound
        Assert.True(s.NodeSystemRunEnabled);
        Assert.False(s.SystemRunSandboxEnabled);
        Assert.True(s.SystemRunAllowOutbound);

        // Everything on, pre-approved
        Assert.True(s.NodeCameraEnabled);
        Assert.True(s.NodeScreenEnabled);
        Assert.True(s.NodeSttEnabled);
        Assert.True(s.NodeLocationEnabled);
        Assert.True(s.NodeBrowserProxyEnabled);
        Assert.True(s.NodeCanvasEnabled);
        Assert.True(s.ScreenRecordingConsentGiven);
        Assert.True(s.CameraRecordingConsentGiven);
        Assert.True(s.EnableMcpServer);

        // Folder access: full read+write
        Assert.Equal(SandboxFolderAccess.ReadWrite, s.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.ReadWrite, s.SandboxDownloadsAccess);
        Assert.Equal(SandboxFolderAccess.ReadWrite, s.SandboxDesktopAccess);
        Assert.Equal(SandboxClipboardMode.Both, s.SandboxClipboard);

        // Trusted gives a longer per-command budget but shares the default
        // 4 MiB output cap (all three presets pin the same value today; the
        // field lives in LevelDefaults so future preset tweaks won't silently
        // skip it).
        Assert.Equal(60_000, s.SandboxTimeoutMs);
        Assert.Equal(4L * 1024 * 1024, s.SandboxMaxOutputBytes);

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
        Assert.Equal(SecurityLevel.LockedDown, reloaded.SecurityBaseLevel);
        Assert.False(reloaded.NodeSystemRunEnabled);
    }

    [Fact]
    public void ApplyTo_WritesBothEffectiveAndBaseLevel()
    {
        // ApplyTo is the "user picked a preset" entry point — both SecurityLevel
        // (effective) and SecurityBaseLevel (anchor) should match the picked level.
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        Assert.Equal(SecurityLevel.LockedDown, s.SecurityLevel);
        Assert.Equal(SecurityLevel.LockedDown, s.SecurityBaseLevel);

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);
        Assert.Equal(SecurityLevel.Trusted, s.SecurityLevel);
        Assert.Equal(SecurityLevel.Trusted, s.SecurityBaseLevel);
    }

    [Fact]
    public void DriftCount_UsesSecurityBaseLevel_NotEffectiveLevel()
    {
        // After a user picks Locked down and toggles one thing, the page sets
        // SecurityLevel=Custom but leaves SecurityBaseLevel=LockedDown.
        // DriftCount must measure against the base, not "fall back to Recommended".
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        Assert.Equal(0, SecurityLevelResolver.DriftCount(s));

        // Simulate the user toggling Camera ON (LockedDown defaults Camera OFF).
        s.NodeCameraEnabled = true;
        // Simulate what the page does on drift:
        s.SecurityLevel = SecurityLevel.Custom;

        // The base is still LockedDown, so Camera=on is 1 unit of drift.
        Assert.Equal(1, SecurityLevelResolver.DriftCount(s));
        Assert.Equal(SecurityLevel.LockedDown, s.SecurityBaseLevel);

        // If DriftCount had wrongly used the effective level (Custom →
        // Recommended fallback), Camera=on would NOT count as drift since
        // Recommended also has Camera on. The fact we got 1 here proves
        // the base-level path is being taken.
    }

    [Fact]
    public void Custom_PreservesBaseLevel_AcrossSave()
    {
        // After drift, the saved file should remember which preset the user
        // picked so a fresh process (or page navigation) keeps the anchor.
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        s.NodeCameraEnabled = true;
        s.SecurityLevel = SecurityLevel.Custom;
        s.Save();

        var reloaded = new SettingsManager(_isolatedDir);
        Assert.Equal(SecurityLevel.Custom, reloaded.SecurityLevel);
        Assert.Equal(SecurityLevel.LockedDown, reloaded.SecurityBaseLevel);
        // Drift still computed against LockedDown, not Recommended.
        Assert.Equal(1, SecurityLevelResolver.DriftCount(reloaded));
    }

    [Fact]
    public void ApplyTo_PreservesCustomFolders_AcrossSwitches()
    {
        // Custom folders are user-curated data, not a preset-driven setting.
        // Switching presets must NEVER wipe the list.
        var s = new SettingsManager(_isolatedDir);
        s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = @"C:\Projects", Access = SandboxFolderAccess.ReadOnly });
        s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = @"D:\work",     Access = SandboxFolderAccess.ReadWrite });

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.LockedDown);
        Assert.Equal(2, s.SandboxCustomFolders.Count);
        Assert.Equal(@"C:\Projects", s.SandboxCustomFolders[0].Path);

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Trusted);
        Assert.Equal(2, s.SandboxCustomFolders.Count);

        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);
        Assert.Equal(2, s.SandboxCustomFolders.Count);
    }

    [Fact]
    public void Recommended_GivesMoreAccessThanLockedDown_LessThanTrusted()
    {
        // Anchors the "balanced" positioning of Recommended against the
        // other two presets.
        var lockedDown = SecurityLevelResolver.DefaultsFor(SecurityLevel.LockedDown);
        var recommended = SecurityLevelResolver.DefaultsFor(SecurityLevel.Recommended);
        var trusted = SecurityLevelResolver.DefaultsFor(SecurityLevel.Trusted);

        // Run programs: off → on → on
        Assert.False(lockedDown.NodeSystemRunEnabled);
        Assert.True(recommended.NodeSystemRunEnabled);
        Assert.True(trusted.NodeSystemRunEnabled);

        // Sandbox: on → on → off
        Assert.True(recommended.SystemRunSandboxEnabled);
        Assert.False(trusted.SystemRunSandboxEnabled);

        // Outbound network: off → off → on
        Assert.False(recommended.SystemRunAllowOutbound);
        Assert.True(trusted.SystemRunAllowOutbound);

        // Capability count enabled: 0 → 6 → 6
        Assert.Equal(0, CapsOn(lockedDown));
        Assert.Equal(6, CapsOn(recommended));
        Assert.Equal(6, CapsOn(trusted));

        // Clipboard: None < Read < Both
        Assert.Equal(SandboxClipboardMode.None, lockedDown.SandboxClipboard);
        Assert.Equal(SandboxClipboardMode.Read, recommended.SandboxClipboard);
        Assert.Equal(SandboxClipboardMode.Both, trusted.SandboxClipboard);

        static int CapsOn(SecurityLevelResolver.LevelDefaults d) =>
            (d.NodeCameraEnabled       ? 1 : 0) +
            (d.NodeScreenEnabled       ? 1 : 0) +
            (d.NodeSttEnabled          ? 1 : 0) +
            (d.NodeLocationEnabled     ? 1 : 0) +
            (d.NodeBrowserProxyEnabled ? 1 : 0) +
            (d.NodeCanvasEnabled       ? 1 : 0);
    }

    [Fact]
    public void Recommended_AllFoldersReadOnly()
    {
        var s = new SettingsManager(_isolatedDir);
        SecurityLevelResolver.ApplyTo(s, SecurityLevel.Recommended);

        // Reasoning: on the balanced preset the agent should be able to read
        // context from the usual user folders, but not silently write or
        // overwrite anything. Users who want write access flip it per folder
        // and the level falls to "Custom".
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDownloadsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, s.SandboxDesktopAccess);
    }
}

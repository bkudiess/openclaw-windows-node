using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Maps <see cref="SecurityLevel"/> values to concrete per-capability
/// defaults for the current settings shape. Pure functions only — applies
/// the level to a SettingsManager via <see cref="ApplyTo"/>, computes the
/// drift between current settings and a level via <see cref="DriftCount"/>.
///
/// This is the only place that knows what a level "means". When new
/// settings join the level scheme (e.g., per-capability scoping in a
/// future phase), update <see cref="DefaultsFor"/>, <see cref="ApplyTo"/>,
/// and <see cref="DriftCount"/> together.
///
/// NOTE: <c>SandboxCustomFolders</c> is intentionally *not* in the preset.
/// User-curated folder grants persist across preset switches — switching
/// to Locked Down disables Run Programs (so the list is dormant) but never
/// wipes it.
/// </summary>
internal static class SecurityLevelResolver
{
    /// <summary>
    /// Snapshot of the settings driven by the security level. Lower-cased
    /// names mirror <see cref="SettingsManager"/>'s public properties.
    /// </summary>
    internal readonly record struct LevelDefaults(
        // Run programs + sandbox core
        bool NodeSystemRunEnabled,
        bool SystemRunSandboxEnabled,
        bool SystemRunAllowOutbound,
        // Capability toggles
        bool NodeCameraEnabled,
        bool NodeScreenEnabled,
        bool NodeSttEnabled,
        bool NodeLocationEnabled,
        bool NodeBrowserProxyEnabled,
        bool NodeCanvasEnabled,
        // Camera/Screen "always allow" consent flags
        bool ScreenRecordingConsentGiven,
        bool CameraRecordingConsentGiven,
        // Local MCP HTTP server
        bool EnableMcpServer,
        // Sandbox folder access (null = blocked)
        SandboxFolderAccess? SandboxDocumentsAccess,
        SandboxFolderAccess? SandboxDownloadsAccess,
        SandboxFolderAccess? SandboxDesktopAccess,
        // Sandbox clipboard policy
        SandboxClipboardMode SandboxClipboard,
        // Sandbox runtime ceiling (ms)
        int SandboxTimeoutMs);

    /// <summary>
    /// Returns the canonical defaults for the given level.
    /// </summary>
    public static LevelDefaults DefaultsFor(SecurityLevel level) => level switch
    {
        // 🔒 Locked down — nothing exposed remotely. Run programs off, every
        // capability off. The user must explicitly turn things on.
        SecurityLevel.LockedDown => new LevelDefaults(
            NodeSystemRunEnabled: false,
            SystemRunSandboxEnabled: true,
            SystemRunAllowOutbound: false,
            NodeCameraEnabled: false,
            NodeScreenEnabled: false,
            NodeSttEnabled: false,
            NodeLocationEnabled: false,
            NodeBrowserProxyEnabled: false,
            NodeCanvasEnabled: false,
            ScreenRecordingConsentGiven: false,
            CameraRecordingConsentGiven: false,
            EnableMcpServer: false,
            SandboxDocumentsAccess: null,
            SandboxDownloadsAccess: null,
            SandboxDesktopAccess: null,
            SandboxClipboard: SandboxClipboardMode.None,
            SandboxTimeoutMs: 30_000),

        // ⚠️ Unprotected (Trusted) — power-user profile. Direct execution,
        // every capability on, full sandbox access, MCP server up.
        SecurityLevel.Trusted => new LevelDefaults(
            NodeSystemRunEnabled: true,
            SystemRunSandboxEnabled: false,
            SystemRunAllowOutbound: true,
            NodeCameraEnabled: true,
            NodeScreenEnabled: true,
            NodeSttEnabled: true,
            NodeLocationEnabled: true,
            NodeBrowserProxyEnabled: true,
            NodeCanvasEnabled: true,
            ScreenRecordingConsentGiven: true,
            CameraRecordingConsentGiven: true,
            EnableMcpServer: true,
            SandboxDocumentsAccess: SandboxFolderAccess.ReadWrite,
            SandboxDownloadsAccess: SandboxFolderAccess.ReadWrite,
            SandboxDesktopAccess: SandboxFolderAccess.ReadWrite,
            SandboxClipboard: SandboxClipboardMode.Both,
            SandboxTimeoutMs: 60_000),

        // 🛡️ Recommended (Balanced) — and Custom for completeness.
        // Run programs on in container; capabilities on but camera/screen ask
        // each time; folder access read only across the board so the agent
        // can read context but never write to user folders; clipboard
        // read-only too. Outbound network off.
        _ => new LevelDefaults(
            NodeSystemRunEnabled: true,
            SystemRunSandboxEnabled: true,
            SystemRunAllowOutbound: false,
            NodeCameraEnabled: true,
            NodeScreenEnabled: true,
            NodeSttEnabled: true,
            NodeLocationEnabled: true,
            NodeBrowserProxyEnabled: true,
            NodeCanvasEnabled: true,
            ScreenRecordingConsentGiven: false,
            CameraRecordingConsentGiven: false,
            EnableMcpServer: false,
            SandboxDocumentsAccess: SandboxFolderAccess.ReadOnly,
            SandboxDownloadsAccess: SandboxFolderAccess.ReadOnly,
            SandboxDesktopAccess: SandboxFolderAccess.ReadOnly,
            SandboxClipboard: SandboxClipboardMode.Read,
            SandboxTimeoutMs: 30_000),
    };

    /// <summary>
    /// Applies the level's defaults to <paramref name="settings"/> in-memory.
    /// Does NOT save — caller decides when to persist. Custom folders are
    /// intentionally preserved across switches.
    ///
    /// Writes BOTH <see cref="SettingsManager.SecurityLevel"/> AND
    /// <see cref="SettingsManager.SecurityBaseLevel"/> so the user's intent
    /// (the preset they picked) is the same as the effective level until
    /// they drift. Per-setting toggle handlers later flip SecurityLevel to
    /// Custom on drift while keeping SecurityBaseLevel anchored.
    /// </summary>
    public static void ApplyTo(SettingsManager settings, SecurityLevel level)
    {
        if (level == SecurityLevel.Custom) return; // no-op
        var d = DefaultsFor(level);
        settings.NodeSystemRunEnabled        = d.NodeSystemRunEnabled;
        settings.SystemRunSandboxEnabled     = d.SystemRunSandboxEnabled;
        settings.SystemRunAllowOutbound      = d.SystemRunAllowOutbound;
        settings.NodeCameraEnabled           = d.NodeCameraEnabled;
        settings.NodeScreenEnabled           = d.NodeScreenEnabled;
        settings.NodeSttEnabled              = d.NodeSttEnabled;
        settings.NodeLocationEnabled         = d.NodeLocationEnabled;
        settings.NodeBrowserProxyEnabled     = d.NodeBrowserProxyEnabled;
        settings.NodeCanvasEnabled           = d.NodeCanvasEnabled;
        settings.ScreenRecordingConsentGiven = d.ScreenRecordingConsentGiven;
        settings.CameraRecordingConsentGiven = d.CameraRecordingConsentGiven;
        settings.EnableMcpServer             = d.EnableMcpServer;
        settings.SandboxDocumentsAccess      = d.SandboxDocumentsAccess;
        settings.SandboxDownloadsAccess      = d.SandboxDownloadsAccess;
        settings.SandboxDesktopAccess        = d.SandboxDesktopAccess;
        settings.SandboxClipboard            = d.SandboxClipboard;
        settings.SandboxTimeoutMs            = d.SandboxTimeoutMs;
        settings.SecurityLevel               = level;
        settings.SecurityBaseLevel           = level;
    }

    /// <summary>
    /// Counts how many of the level-driven settings differ from the
    /// canonical defaults of the user's stored <see cref="SettingsManager.SecurityBaseLevel"/>.
    /// Returns 0 when settings match the base level exactly.
    /// </summary>
    public static int DriftCount(SettingsManager settings)
    {
        // SecurityBaseLevel is the user's chosen preset — stays anchored even
        // when SecurityLevel flips to Custom. Defensive guard: if a corrupted
        // settings file deserialized to Custom here somehow, fall back to
        // Recommended (matches the SettingsManager.Load() migration path).
        var baseLevel = settings.SecurityBaseLevel == SecurityLevel.Custom
            ? SecurityLevel.Recommended
            : settings.SecurityBaseLevel;
        var d = DefaultsFor(baseLevel);
        var n = 0;
        if (settings.NodeSystemRunEnabled        != d.NodeSystemRunEnabled)        n++;
        if (settings.SystemRunSandboxEnabled     != d.SystemRunSandboxEnabled)     n++;
        if (settings.SystemRunAllowOutbound      != d.SystemRunAllowOutbound)      n++;
        if (settings.NodeCameraEnabled           != d.NodeCameraEnabled)           n++;
        if (settings.NodeScreenEnabled           != d.NodeScreenEnabled)           n++;
        if (settings.NodeSttEnabled              != d.NodeSttEnabled)              n++;
        if (settings.NodeLocationEnabled         != d.NodeLocationEnabled)         n++;
        if (settings.NodeBrowserProxyEnabled     != d.NodeBrowserProxyEnabled)     n++;
        if (settings.NodeCanvasEnabled           != d.NodeCanvasEnabled)           n++;
        if (settings.ScreenRecordingConsentGiven != d.ScreenRecordingConsentGiven) n++;
        if (settings.CameraRecordingConsentGiven != d.CameraRecordingConsentGiven) n++;
        if (settings.EnableMcpServer             != d.EnableMcpServer)             n++;
        if (settings.SandboxDocumentsAccess      != d.SandboxDocumentsAccess)      n++;
        if (settings.SandboxDownloadsAccess      != d.SandboxDownloadsAccess)      n++;
        if (settings.SandboxDesktopAccess        != d.SandboxDesktopAccess)        n++;
        if (settings.SandboxClipboard            != d.SandboxClipboard)            n++;
        if (settings.SandboxTimeoutMs            != d.SandboxTimeoutMs)            n++;
        return n;
    }
}

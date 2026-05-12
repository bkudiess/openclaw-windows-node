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
/// </summary>
internal static class SecurityLevelResolver
{
    /// <summary>
    /// Snapshot of the settings driven by the security level. Lower-cased
    /// names mirror <see cref="SettingsManager"/>'s public properties.
    /// </summary>
    internal readonly record struct LevelDefaults(
        bool NodeSystemRunEnabled,
        bool SystemRunSandboxEnabled,
        bool SystemRunAllowOutbound,
        bool ScreenRecordingConsentGiven,
        bool CameraRecordingConsentGiven,
        bool EnableMcpServer);

    /// <summary>
    /// Returns the canonical defaults for the given level.
    /// </summary>
    public static LevelDefaults DefaultsFor(SecurityLevel level) => level switch
    {
        SecurityLevel.LockedDown => new LevelDefaults(
            NodeSystemRunEnabled: false,
            SystemRunSandboxEnabled: true,
            SystemRunAllowOutbound: false,
            ScreenRecordingConsentGiven: false,
            CameraRecordingConsentGiven: false,
            EnableMcpServer: false),

        SecurityLevel.Trusted => new LevelDefaults(
            NodeSystemRunEnabled: true,
            SystemRunSandboxEnabled: false,
            SystemRunAllowOutbound: true,
            ScreenRecordingConsentGiven: true,
            CameraRecordingConsentGiven: true,
            EnableMcpServer: true),

        // Recommended (and Custom — for completeness, though we never
        // actively "apply" Custom).
        _ => new LevelDefaults(
            NodeSystemRunEnabled: true,
            SystemRunSandboxEnabled: true,
            SystemRunAllowOutbound: false,
            ScreenRecordingConsentGiven: false,
            CameraRecordingConsentGiven: false,
            EnableMcpServer: false),
    };

    /// <summary>
    /// Applies the level's defaults to <paramref name="settings"/> in-memory.
    /// Does NOT save — caller decides when to persist.
    /// </summary>
    public static void ApplyTo(SettingsManager settings, SecurityLevel level)
    {
        if (level == SecurityLevel.Custom) return; // no-op
        var d = DefaultsFor(level);
        settings.NodeSystemRunEnabled       = d.NodeSystemRunEnabled;
        settings.SystemRunSandboxEnabled    = d.SystemRunSandboxEnabled;
        settings.SystemRunAllowOutbound     = d.SystemRunAllowOutbound;
        settings.ScreenRecordingConsentGiven = d.ScreenRecordingConsentGiven;
        settings.CameraRecordingConsentGiven = d.CameraRecordingConsentGiven;
        settings.EnableMcpServer            = d.EnableMcpServer;
        settings.SecurityLevel              = level;
    }

    /// <summary>
    /// Counts how many of the level-driven settings differ from the
    /// canonical defaults of the user's stored base level. Returns 0 when
    /// settings match the base level exactly.
    /// </summary>
    public static int DriftCount(SettingsManager settings)
    {
        var baseLevel = settings.SecurityLevel == SecurityLevel.Custom
            ? SecurityLevel.Recommended
            : settings.SecurityLevel;
        var d = DefaultsFor(baseLevel);
        var n = 0;
        if (settings.NodeSystemRunEnabled       != d.NodeSystemRunEnabled)       n++;
        if (settings.SystemRunSandboxEnabled    != d.SystemRunSandboxEnabled)    n++;
        if (settings.SystemRunAllowOutbound     != d.SystemRunAllowOutbound)     n++;
        if (settings.ScreenRecordingConsentGiven != d.ScreenRecordingConsentGiven) n++;
        if (settings.CameraRecordingConsentGiven != d.CameraRecordingConsentGiven) n++;
        if (settings.EnableMcpServer            != d.EnableMcpServer)            n++;
        return n;
    }
}

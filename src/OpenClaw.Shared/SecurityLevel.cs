namespace OpenClaw.Shared;

/// <summary>
/// User-facing security profile that resolves to defaults for several
/// privacy/security-related settings on this PC. Picked from the top of
/// the Capabilities page; per-row overrides flip the effective state to
/// <see cref="Custom"/> until the user resets.
///
/// The mapping from level → per-capability settings lives in
/// <c>OpenClawTray.Services.SecurityLevelResolver</c>.
/// </summary>
public enum SecurityLevel
{
    /// <summary>
    /// Maximum protection. Run Programs is OFF. Sandbox enforced.
    /// Camera/screen always prompt. MCP server off.
    /// </summary>
    LockedDown,

    /// <summary>
    /// Default for new installs. Safe defaults for most users:
    /// Run Programs ON in container; camera/screen ask once; MCP off
    /// (user can opt in).
    /// </summary>
    Recommended,

    /// <summary>
    /// Power-user / developer profile. Run Programs runs directly.
    /// Camera/screen pre-approved. Outbound network from sandbox allowed.
    /// MCP server on. Switching to this level requires confirmation.
    /// </summary>
    Trusted,

    /// <summary>
    /// Reserved sentinel — not used as a stored level. The drift counter
    /// reports drift relative to the user's chosen base level
    /// (LockedDown/Recommended/Trusted); when drift > 0 the UI badges
    /// "&lt;BaseLevel&gt; + N changes".
    /// </summary>
    Custom
}

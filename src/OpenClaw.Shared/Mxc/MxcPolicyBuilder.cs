namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Pure function: <see cref="SettingsData"/> + capability name → <see cref="SandboxPolicy"/>.
/// </summary>
/// <remarks>
/// Slice 1 scope is system.run only. Future slices extend this with per-capability
/// AppContainer cap declarations (Slice 8 — Q-NESTED-APPCONTAINER). The structure
/// keeps the function signature stable so v2 work is internal-only.
///
/// Policy decisions in Slice 1:
/// <list type="bullet">
/// <item><c>readonlyPaths</c> — empty for now; <c>run-command.cjs</c> populates it
/// via <c>getAvailableToolsPolicy()</c> at the Node side. (Future: port to C#.)</item>
/// <item><c>readwritePaths</c> — single per-call temp directory. <c>run-command.cjs</c>
/// fills it via <c>getTemporaryFilesPolicy()</c>.</item>
/// <item><c>deniedPaths</c> — settings directory (protect MCP token, gateway creds,
/// ElevenLabs key). Plus <c>~/.ssh</c> as a defense-in-depth default.</item>
/// <item><c>network.allowOutbound</c> — bound by <see cref="SettingsData.SystemRunAllowOutbound"/>.</item>
/// <item><c>ui</c> — default-deny across the board; shell exec doesn't need windows.</item>
/// </list>
/// </remarks>
public static class MxcPolicyBuilder
{
    /// <summary>
    /// Policy schema version targeted by Slice 1. Per the @microsoft/mxc-sdk validator,
    /// this must be in the supported range (currently MIN 0.4.0-alpha, SUPPORTED 0.5.0-alpha).
    /// </summary>
    public const string SupportedPolicyVersion = "0.4.0-alpha";

    /// <summary>
    /// Build the policy for a system.run invocation given current settings.
    /// </summary>
    /// <param name="settings">Live settings snapshot from <see cref="SettingsManager"/> (or test stub).</param>
    /// <param name="settingsDirectoryPath">
    /// Path to <see cref="SettingsManager.SettingsDirectoryPath"/>. Passed in (rather than
    /// read statically) so tests can isolate via <c>OPENCLAW_TRAY_DATA_DIR</c>.
    /// </param>
    public static SandboxPolicy ForSystemRun(SettingsData settings, string settingsDirectoryPath)
    {
        var deniedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(settingsDirectoryPath))
            deniedPaths.Add(settingsDirectoryPath);

        var sshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
        if (!string.IsNullOrWhiteSpace(sshPath))
            deniedPaths.Add(sshPath);

        return new SandboxPolicy(
            Version: SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: Array.Empty<string>(),
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: deniedPaths,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(
                AllowOutbound: settings.SystemRunAllowOutbound,
                AllowLocalNetwork: settings.SystemRunAllowLocalNetwork),
            Ui: new UiPolicy(
                AllowWindows: false,
                Clipboard: ClipboardPolicy.None,
                AllowInputInjection: false),
            TimeoutMs: null);
    }
}

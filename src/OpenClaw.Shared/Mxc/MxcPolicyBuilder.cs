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

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshPath = Path.Combine(userProfile, ".ssh");
        if (!string.IsNullOrWhiteSpace(sshPath))
            deniedPaths.Add(sshPath);

        var readonlyPaths = new List<string>();
        var readwritePaths = new List<string>();

        AddWellKnownFolder(Environment.SpecialFolder.MyDocuments, settings.SandboxDocumentsAccess, readonlyPaths, readwritePaths);
        AddWellKnownFolder(Environment.SpecialFolder.Desktop, settings.SandboxDesktopAccess, readonlyPaths, readwritePaths);
        AddDownloadsFolder(userProfile, settings.SandboxDownloadsAccess, readonlyPaths, readwritePaths);

        if (settings.SandboxCustomFolders is { Count: > 0 } customFolders)
        {
            foreach (var folder in customFolders)
            {
                if (string.IsNullOrWhiteSpace(folder.Path)) continue;
                if (folder.Access == SandboxFolderAccess.ReadWrite)
                    readwritePaths.Add(folder.Path);
                else
                    readonlyPaths.Add(folder.Path);
            }
        }

        return new SandboxPolicy(
            Version: SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: readwritePaths,
                ReadonlyPaths: readonlyPaths,
                DeniedPaths: deniedPaths,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(
                AllowOutbound: settings.SystemRunAllowOutbound,
                // LAN access (privateNetworkClientServer capability) intentionally not
                // exposed: MXC team confirmed only internetClient is validated today.
                // The setting field is retained for forward compat but ignored here.
                AllowLocalNetwork: false),
            Ui: new UiPolicy(
                AllowWindows: false,
                Clipboard: MapClipboard(settings.SandboxClipboard),
                AllowInputInjection: false),
            TimeoutMs: settings.SandboxTimeoutMs > 0 ? settings.SandboxTimeoutMs : null);
    }

    private static void AddWellKnownFolder(
        Environment.SpecialFolder folder,
        SandboxFolderAccess? access,
        List<string> readonlyPaths,
        List<string> readwritePaths)
    {
        if (access is null) return;
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (access == SandboxFolderAccess.ReadWrite) readwritePaths.Add(path);
        else readonlyPaths.Add(path);
    }

    private static void AddDownloadsFolder(
        string userProfile,
        SandboxFolderAccess? access,
        List<string> readonlyPaths,
        List<string> readwritePaths)
    {
        if (access is null) return;
        // .NET has no SpecialFolder.Downloads. Use the Win32 known-folder API so we
        // honor user redirection (e.g., OneDrive\Downloads). Fall back to the
        // %USERPROFILE%\Downloads convention if the API isn't available.
        var path = ResolveKnownFolderDownloads() ?? Path.Combine(userProfile, "Downloads");
        if (access == SandboxFolderAccess.ReadWrite) readwritePaths.Add(path);
        else readonlyPaths.Add(path);
    }

    private static readonly Guid s_folderIdDownloads =
        new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? ResolveKnownFolderDownloads()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hr = SHGetKnownFolderPath(s_folderIdDownloads, 0, IntPtr.Zero, out var ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr); }
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHGetKnownFolderPath(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);

    private static ClipboardPolicy MapClipboard(SandboxClipboardMode mode) => mode switch
    {
        SandboxClipboardMode.Read => ClipboardPolicy.Read,
        SandboxClipboardMode.Write => ClipboardPolicy.Write,
        SandboxClipboardMode.Both => ClipboardPolicy.All,
        _ => ClipboardPolicy.None,
    };
}

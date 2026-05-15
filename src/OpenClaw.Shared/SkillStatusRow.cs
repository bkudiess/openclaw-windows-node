using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenClawTray.Windows;

/// <summary>
/// Snapshot of a single skill's status as reported by the gateway's <c>skills.status</c> RPC.
/// Maps 1:1 to the openclaw core's <c>SkillStatusEntry</c> shape. Mirrors how
/// <c>apps/macos/Sources/OpenClaw/SkillsSettings.swift</c> models <c>SkillStatus</c>.
/// </summary>
public sealed class SkillStatusRow
{
    public string Id { get; init; } = "";
    public string SkillKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Emoji { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";
    public string Homepage { get; init; } = "";
    public string PrimaryEnv { get; init; } = "";

    /// <summary>Gateway-controlled flag: skill is admin-disabled in config.</summary>
    public bool Disabled { get; init; }
    /// <summary>Gateway-computed flag: all required bins + env + config present.</summary>
    public bool Eligible { get; init; }
    public bool Bundled { get; init; }

    public IReadOnlyList<string> MissingBins { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingEnv { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingConfig { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SkillInstallOption> Install { get; init; } = Array.Empty<SkillInstallOption>();
    public IReadOnlyList<SkillConfigCheck> ConfigChecks { get; init; } = Array.Empty<SkillConfigCheck>();

    /// <summary>Computed render-state. See <see cref="SkillRowState"/> for the state machine.</summary>
    public SkillRowState State => ComputeState();

    /// <summary>
    /// Mac-compatible source label. Mirrors the mapping in <c>SkillsSettings.swift</c>.
    /// </summary>
    public string SourceLabel => Source switch
    {
        "openclaw-bundled" => "Bundled",
        "openclaw-managed" => "Managed",
        "openclaw-workspace" => "Workspace",
        "openclaw-extra" => "Extra",
        "openclaw-plugin" => "Plugin",
        "windows-node" => "Node",
        _ => Source,
    };

    /// <summary>Display name with emoji prefix when present.</summary>
    public string DisplayName => string.IsNullOrEmpty(Emoji) ? Name : $"{Emoji} {Name}";

    private SkillRowState ComputeState()
    {
        if (Disabled) return SkillRowState.Disabled;
        if (Eligible && MissingBins.Count == 0 && MissingEnv.Count == 0 && MissingConfig.Count == 0)
        {
            return SkillRowState.Ready;
        }
        if (MissingBins.Count > 0 && InstallOptionsForMissingBins.Count > 0)
        {
            return SkillRowState.NeedsInstall;
        }
        if (MissingEnv.Count > 0)
        {
            return SkillRowState.NeedsEnv;
        }
        return SkillRowState.NeedsSetup;
    }

    /// <summary>
    /// Subset of <see cref="Install"/> whose bins overlap with <see cref="MissingBins"/>.
    /// An install option with no declared bins is always relevant.
    /// </summary>
    public IReadOnlyList<SkillInstallOption> InstallOptionsForMissingBins
    {
        get
        {
            if (MissingBins.Count == 0) return Array.Empty<SkillInstallOption>();
            var missing = new HashSet<string>(MissingBins, StringComparer.OrdinalIgnoreCase);
            var matches = new List<SkillInstallOption>();
            foreach (var opt in Install)
            {
                if (opt.Bins.Count == 0)
                {
                    matches.Add(opt);
                    continue;
                }
                foreach (var bin in opt.Bins)
                {
                    if (missing.Contains(bin))
                    {
                        matches.Add(opt);
                        break;
                    }
                }
            }
            return matches;
        }
    }
}

public sealed class SkillInstallOption
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public IReadOnlyList<string> Bins { get; init; } = Array.Empty<string>();
}

public sealed class SkillConfigCheck
{
    public string Path { get; init; } = "";
    public bool Satisfied { get; init; }
    public string ValueDisplay { get; init; } = "";
}

public enum SkillRowState
{
    /// <summary>All requirements met. Render enable/disable toggle.</summary>
    Ready,
    /// <summary>Missing binaries with at least one install option. Render install button(s).</summary>
    NeedsInstall,
    /// <summary>Missing env vars. Render "Set ENV" / "Set API Key" button(s).</summary>
    NeedsEnv,
    /// <summary>Admin-disabled in config. Render "Disabled in config" label.</summary>
    Disabled,
    /// <summary>Other not-ready cases (missing config without install option, etc.). Render diagnostic summary.</summary>
    NeedsSetup,
}

public enum SkillsFilter
{
    All,
    Ready,
    NeedsSetup,
    Disabled,
}

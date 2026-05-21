using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Detects when the gateway's <c>node.list</c> response for THIS PC has
/// drifted from what our local <see cref="WindowsNodeClient"/> has actually
/// registered. We do NOT mutate the displayed list — the gateway remains
/// the source of truth. We only emit a tagged log line so operators can
/// see at a glance when the gateway is stale (re-pair duplicates,
/// first-pair capability snapshots, wrong <c>connected</c> flags).
/// </summary>
internal static class GatewayDivergenceChecker
{
    /// <summary>Result of comparing the gateway's view of our PC to the live local registration.</summary>
    public sealed record DivergenceReport(
        int DuplicateSelfEntries,
        bool MissingFromGateway,
        bool ConnectedFlagMismatch,
        bool? GatewayConnected,
        bool LiveConnected,
        string[] CapsOnlyInLocal,
        string[] CapsOnlyInGateway,
        string[] CommandsOnlyInLocal,
        string[] CommandsOnlyInGateway,
        int GatewayCommandCount,
        int LiveCommandCount)
    {
        /// <summary>True when anything looks off — caller can short-circuit logging when in sync.</summary>
        public bool HasAnyDrift =>
            DuplicateSelfEntries > 1 ||
            MissingFromGateway ||
            ConnectedFlagMismatch ||
            CapsOnlyInLocal.Length > 0 ||
            CapsOnlyInGateway.Length > 0 ||
            CommandsOnlyInLocal.Length > 0 ||
            CommandsOnlyInGateway.Length > 0;
    }

    /// <summary>
    /// Returns a divergence report, or null when the inputs aren't comparable
    /// (e.g., we don't know our own NodeId yet, or there's no live local
    /// snapshot to compare to).
    /// </summary>
    public static DivergenceReport? Compare(
        GatewayNodeInfo[] gatewayNodes,
        string? localNodeId,
        GatewayNodeInfo? liveLocal,
        bool liveConnected)
    {
        if (gatewayNodes == null) gatewayNodes = Array.Empty<GatewayNodeInfo>();
        if (liveLocal == null || string.IsNullOrWhiteSpace(localNodeId)) return null;

        var selfMatches = gatewayNodes
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.NodeId) &&
                        string.Equals(n.NodeId, localNodeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missing = selfMatches.Count == 0;

        // Pick the freshest-looking gateway record (max command count) for
        // the field-by-field comparison when there are duplicates.
        var gatewaySelf = selfMatches
            .OrderByDescending(n => n.Commands?.Count ?? 0)
            .FirstOrDefault();

        var gatewayCaps = gatewaySelf?.Capabilities?.ToArray() ?? Array.Empty<string>();
        var gatewayCmds = gatewaySelf?.Commands?.ToArray() ?? Array.Empty<string>();
        var liveCaps = liveLocal.Capabilities?.ToArray() ?? Array.Empty<string>();
        var liveCmds = liveLocal.Commands?.ToArray() ?? Array.Empty<string>();

        var capsOnlyLocal = liveCaps.Except(gatewayCaps, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var capsOnlyGateway = gatewayCaps.Except(liveCaps, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var cmdsOnlyLocal = liveCmds.Except(gatewayCmds, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var cmdsOnlyGateway = gatewayCmds.Except(liveCmds, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        var connectedMismatch = !missing && gatewaySelf!.IsOnline != liveConnected;

        return new DivergenceReport(
            DuplicateSelfEntries: selfMatches.Count,
            MissingFromGateway: missing,
            ConnectedFlagMismatch: connectedMismatch,
            GatewayConnected: gatewaySelf?.IsOnline,
            LiveConnected: liveConnected,
            CapsOnlyInLocal: capsOnlyLocal,
            CapsOnlyInGateway: capsOnlyGateway,
            CommandsOnlyInLocal: cmdsOnlyLocal,
            CommandsOnlyInGateway: cmdsOnlyGateway,
            GatewayCommandCount: gatewayCmds.Length,
            LiveCommandCount: liveCmds.Length);
    }

    /// <summary>Builds a single human-readable log line from a divergence report.</summary>
    public static string FormatLogLine(DivergenceReport report)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        var parts = new List<string>();
        if (report.MissingFromGateway)
            parts.Add("MISSING_FROM_GATEWAY=true");
        if (report.DuplicateSelfEntries > 1)
            parts.Add($"duplicate_self_entries={report.DuplicateSelfEntries}");
        if (report.ConnectedFlagMismatch)
            parts.Add($"connected_mismatch (gateway={report.GatewayConnected?.ToString().ToLowerInvariant() ?? "null"}, live={report.LiveConnected.ToString().ToLowerInvariant()})");
        if (report.CapsOnlyInLocal.Length > 0)
            parts.Add($"caps_only_in_local=[{string.Join(",", report.CapsOnlyInLocal)}]");
        if (report.CapsOnlyInGateway.Length > 0)
            parts.Add($"caps_only_in_gateway=[{string.Join(",", report.CapsOnlyInGateway)}]");
        if (report.CommandsOnlyInLocal.Length > 0)
            parts.Add($"cmds_only_in_local(count={report.CommandsOnlyInLocal.Length})=[{string.Join(",", report.CommandsOnlyInLocal)}]");
        if (report.CommandsOnlyInGateway.Length > 0)
            parts.Add($"cmds_only_in_gateway(count={report.CommandsOnlyInGateway.Length})=[{string.Join(",", report.CommandsOnlyInGateway)}]");
        parts.Add($"totals(gateway_cmds={report.GatewayCommandCount}, live_cmds={report.LiveCommandCount})");
        return string.Join(" | ", parts);
    }
}

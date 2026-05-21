using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Pure helper that decides which capability categories to surface in the
/// tray UI (ConnectionPage chips, providing-string, InstancesPage detail
/// counters) and how to render each one.
///
/// "Exposed" = the set the node is currently advertising to the gateway —
/// not the set of toggles the user could flip. When the node is connected
/// and has a live registration snapshot, we use that as the source of truth
/// (so Connection and Instances always agree). When we don't have a live
/// snapshot (e.g. the node hasn't connected yet), we fall back to the
/// settings toggles via <see cref="NodeCapabilityGating"/> so the chip set
/// reflects what *will* be advertised on the next reconnect.
///
/// Either way, a category is only listed if it's actively being exposed —
/// flipping a toggle off makes its chip disappear, it doesn't grey-out.
/// Always-on categories (<c>app</c>, <c>device</c>) are listed too so the
/// chip count matches what InstancesPage shows for the same node.
/// </summary>
internal static class NodeCapabilityDisplay
{
    // Canonical display order: primary toggle first, then user-togglable
    // optional capabilities, then always-on infrastructure capabilities at
    // the end. Anything not in this list sorts alphabetically after.
    private static readonly string[] CanonicalOrder =
    [
        "system",
        "browser",
        "camera",
        "canvas",
        "screen",
        "location",
        "tts",
        "stt",
        "app",
        "device",
    ];

    /// <summary>
    /// Returns the ordered list of capability categories the node is
    /// exposing. Prefer <paramref name="liveCategories"/> (the actual
    /// handshake registration); fall back to settings-derived list when
    /// the node hasn't connected yet.
    /// </summary>
    public static IReadOnlyList<string> BuildExposedCategories(
        IReadOnlyList<string>? liveCategories,
        SettingsManager? settings)
    {
        IEnumerable<string> source;
        if (liveCategories != null && liveCategories.Count > 0)
        {
            source = liveCategories;
        }
        else
        {
            source = BuildFromSettings(settings);
        }

        return NormalizeAndOrder(source);
    }

    /// <summary>
    /// Returns the categories the gateway thinks this node is exposing,
    /// sourced from its <c>node.list</c> self-entry. Use this when the
    /// surface should reflect the gateway's view (matching the tray menu
    /// flyout) rather than the live local registration. When the gateway
    /// list hasn't arrived yet, falls back to the settings-derived list
    /// so the card isn't blank on initial connect. When the gateway has
    /// a list but no entry for us, returns an empty list — same as the
    /// tray menu would render.
    /// </summary>
    public static IReadOnlyList<string> BuildGatewayViewCategories(
        IReadOnlyList<GatewayNodeInfo>? gatewayNodes,
        string? localNodeId,
        SettingsManager? settingsFallback)
    {
        // Pre-connect: no gateway list yet → fall back to settings so the
        // card has something to show during initial connection.
        if (gatewayNodes == null || gatewayNodes.Count == 0)
            return BuildExposedCategories(Array.Empty<string>(), settingsFallback);

        if (string.IsNullOrWhiteSpace(localNodeId))
            return Array.Empty<string>();

        // Pick the freshest self-entry (most capabilities wins) when the
        // gateway returns duplicates from re-pair events.
        var self = gatewayNodes
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.NodeId) &&
                        string.Equals(n.NodeId, localNodeId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Capabilities?.Count ?? 0)
            .FirstOrDefault();

        if (self == null || self.Capabilities == null || self.Capabilities.Count == 0)
            return Array.Empty<string>();

        return NormalizeAndOrder(self.Capabilities);
    }

    private static IReadOnlyList<string> NormalizeAndOrder(IEnumerable<string> source)
    {
        // De-dup case-insensitively, then order canonically with unknowns alphabetical at end.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var c in source)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (seen.Add(c)) deduped.Add(c);
        }

        return deduped
            .OrderBy(c => CanonicalIndex(c))
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Build the categories from user settings (used when we don't yet have
    /// a live registration). Mirrors <see cref="NodeCapabilityGating"/>'s
    /// gating rules and adds the always-on <c>app</c>/<c>device</c>
    /// categories so the count matches InstancesPage.
    /// </summary>
    private static IEnumerable<string> BuildFromSettings(SettingsManager? s)
    {
        // Always-on capabilities — registered unconditionally by NodeService.
        yield return "app";
        yield return "device";

        if (NodeCapabilityGating.ShouldRegisterSystemRun(s))    yield return "system";
        if (NodeCapabilityGating.ShouldRegisterBrowserProxy(s)) yield return "browser";
        if (NodeCapabilityGating.ShouldRegisterCamera(s))       yield return "camera";
        if (NodeCapabilityGating.ShouldRegisterCanvas(s))       yield return "canvas";
        if (NodeCapabilityGating.ShouldRegisterScreen(s))       yield return "screen";
        if (NodeCapabilityGating.ShouldRegisterLocation(s))     yield return "location";
        if (NodeCapabilityGating.ShouldRegisterTts(s))          yield return "tts";
        if (NodeCapabilityGating.ShouldRegisterStt(s))          yield return "stt";
    }

    private static int CanonicalIndex(string category)
    {
        for (int i = 0; i < CanonicalOrder.Length; i++)
        {
            if (string.Equals(CanonicalOrder[i], category, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return int.MaxValue;
    }

    /// <summary>Chip label shown in the UI (e.g. "System tools" for the <c>system</c> category).</summary>
    public static string GetChipLabel(string category) => category?.ToLowerInvariant() switch
    {
        "system"   => "System tools",
        "browser"  => "Browser",
        "camera"   => "Camera",
        "canvas"   => "Canvas",
        "screen"   => "Screen",
        "location" => "Location",
        "tts"      => "TTS",
        "stt"      => "STT",
        "app"      => "App",
        "device"   => "Device",
        _ => Capitalize(category ?? ""),
    };

    /// <summary>Slug used in the "Providing N capabilities: …" line.</summary>
    public static string GetSlug(string category) => category?.ToLowerInvariant() switch
    {
        "system" => "system-tools",
        _ => (category ?? "").ToLowerInvariant(),
    };

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}

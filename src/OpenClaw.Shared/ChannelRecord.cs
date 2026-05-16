using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Aggregate view of one channel — combines the gateway-provided status snapshot
/// with capability flags and metadata. The page renders <see cref="ChannelRecord"/>s,
/// not raw snapshots.
/// </summary>
public sealed class ChannelRecord
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string DetailLabel { get; init; } = "";
    public string? SystemImage { get; init; }

    /// <summary>Raw status JSON from <c>channels.status</c>. Use <see cref="ChannelsStatusParser"/> helpers to extract.</summary>
    public JsonElement RawStatus { get; init; }

    public IReadOnlyList<ChannelAccountSnapshot> Accounts { get; init; } = [];
    public string? DefaultAccountId { get; init; }

    /// <summary>True if this channel has any active configuration/state. Mirrors Mac's <c>channelEnabled</c>.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>Capability flags (per-channel — driven by id).</summary>
    public ChannelCapabilities Capabilities { get; init; }

    /// <summary>True if Windows cannot host this channel even when configured (e.g. iMessage).</summary>
    public bool IsUnavailableOnWindows { get; init; }

    /// <summary>Sort order (gateway-provided); lower wins.</summary>
    public int SortOrder { get; init; }

    /// <summary>When we last received a status update for this channel.</summary>
    public DateTime LastUpdatedAt { get; init; }

    /// <summary>Last probe completion (epoch ms → DateTime), when reported.</summary>
    public DateTime? LastProbeAt { get; init; }
}

/// <summary>Per-channel capability flags. Inferred from id; the page uses these to gate action buttons.</summary>
[Flags]
public enum ChannelCapabilities
{
    None = 0,
    CanRefresh = 1 << 0,
    CanLogout = 1 << 1,
    CanShowQr = 1 << 2,
    CanRelink = 1 << 3,
}

/// <summary>
/// Merges <see cref="ChannelsStatusSnapshot"/> + a built-in capability/availability catalog
/// into a stable list of <see cref="ChannelRecord"/>s suitable for binding to a list view.
/// </summary>
public static class ChannelsAggregator
{
    /// <summary>Built-in fallback ordering when the gateway returns an empty <c>channelOrder</c>.</summary>
    public static readonly IReadOnlyList<string> BuiltInChannelOrder =
        new[] { "whatsapp", "telegram", "discord", "googlechat", "slack", "signal", "imessage", "nostr" };

    /// <summary>Channels that require a phone-app QR scan.</summary>
    private static readonly HashSet<string> QrLinkChannels =
        new(StringComparer.OrdinalIgnoreCase) { "whatsapp", "signal" };

    /// <summary>Channels that support a per-channel logout/unlink action.</summary>
    private static readonly HashSet<string> LogoutChannels =
        new(StringComparer.OrdinalIgnoreCase) { "whatsapp", "telegram" };

    /// <summary>Channels that cannot be hosted on Windows.</summary>
    private static readonly HashSet<string> WindowsUnsupportedChannels =
        new(StringComparer.OrdinalIgnoreCase) { "imessage" };

    /// <summary>
    /// Aggregate a snapshot into <see cref="ChannelRecord"/>s.
    /// Returns records ordered Configured-first, then by gateway/fallback sort order.
    /// </summary>
    public static IReadOnlyList<ChannelRecord> Aggregate(ChannelsStatusSnapshot? snapshot, DateTime now)
    {
        snapshot ??= new ChannelsStatusSnapshot();

        // Build the channel id list as the *union* of every source the gateway
        // might use to expose channels. Older gateways and plugin-only setups
        // sometimes omit channelOrder while still populating channels/meta —
        // iterating channelOrder alone would silently drop them.
        var order = BuildOrderedIds(snapshot);
        var records = new List<ChannelRecord>(order.Count);

        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            snapshot.Channels.TryGetValue(id, out var raw);
            var accounts = snapshot.ChannelAccounts.TryGetValue(id, out var accs) ? accs : [];
            snapshot.ChannelDefaultAccountId.TryGetValue(id, out var defaultAccountId);

            var configured = IsChannelConfigured(raw, accounts);
            var caps = ChannelCapabilities.CanRefresh;
            if (LogoutChannels.Contains(id)) caps |= ChannelCapabilities.CanLogout;
            if (QrLinkChannels.Contains(id)) caps |= ChannelCapabilities.CanShowQr | ChannelCapabilities.CanRelink;

            records.Add(new ChannelRecord
            {
                Id = id,
                Label = snapshot.ResolveLabel(id),
                DetailLabel = snapshot.ResolveDetailLabel(id),
                SystemImage = snapshot.ResolveSystemImage(id),
                RawStatus = raw,
                Accounts = accounts,
                DefaultAccountId = defaultAccountId,
                IsConfigured = configured,
                Capabilities = caps,
                IsUnavailableOnWindows = WindowsUnsupportedChannels.Contains(id),
                SortOrder = i,
                LastUpdatedAt = now,
                LastProbeAt = ExtractLastProbeAt(raw),
            });
        }

        return records
            .OrderByDescending(r => r.IsConfigured)
            .ThenBy(r => r.SortOrder)
            .ToList();
    }

    /// <summary>
    /// Build the ordered channel id list by unioning every source in the snapshot:
    /// <c>channelOrder</c> (canonical, if provided) → channel ids in <c>channels</c>
    /// (covers older gateways missing channelOrder) → ids in <c>channelMeta</c> /
    /// <c>channelAccounts</c> (covers metadata-only entries). Falls back to the
    /// built-in ordering when the snapshot is empty.
    /// </summary>
    internal static IReadOnlyList<string> BuildOrderedIds(ChannelsStatusSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Append(IEnumerable<string> source)
        {
            foreach (var id in source)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (seen.Add(id)) order.Add(id);
            }
        }

        Append(snapshot.ChannelOrder);
        if (snapshot.ChannelMeta is { } meta) Append(meta.Select(m => m.Id));
        Append(snapshot.Channels.Keys);
        Append(snapshot.ChannelAccounts.Keys);

        // Last resort: built-in order when the gateway returned literally nothing.
        if (order.Count == 0) Append(BuiltInChannelOrder);
        return order;
    }

    /// <summary>Mac's <c>channelEnabled</c> rule: configured || running || connected || any-account-active.</summary>
    public static bool IsChannelConfigured(JsonElement raw, IReadOnlyList<ChannelAccountSnapshot>? accounts)
    {
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (TryGetBool(raw, "configured")) return true;
            if (TryGetBool(raw, "running")) return true;
            if (TryGetBool(raw, "connected")) return true;
        }
        if (accounts != null)
        {
            foreach (var acc in accounts)
                if (acc.Configured == true || acc.Running == true || acc.Connected == true)
                    return true;
        }
        return false;
    }

    private static bool TryGetBool(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime? ExtractLastProbeAt(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("lastProbeAt", out var ms) || ms.ValueKind != JsonValueKind.Number) return null;
        if (!ms.TryGetDouble(out var d)) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)d).UtcDateTime;
    }
}

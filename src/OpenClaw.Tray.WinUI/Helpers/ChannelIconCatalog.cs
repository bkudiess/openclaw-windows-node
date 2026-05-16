using System.Collections.Generic;

namespace OpenClawTray.Helpers;

/// <summary>
/// Maps channel ids to Fluent / Segoe icons. Segoe Fluent Icons doesn't carry
/// third-party brand glyphs (no WhatsApp/Discord logos), so this catalog uses
/// semantic Fluent glyphs to distinguish channel families: phone-link, chat,
/// webhook, decentralized.
/// </summary>
public static class ChannelIconCatalog
{
    /// <summary>Fallback glyph (CellularData / Tower).</summary>
    public const string Default = "\uEC05";

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        // Phone-link channels (QR scan with mobile app)
        ["whatsapp"] = "\uE717",   // Phone
        ["signal"]   = "\uE717",   // Phone
        ["imessage"] = "\uE8BD",   // Message

        // Chat/bot channels
        ["telegram"] = "\uE8BD",   // Message
        ["discord"]  = "\uE8BD",   // Message
        ["slack"]    = "\uE8F2",   // Comment

        // Workspace/webhook channels
        ["googlechat"] = "\uE715", // Mail

        // Decentralized / plugin channels
        ["nostr"]    = "\uE774",   // Globe
    };

    /// <summary>Resolve a glyph for a channel id. Returns the default Channels glyph for unknown ids.</summary>
    public static string ResolveGlyph(string channelId) =>
        !string.IsNullOrEmpty(channelId) && Glyphs.TryGetValue(channelId.ToLowerInvariant(), out var glyph)
            ? glyph
            : Default;
}

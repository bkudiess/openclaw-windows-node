using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Helpers;

/// <summary>
/// Central catalog of Segoe Fluent Icons (PUA) glyphs used by the tray UI.
/// Each entry is a single-character string in the Private Use Area
/// (U+E000-U+F8FF) so call sites avoid magic literals and tests can verify
/// the catalog is well-formed.
///
/// Codepoints are taken from the published Segoe Fluent Icons list. Where
/// a semantic match was ambiguous the closest available glyph is used and
/// noted in a comment.
/// </summary>
public static class FluentIconCatalog
{
    // ── Status / state ─────────────────────────────────────────────
    public const string StatusOk = "\uE73E";       // CheckMark
    public const string StatusWarn = "\uE7BA";     // Warning
    public const string StatusErr = "\uEA39";      // ErrorBadge

    // ── Sections / categories ──────────────────────────────────────
    public const string Sessions = "\uE8BD";       // Message
    public const string Approvals = "\uE7BA";      // Warning (re-use)
    public const string Devices = "\uE772";        // Devices
    public const string Permissions = "\uE72E";    // Permissions

    // ── Capabilities (per-permission glyphs) ───────────────────────
    public const string Browser = "\uE774";        // Globe
    public const string Camera = "\uE722";         // Camera
    public const string Canvas = "\uE70F";         // Edit
    public const string Screen = "\uE7F4";         // TVMonitor
    public const string Location = "\uE707";       // MapPin (Globe2 alt)
    public const string Voice = "\uE767";          // Volume (speaker, for TTS)
    public const string Speech = "\uF12E";         // Dictate (speech-to-text)
    public const string System = "\uE713";         // Settings (Enable node toggle)

    // ── Actions ────────────────────────────────────────────────────
    public const string Dashboard = "\uE774";      // Globe
    public const string Chat = "\uE8BD";           // Message
    public const string CanvasAct = "\uE70F";      // Edit
    public const string VoiceAct = "\uE720";       // Microphone
    public const string Settings = "\uE713";       // Settings
    public const string QuickSend = "\uE724";      // Send (Mail variant) — closest universal Send glyph
    public const string Setup = "\uE825";          // MapPin (compass glyph U+E1D3 isn't reliably in Segoe Fluent; MapPin is safer)
    public const string About = "\uE946";          // Info
    public const string Exit = "\uE711";           // Cancel (X) — used for "Close" menu item

    // ── Affordances ────────────────────────────────────────────────
    public const string ChevronR = "\uE76C";       // ChevronRight
    public const string Check = "\uE73E";          // CheckMark

    // ── Brand placeholder (lobster emoji currently retained) ───────
    public const string Brand = "🦞";

    /// <summary>
    /// Builds a <see cref="FontIcon"/> for the given PUA glyph using the
    /// system-resolved <c>SymbolThemeFontFamily</c> so the icon honors
    /// the user's selected icon font (Segoe Fluent Icons on Win11, Segoe
    /// MDL2 Assets fallback on Win10).
    /// </summary>
    public static FontIcon Build(string glyph, double size = 16)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            FontSize = size,
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> is a single character in the
    /// Unicode Private Use Area (U+E000-U+F8FF) — i.e. a Segoe Fluent
    /// Icons glyph rather than an emoji.
    /// </summary>
    public static bool IsPuaGlyph(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 1)
            return false;
        var c = value[0];
        return c >= '\uE000' && c <= '\uF8FF';
    }
}

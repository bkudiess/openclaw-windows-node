using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Chat.Explorations;

/// <summary>
/// Pure functions that translate the live <see cref="ChatExplorationState"/>
/// values into concrete WinUI primitives (Thickness, double, Brush). Components
/// in <see cref="OpenClawTray.Chat"/> call these from their <c>Render</c> so the
/// translation logic lives in one place and stays consistent across the chat
/// surface.
///
/// Centralizing here also lets the <see cref="ChatVariation"/> presets fan-out
/// to multiple sliders in one place — pick a variation, get the bundled bubble
/// radius / gutter / message gap / padding density / composer radius, etc.
/// (The variation only fires when the user hits a preset button; once they
/// nudge a slider, the slider value wins because it's stored separately.)
/// </summary>
public static class ChatVisualResolver
{
    // ── Bubble / Layout (C) ───────────────────────────────────────────

    public static CornerRadius BubbleCornerRadius()
        => new(ChatExplorationState.BubbleCornerRadius);

    public static Thickness BubbleListPadding()
    {
        // Gutter is symmetric horizontal padding around the timeline column.
        var g = ChatExplorationState.Gutter;
        return new Thickness(g, 0, g, 0);
    }

    public static double MessageGap() => ChatExplorationState.MessageGap;

    public static Thickness BubbleInnerPadding() => ChatExplorationState.PaddingDensity switch
    {
        ChatPaddingDensity.Cozy        => new Thickness(16, 12, 16, 12),
        ChatPaddingDensity.Compact     => new Thickness(10, 6, 10, 6),
        _ /* Comfortable */            => new Thickness(14, 8, 14, 8),
    };

    public static bool ShowTimestamps() => ChatExplorationState.ShowTimestamps;

    // ── Avatar (D) ────────────────────────────────────────────────────

    public static bool ShowUserAvatar() => ChatExplorationState.ShowAvatars
        && ChatExplorationState.AvatarMode == ChatAvatarMode.Both;

    public static bool ShowAssistantAvatar() => ChatExplorationState.ShowAvatars
        && ChatExplorationState.AvatarMode != ChatAvatarMode.None;

    // ── Composer (E) ──────────────────────────────────────────────────

    public static CornerRadius ComposerCornerRadius()
        => new(ChatExplorationState.ComposerCornerRadius);

    public static double ComposerIconSize() => ChatExplorationState.ComposerIconSize;

    public static double SendButtonSize() => ChatExplorationState.SendButtonSize;

    // ── Brush overrides (F). Resolve to override-or-fallback. ─────────

    /// <summary>Returns the override brush if set, else <paramref name="fallback"/>.</summary>
    public static Brush AccentBrush(Brush fallback)
        => ChatExplorationState.AccentBrushOverride ?? fallback;

    public static Brush UserBubbleBrush(Brush fallback)
        => ChatExplorationState.UserBubbleBrushOverride ?? fallback;

    public static Brush AssistantBubbleBrush(Brush fallback)
        => ChatExplorationState.AssistantBubbleBrushOverride ?? fallback;

    public static Brush SendButtonBrush(Brush fallback)
        => ChatExplorationState.SendButtonBrushOverride ?? fallback;

    // ── v2 additions ─────────────────────────────────────────────────

    public static bool ShowAssistantBubbles() => ChatExplorationState.ShowAssistantBubbles;
    public static bool ShowToolCalls()        => ChatExplorationState.ShowToolCalls;
    public static double BubbleMaxWidth()     => ChatExplorationState.BubbleMaxWidth;
    public static double BubbleSideMargin()   => ChatExplorationState.BubbleSideMargin;

    /// <summary>
    /// Build a chat footer text from the live toggles. <paramref name="time"/>,
    /// <paramref name="model"/>, <paramref name="tokens"/>, <paramref name="contextPct"/>
    /// are shown only when their corresponding toggle is on. <paramref name="sender"/>
    /// is shown when <see cref="ChatExplorationState.ShowSenderName"/> is on.
    /// Returns an empty string when nothing should be shown — callers can
    /// short-circuit rendering the whole footer in that case.
    /// </summary>
    public static string BuildFooterText(string? sender, string? time, string? model, string? tokens, string? contextPct)
    {
        var parts = new System.Collections.Generic.List<string>(5);
        if (ChatExplorationState.ShowSenderName     && !string.IsNullOrEmpty(sender))     parts.Add(sender!);
        if (ChatExplorationState.ShowTimestamps     && !string.IsNullOrEmpty(time))       parts.Add(time!);
        if (ChatExplorationState.ShowModelName      && !string.IsNullOrEmpty(model))      parts.Add(model!);
        if (ChatExplorationState.ShowTokens         && !string.IsNullOrEmpty(tokens))     parts.Add(tokens!);
        if (ChatExplorationState.ShowContextPercent && !string.IsNullOrEmpty(contextPct)) parts.Add(contextPct!);
        return string.Join(" · ", parts);
    }

    // ── Theme (A) ────────────────────────────────────────────────────

    public static ElementTheme ResolvePreviewTheme() => ChatExplorationState.PreviewTheme switch
    {
        ChatPreviewTheme.Light => ElementTheme.Light,
        ChatPreviewTheme.Dark  => ElementTheme.Dark,
        _                      => ElementTheme.Default,
    };
}

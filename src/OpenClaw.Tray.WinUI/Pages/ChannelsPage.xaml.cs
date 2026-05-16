using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace OpenClawTray.Pages;

/// <summary>
/// Channels page — single-column Expander layout. See <c>ChannelsPage.xaml</c>
/// for the architectural comment. Data flows from the gateway's
/// <c>channels.status</c> response → <see cref="ChannelsStatusSnapshot"/> →
/// <see cref="ChannelRecord"/>s → Expander cards built imperatively below.
///
/// Follows the single-app-model pattern (see <c>docs/DATA_FLOW_ARCHITECTURE.md</c>):
/// the page observes <see cref="AppState"/> directly via <see cref="INotifyPropertyChanged"/>
/// and routes commands through <see cref="App"/> globally; no <c>HubWindow</c> coupling.
/// </summary>
public sealed partial class ChannelsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    /// <summary>
    /// When we last received a snapshot from the gateway. The snapshot itself
    /// lives on <see cref="AppState.ChannelsSnapshot"/> so other surfaces can
    /// observe it — only the receive-timestamp is page-local because
    /// <see cref="ChannelsAggregator"/> needs it to stamp records.
    /// </summary>
    private DateTime _latestSnapshotAt;
    private CancellationTokenSource? _refreshCts;

    /// <summary>Tracks the channel id for each Expander so per-channel pushes can target the right card.</summary>
    private readonly Dictionary<string, Expander> _expanderById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Preserves per-channel <c>IsExpanded</c> across refreshes (don't collapse on every push).</summary>
    private readonly HashSet<string> _expandedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gates concurrent refreshes so a burst of channel-health pushes plus a user
    /// click don't trigger overlapping <c>channels.status</c> requests. The CTS
    /// only suppresses stale UI updates — the semaphore prevents duplicate calls.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Cancellation token for in-flight linking flows (QR scan polls). Distinct
    /// from <see cref="_refreshCts"/> so the user can Refresh without aborting
    /// an active linking session.
    /// </summary>
    private CancellationTokenSource? _linkingCts;

    public ChannelsPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cancel + dispose all per-page tokens. Re-enabling the Refresh button
        // here covers the back-to-back-cancel race where neither call reaches
        // its finally block in the !cts.IsCancellationRequested branch.
        try { _refreshCts?.Cancel(); _refreshCts?.Dispose(); } catch { }
        _refreshCts = null;
        try { _linkingCts?.Cancel(); _linkingCts?.Dispose(); } catch { }
        _linkingCts = null;
        SetRefreshBusy(false);

        if (_appState != null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
    }

    /// <summary>
    /// Hook from <c>HubWindow.InitializeCurrentPage</c> — subscribes to
    /// <see cref="AppState"/> and triggers an initial <c>channels.status</c> fetch.
    /// </summary>
    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        if (_appState != null)
            _appState.PropertyChanged += OnAppStateChanged;

        NotConnectedBar.IsOpen = CurrentApp.GatewayClient == null;

        // Render whatever AppState already holds (lets the user re-enter the
        // page without a gateway round-trip) and then kick off a fresh fetch
        // so the snapshot stays current.
        var cached = _appState?.ChannelsSnapshot;
        if (cached != null)
            Render(cached);

        _ = RefreshAsync();
    }

    /// <summary>
    /// React to <see cref="AppState"/> updates. Two properties matter:
    /// <list type="bullet">
    /// <item><see cref="AppState.ChannelsSnapshot"/> — the rich snapshot
    /// (Updated by us after a <c>channels.status</c> fetch; other surfaces
    /// may write to it too). Re-render directly.</item>
    /// <item><see cref="AppState.Channels"/> — slim per-event health array
    /// pushed by the gateway. Signals something changed; refresh the rich
    /// snapshot to keep metadata current.</item>
    /// </list>
    /// </summary>
    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.ChannelsSnapshot):
                if (_appState?.ChannelsSnapshot is { } snap)
                    Render(snap);
                break;
            case nameof(AppState.Channels):
                _ = RefreshAsync(probe: false);
                break;
        }
    }

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool probe = true)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            NotConnectedBar.IsOpen = true;
            return;
        }
        NotConnectedBar.IsOpen = false;

        // Replace any superseded CTS, disposing the old one so we don't leak
        // its internal handle.
        var oldCts = _refreshCts;
        _refreshCts = new CancellationTokenSource();
        var cts = _refreshCts;
        if (oldCts != null)
        {
            try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
        }

        // Coalesce concurrent calls (user clicks + push deltas) — only one
        // gateway request in flight at a time. If we can't acquire immediately,
        // skip: the in-flight call will reflect the latest state shortly.
        if (!await _refreshGate.WaitAsync(0))
            return;

        SetRefreshBusy(true);
        try
        {
            var snapshot = await client.GetChannelsStatusAsync(probe);
            if (cts.IsCancellationRequested) return;
            if (snapshot == null)
            {
                ErrorBar.Title = "Couldn't refresh channels";
                ErrorBar.Message = "The gateway didn't return a channels.status response. Try Refresh again.";
                ErrorBar.IsOpen = true;
                return;
            }
            ErrorBar.IsOpen = false;
            _latestSnapshotAt = DateTime.UtcNow;
            // Publish into AppState — single source of truth. Setting the
            // property fires PropertyChanged which calls Render via
            // OnAppStateChanged; no need to call Render directly here.
            if (_appState != null)
                _appState.ChannelsSnapshot = snapshot;
            else
                Render(snapshot); // tests / scenarios with no AppState
        }
        finally
        {
            // Always re-enable the button — OnUnloaded also calls SetRefreshBusy(false)
            // as a belt-and-braces for the cancel-during-cancel race.
            SetRefreshBusy(false);
            _refreshGate.Release();
        }
    }

    private void SetRefreshBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
    }

    private void Render(ChannelsStatusSnapshot snapshot)
    {
        // When connected to a gateway, show ONLY what the gateway reports — no
        // built-in fallback list. Fake-listing channels the gateway doesn't
        // expose causes clicks-into-nothing on Show QR. When disconnected,
        // fall back to the built-in list so the page isn't empty.
        var useFallback = CurrentApp.GatewayClient == null;
        var records = ChannelsAggregator.Aggregate(
            snapshot,
            _latestSnapshotAt == default ? DateTime.UtcNow : _latestSnapshotAt,
            useBuiltInFallback: useFallback);
        var configured = records.Where(r => r.IsConfigured).ToList();
        var available = records.Where(r => !r.IsConfigured).ToList();

        _expanderById.Clear();
        ConfiguredList.Children.Clear();
        AvailableList.Children.Clear();

        foreach (var rec in configured)
            ConfiguredList.Children.Add(BuildExpander(rec));
        foreach (var rec in available)
            AvailableList.Children.Add(BuildExpander(rec));

        ConfiguredSection.Visibility = configured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableSection.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        MetaText.Text = records.Count == 0
            ? (useFallback
                ? "connect to a gateway to see what channels are available"
                : "this gateway didn't report any channels")
            : $"{records.Count} channels · {configured.Count} configured · last check just now";
    }

    // ─── Expander construction ─────────────────────────────────────────────

    private Expander BuildExpander(ChannelRecord record)
    {
        var (dotBrushKey, badgeText, badgeSeverity, subtitle) = ResolveHeaderState(record);

        var header = BuildHeader(record, dotBrushKey, badgeText, badgeSeverity, subtitle);
        var body = BuildBody(record);

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expandedIds.Contains(record.Id),
            Header = header,
            Content = body,
        };
        expander.Expanding += (_, _) => _expandedIds.Add(record.Id);
        expander.Collapsed += (_, _) => _expandedIds.Remove(record.Id);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, record.Label);
        _expanderById[record.Id] = expander;
        return expander;
    }

    private FrameworkElement BuildHeader(
        ChannelRecord record,
        string dotBrushKey,
        string badgeText,
        BadgeSeverity badgeSeverity,
        string subtitle)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Channel icon — built via FluentIconCatalog.Build so the icon honors
        // SymbolThemeFontFamily (Segoe Fluent Icons on Win11, MDL2 fallback on Win10).
        // Size 22 px matches the Permissions row icon size in tokens.md.
        var icon = FluentIconCatalog.Build(ChannelIconCatalog.ResolveGlyph(record.Id), size: 22);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // Middle stack: top row (dot + name + badge) + subtitle
        var middle = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Ellipse
        {
            // 12 px diameter on cards per tokens.md.
            Width = 12, Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current.Resources[dotBrushKey] is Brush brush) dot.Fill = brush;
        topRow.Children.Add(dot);
        topRow.Children.Add(new TextBlock
        {
            Text = record.Label,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        topRow.Children.Add(BuildBadge(badgeText, badgeSeverity));
        middle.Children.Add(topRow);

        if (!string.IsNullOrEmpty(subtitle))
        {
            middle.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        // Header actions (Refresh, Logout)
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanRefresh))
            actions.Children.Add(BuildHeaderActionButton(FluentIconCatalog.ChannelRefresh, "Refresh", null, ignored => { var _ignored = RefreshAsync(); }));
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanLogout))
            actions.Children.Add(BuildHeaderActionButton(FluentIconCatalog.ChannelLogout, "Logout", record.Id, channelId => { var _ignored = LogoutAsync(channelId!); }));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return grid;
    }

    private Border BuildBadge(string text, BadgeSeverity severity)
    {
        var (bgKey, fgKey) = severity switch
        {
            BadgeSeverity.Success  => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            BadgeSeverity.Caution  => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            BadgeSeverity.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _                      => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        var bg = Application.Current.Resources.TryGetValue(bgKey, out var bgObj) && bgObj is Brush b
            ? b
            : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        var fg = Application.Current.Resources.TryGetValue(fgKey, out var fgObj) && fgObj is Brush f
            ? f
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = fg,
            },
        };
    }

    private Button BuildHeaderActionButton(string glyph, string label, string? tag, Action<string?> handler)
    {
        var btn = new Button
        {
            Padding = new Thickness(8, 2, 8, 2),
            Tag = tag,
        };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        // Use FluentIconCatalog.Build so the icon honors SymbolThemeFontFamily.
        var icon = FluentIconCatalog.Build(glyph, size: 12);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        btn.Content = stack;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, label);
        btn.Click += (s, _) =>
        {
            handler(((Button)s).Tag as string);
        };
        return btn;
    }

    // ─── Body construction ─────────────────────────────────────────────────

    private FrameworkElement BuildBody(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };

        if (record.IsUnavailableOnWindows)
        {
            stack.Children.Add(BuildInfoText(
                "This channel requires a macOS host. It can't be configured from a Windows machine."));
            return stack;
        }

        // Getting started — only for unconfigured channels. The user doesn't
        // need to read setup instructions for something that's already running.
        if (!record.IsConfigured)
        {
            var guide = BuildSetupGuide(record);
            if (guide != null) stack.Children.Add(guide);
        }

        // Status section
        stack.Children.Add(BuildSection("Status", BuildStatusKv(record)));

        // Linking section (WhatsApp/Signal) — body is built lazily when the user opens the section.
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanShowQr))
            stack.Children.Add(BuildLinkingPlaceholder(record));

        // Configuration section — inline credential form for channels we have
        // explicit field definitions for (Telegram bot token, Discord webhook,
        // Slack tokens, Google Chat webhook, Nostr key/relays). For unknown
        // plugin channels we fall back to the "Open Config page" stub.
        var inlineForm = BuildInlineConfigForm(record);
        stack.Children.Add(BuildSection("Configuration", inlineForm));

        return stack;
    }

    private static FrameworkElement BuildSection(string title, FrameworkElement content)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>
    /// Per-channel "Getting started" card. Shown only for unconfigured channels
    /// so the user has a concrete, channel-specific path forward instead of a
    /// generic "Open Config page" stub. Each guide is a short numbered list
    /// describing exactly what to do for that channel, plus an external help
    /// link when there's a canonical third-party page to visit.
    /// </summary>
    private FrameworkElement? BuildSetupGuide(ChannelRecord record)
    {
        var (headline, steps) = ResolveSetupGuide(record.Id);
        if (headline == null) return null;

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 14),
        };

        var stack = new StackPanel { Spacing = 6 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icon = FluentIconCatalog.Build("\uE946", 16); // Info glyph
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        headerRow.Children.Add(icon);
        headerRow.Children.Add(new TextBlock
        {
            Text = headline,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(headerRow);

        for (int i = 0; i < steps!.Length; i++)
        {
            var stepRow = new Grid();
            stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = new TextBlock
            {
                Text = (i + 1).ToString() + ".",
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(num, 0);
            stepRow.Children.Add(num);

            var body = new TextBlock
            {
                Text = steps[i],
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(body, 1);
            stepRow.Children.Add(body);

            stack.Children.Add(stepRow);
        }

        // External help link, if we have one for this channel. Clicking the
        // HyperlinkButton opens the user's default browser via the standard
        // WinUI mechanism (we don't need a code-behind handler).
        var (linkText, linkUrl) = ResolveExternalHelpLink(record.Id);
        if (!string.IsNullOrEmpty(linkUrl))
        {
            var helpLink = new HyperlinkButton
            {
                Content = linkText,
                NavigateUri = new Uri(linkUrl),
                Padding = new Thickness(0, 4, 0, 0),
            };
            stack.Children.Add(helpLink);
        }

        card.Child = stack;
        return card;
    }

    /// <summary>
    /// External help URL for the third-party service a channel needs credentials
    /// from. Where there's no canonical page (e.g. WhatsApp/Signal use a phone
    /// app, not a website), returns (null, null).
    /// </summary>
    private static (string? Text, string? Url) ResolveExternalHelpLink(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "telegram"   => ("How to create a Telegram bot →",     "https://core.telegram.org/bots/features#botfather"),
            "discord"    => ("How to create a Discord webhook →",  "https://support.discord.com/hc/en-us/articles/228383668"),
            "googlechat" => ("How to add a Google Chat webhook →", "https://developers.google.com/chat/how-tos/webhooks"),
            "slack"      => ("Slack app dashboard →",              "https://api.slack.com/apps"),
            "nostr"      => ("About Nostr →",                      "https://nostr.com/"),
            _ => (null, null),
        };

    /// <summary>
    /// Channel-specific setup content. Returns (null, null) for channels we
    /// don't have explicit guidance for — the generic Configuration section
    /// still renders, so plugin channels aren't left without any signposting.
    /// </summary>
    private static (string? Headline, string[]? Steps) ResolveSetupGuide(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "whatsapp" => ("Link your WhatsApp phone", new[]
            {
                "Click \"Show QR\" in the Linking section below.",
                "On your phone: WhatsApp → Settings → Linked devices → Link a device.",
                "Scan the QR code that appears here.",
            }),
            "signal" => ("Link your Signal phone", new[]
            {
                "Click \"Show QR\" in the Linking section below.",
                "On your phone: Signal → Settings → Linked devices → Link new device.",
                "Scan the QR code that appears here.",
            }),
            "telegram" => ("Connect Telegram via a bot", new[]
            {
                "Open Telegram and send a message to @BotFather.",
                "Send /newbot and follow the prompts. Copy the bot token at the end.",
                "Paste the token into the Configuration form below.",
                "Click \"Save and start\". The channel will start automatically.",
            }),
            "discord" => ("Connect Discord via a webhook", new[]
            {
                "Open your Discord server settings → Integrations → Webhooks.",
                "Click \"New Webhook\", give it a name, and copy the webhook URL.",
                "Paste the URL into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "googlechat" => ("Connect Google Chat via a webhook", new[]
            {
                "In Google Chat, open the space → Manage webhooks → Add webhook.",
                "Copy the webhook URL.",
                "Paste the URL into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "slack" => ("Connect Slack via an app", new[]
            {
                "Create a Slack app at api.slack.com/apps and install it to your workspace.",
                "Copy the bot token (xoxb-…) and the signing secret.",
                "Paste both into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "nostr" => ("Connect Nostr via relays", new[]
            {
                "Generate or paste a private key (nsec).",
                "Pick one or more relay URLs (e.g. wss://relay.damus.io).",
                "Paste both into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            _ => (null, null),
        };

    // ─── Inline credential form (no Config page detour) ─────────────────────

    /// <summary>One field rendered in the inline config form.</summary>
    private sealed record ConfigField(
        string Path,
        string Label,
        string Placeholder,
        bool Sensitive,
        bool Required,
        bool Multiline = false,
        string? HelpText = null);

    /// <summary>
    /// Per-channel inline-form schema. Fields were validated against the
    /// gateway test fixtures (src/cli/config-cli.test.ts and related tests
    /// confirm channels.telegram.botToken, channels.slack.botToken/signingSecret,
    /// channels.discord.webhookUrl, etc.). Returns null for channels without an
    /// inline form — those still get the "Open Config page" stub.
    /// </summary>
    private static IReadOnlyList<ConfigField>? ResolveConfigFields(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "telegram" => new[]
            {
                new ConfigField(
                    "channels.telegram.botToken",
                    "Bot token",
                    "123456:ABCdef...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Get from @BotFather (/newbot)."),
            },
            "discord" => new[]
            {
                new ConfigField(
                    "channels.discord.webhookUrl",
                    "Webhook URL",
                    "https://discord.com/api/webhooks/...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Server Settings → Integrations → Webhooks → New Webhook."),
            },
            "googlechat" => new[]
            {
                new ConfigField(
                    "channels.googlechat.webhookUrl",
                    "Webhook URL",
                    "https://chat.googleapis.com/v1/spaces/...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Open a space → Manage webhooks → Add webhook."),
            },
            "slack" => new[]
            {
                new ConfigField(
                    "channels.slack.botToken",
                    "Bot token",
                    "xoxb-...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "OAuth tokens from your Slack app."),
                new ConfigField(
                    "channels.slack.signingSecret",
                    "Signing secret",
                    "",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Basic Information → App Credentials."),
            },
            "nostr" => new[]
            {
                new ConfigField(
                    "channels.nostr.nsec",
                    "Private key (nsec)",
                    "nsec1...",
                    Sensitive: true,
                    Required: true),
                new ConfigField(
                    "channels.nostr.relays",
                    "Relay URLs",
                    "wss://relay.damus.io",
                    Sensitive: false,
                    Required: true,
                    Multiline: true,
                    HelpText: "One per line."),
            },
            _ => null,
        };

    /// <summary>
    /// Build the Configuration body for a channel. For channels in
    /// <see cref="ResolveConfigFields"/> we render an inline form that writes
    /// directly to the gateway via <c>config.set</c> — no Config page detour.
    /// For unknown channels we fall back to the generic "Open Config page" stub.
    /// </summary>
    private FrameworkElement BuildInlineConfigForm(ChannelRecord record)
    {
        var fields = ResolveConfigFields(record.Id);
        if (fields == null) return BuildConfigPlaceholder(record);

        var stack = new StackPanel { Spacing = 10 };

        // Status banner (set on Save success/failure, or by Test action).
        var statusBar = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Informational,
        };
        stack.Children.Add(statusBar);

        // Field inputs — track the FrameworkElement (TextBox / PasswordBox) so
        // Save can read the value and validate "required".
        var inputs = new Dictionary<string, FrameworkElement>();

        foreach (var field in fields)
        {
            var row = new StackPanel { Spacing = 4 };

            // Label with optional "required" marker.
            var label = new TextBlock
            {
                Text = field.Required ? $"{field.Label} *" : field.Label,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            row.Children.Add(label);

            // Input: PasswordBox for sensitive, multi-line TextBox for relay lists,
            // single-line TextBox otherwise.
            FrameworkElement input;
            if (field.Sensitive)
            {
                input = new PasswordBox
                {
                    PlaceholderText = field.Placeholder,
                    PasswordRevealMode = PasswordRevealMode.Peek,
                };
            }
            else if (field.Multiline)
            {
                input = new TextBox
                {
                    PlaceholderText = field.Placeholder,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 70,
                };
            }
            else
            {
                input = new TextBox { PlaceholderText = field.Placeholder };
            }
            row.Children.Add(input);
            inputs[field.Path] = input;

            // Help text.
            if (!string.IsNullOrEmpty(field.HelpText))
            {
                row.Children.Add(new TextBlock
                {
                    Text = field.HelpText,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            stack.Children.Add(row);
        }

        // Save row.
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var saveBtn = new Button
        {
            Content = record.IsConfigured ? "Save changes" : "Save and start",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var openConfigBtn = new Button
        {
            Content = "Open Config page",
        };
        openConfigBtn.Click += (_, _) => ((IAppCommands)CurrentApp).Navigate("config");
        actionRow.Children.Add(saveBtn);
        actionRow.Children.Add(openConfigBtn);
        stack.Children.Add(actionRow);

        async Task SaveAsync()
        {
            var client = CurrentApp.GatewayClient;
            if (client == null)
            {
                statusBar.Severity = InfoBarSeverity.Error;
                statusBar.Title = "Not connected";
                statusBar.Message = "Connect to a gateway before saving channel config.";
                statusBar.IsOpen = true;
                return;
            }

            // Validate required + collect values from the inputs.
            var values = new List<(string Path, string Value)>();
            foreach (var field in fields)
            {
                var raw = inputs[field.Path] switch
                {
                    PasswordBox pb => pb.Password,
                    TextBox tb => tb.Text,
                    _ => string.Empty,
                };
                if (field.Required && string.IsNullOrWhiteSpace(raw))
                {
                    statusBar.Severity = InfoBarSeverity.Error;
                    statusBar.Title = "Missing field";
                    statusBar.Message = $"{field.Label} is required.";
                    statusBar.IsOpen = true;
                    return;
                }
                if (string.IsNullOrWhiteSpace(raw)) continue; // skip empty optional fields
                values.Add((field.Path, raw.Trim()));
            }

            if (values.Count == 0) return;

            saveBtn.IsEnabled = false;
            try
            {
                statusBar.Severity = InfoBarSeverity.Informational;
                statusBar.Title = "Saving…";
                statusBar.Message = $"Writing {values.Count} field(s) to {record.Id} config.";
                statusBar.IsOpen = true;

                // For multi-line relay-style fields we send the array form;
                // otherwise send the string as-is. The gateway schema accepts
                // both for these specific paths in our catalog.
                var failures = new List<string>();
                foreach (var (path, value) in values)
                {
                    object payload = value;
                    if (fields.FirstOrDefault(f => f.Path == path) is { Multiline: true })
                    {
                        // Treat each non-empty line as a list entry.
                        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => s.Length > 0)
                            .ToArray();
                        payload = lines;
                    }

                    var ok = await client.SetConfigAsync(path, payload);
                    if (!ok) failures.Add(path);
                }

                if (failures.Count > 0)
                {
                    statusBar.Severity = InfoBarSeverity.Error;
                    statusBar.Title = "Save failed";
                    statusBar.Message = $"The gateway rejected: {string.Join(", ", failures)}";
                }
                else
                {
                    statusBar.Severity = InfoBarSeverity.Success;
                    statusBar.Title = "Saved";
                    statusBar.Message = $"{record.Id} config updated. Refreshing channel state…";
                    // Re-fetch the snapshot so the page reflects the new
                    // "configured" state and any auto-start behavior.
                    await RefreshAsync(probe: true);
                }
            }
            finally
            {
                saveBtn.IsEnabled = true;
            }
        }
        saveBtn.Click += async (_, _) => await SaveAsync();

        return stack;
    }

    private static FrameworkElement BuildStatusKv(ChannelRecord record)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pairs = BuildStatusPairs(record);
        for (int i = 0; i < pairs.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < pairs.Count; i++)
        {
            var (key, value) = pairs[i];
            var keyBlock = new TextBlock
            {
                Text = key,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(keyBlock, i);
            Grid.SetColumn(keyBlock, 0);
            grid.Children.Add(keyBlock);

            var valBlock = new TextBlock
            {
                Text = value,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(valBlock, i);
            Grid.SetColumn(valBlock, 1);
            grid.Children.Add(valBlock);
        }

        return grid;
    }

    private static List<(string Key, string Value)> BuildStatusPairs(ChannelRecord record)
    {
        var raw = record.RawStatus;
        var pairs = new List<(string, string)>();

        if (raw.ValueKind != JsonValueKind.Object)
        {
            pairs.Add(("Status", record.IsConfigured ? "configured" : "not configured"));
            return pairs;
        }

        // Lifecycle status
        var configured = GetBool(raw, "configured");
        var running = GetBool(raw, "running");
        var connected = GetBool(raw, "connected");
        var linked = GetBool(raw, "linked");
        var lastError = GetString(raw, "lastError") ?? GetString(raw, "error");

        // Lowercase short forms per naming.md status vocabulary.
        if (!string.IsNullOrEmpty(lastError))
            pairs.Add(("Status", "error"));
        else if (connected == true)
            pairs.Add(("Status", "connected"));
        else if (running == true)
            pairs.Add(("Status", "running"));
        else if (configured == true)
            pairs.Add(("Status", "configured"));
        else
            pairs.Add(("Status", "not configured"));

        if (linked == true)
        {
            // WhatsApp-style self.e164 if present
            if (raw.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                var e164 = GetString(self, "e164") ?? GetString(self, "jid");
                if (!string.IsNullOrEmpty(e164))
                    pairs.Add(("Linked as", e164));
                else
                    pairs.Add(("Linked", "Yes"));
            }
            else
            {
                pairs.Add(("Linked", "Yes"));
            }
        }

        // Auth age (WA)
        if (raw.TryGetProperty("authAgeMs", out var ageMs) && ageMs.ValueKind == JsonValueKind.Number && ageMs.TryGetDouble(out var ageDouble))
            pairs.Add(("Auth age", FormatAge(ageDouble)));

        // Reconnect attempts (WA)
        if (raw.TryGetProperty("reconnectAttempts", out var attempts) && attempts.ValueKind == JsonValueKind.Number && attempts.TryGetInt32(out var att) && att > 0)
            pairs.Add(("Reconnect attempts", att.ToString()));

        // Probe info (TG/Discord/Signal/GoogleChat/iMessage)
        if (raw.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
        {
            var ok = GetBool(probe, "ok");
            var elapsed = probe.TryGetProperty("elapsedMs", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var ed) ? (double?)ed : null;
            var version = GetString(probe, "version");
            var probeError = GetString(probe, "error");
            if (ok == true)
            {
                var parts = new List<string> { "OK" };
                if (elapsed.HasValue) parts.Add($"{(int)elapsed.Value} ms");
                if (!string.IsNullOrEmpty(version)) parts.Add(version);
                pairs.Add(("Last probe", string.Join(" · ", parts)));
            }
            else if (ok == false)
            {
                pairs.Add(("Last probe", $"Failed · {probeError ?? "unknown error"}"));
            }
        }

        // Channel-specific identifiers
        if (GetString(raw, "botUsername") is { Length: > 0 } botUsername)
            pairs.Add(("Bot", "@" + botUsername));
        if (GetString(raw, "webhookUrl") is { Length: > 0 } webhook)
            pairs.Add(("Webhook", webhook));
        if (GetString(raw, "baseUrl") is { Length: > 0 } baseUrl)
            pairs.Add(("Base URL", baseUrl));

        // Accounts
        if (record.Accounts.Count > 0)
            pairs.Add(("Accounts", record.Accounts.Count == 1 ? "1" : record.Accounts.Count.ToString()));

        if (!string.IsNullOrEmpty(lastError))
            pairs.Add(("Last error", lastError));

        return pairs;
    }

    private FrameworkElement BuildLinkingPlaceholder(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = "LINKING",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var qrImage = new Image
        {
            Width = 180,
            Height = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };

        // Initial message is a call-to-action, not "scan this QR" — the QR
        // doesn't exist yet. RenderQrAsync (and the success path) replace this
        // text with the real instructions once a QR is on screen.
        var messageBlock = new TextBlock
        {
            Text = "Click \"Show QR\" to start linking your phone to this device.",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };

        // Collapsed diagnostic detail — populated by StartLinkingAsync when a
        // call fails so the user can see exactly what the gateway said
        // instead of just our paraphrased error message.
        var diagnostic = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            Header = "Why isn't this working?",
            Visibility = Visibility.Collapsed,
        };
        var diagnosticBody = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 8, 0, 0),
        };
        diagnostic.Content = diagnosticBody;

        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var showQrBtn = new Button { Content = "Show QR", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var relinkBtn = new Button { Content = "Relink" };
        buttonsRow.Children.Add(showQrBtn);
        buttonsRow.Children.Add(relinkBtn);

        // Re-entrancy lock: disable both buttons while a linking flow is in flight
        // so rapid Show QR / Relink clicks can't spawn parallel web.login.start calls.
        async Task RunLinking(bool force)
        {
            showQrBtn.IsEnabled = false;
            relinkBtn.IsEnabled = false;
            try
            {
                await StartLinkingAsync(qrImage, messageBlock, diagnostic, diagnosticBody, record.Id, force);
            }
            finally
            {
                showQrBtn.IsEnabled = true;
                relinkBtn.IsEnabled = true;
            }
        }
        showQrBtn.Click += async (_, _) => await RunLinking(force: false);
        relinkBtn.Click += async (_, _) => await RunLinking(force: true);

        stack.Children.Add(qrImage);
        stack.Children.Add(messageBlock);
        stack.Children.Add(diagnostic);
        stack.Children.Add(buttonsRow);
        return stack;
    }

    private async Task StartLinkingAsync(
        Image qrImage,
        TextBlock messageBlock,
        Expander diagnostic,
        TextBlock diagnosticBody,
        string channelId,
        bool force)
    {
        // Local helper: show the diagnostic disclosure with method/params/response
        // info so the user can see *exactly* what the gateway did or didn't do.
        void ShowDiagnostic(string method, object @params, string? error, string? rawResponse)
        {
            var parts = new List<string>
            {
                $"Method:   {method}",
                $"Channel:  {channelId}",
                $"Params:   {System.Text.Json.JsonSerializer.Serialize(@params)}",
            };
            if (!string.IsNullOrEmpty(error)) parts.Add($"Error:    {error}");
            if (!string.IsNullOrEmpty(rawResponse))
            {
                // Trim verbose stack traces; keep first 1000 chars (plenty for diagnosis,
                // small enough to read in the disclosure).
                var trimmed = rawResponse.Length > 1000 ? rawResponse[..1000] + "…" : rawResponse;
                parts.Add($"Response: {trimmed}");
            }
            diagnosticBody.Text = string.Join("\n", parts);
            diagnostic.Visibility = Visibility.Visible;
        }
        void HideDiagnostic()
        {
            diagnostic.IsExpanded = false;
            diagnostic.Visibility = Visibility.Collapsed;
            diagnosticBody.Text = "";
        }

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = "Not connected to a gateway. Open Connection settings to connect first.";
            HideDiagnostic();
            return;
        }

        // Cancel any previous linking session before starting a new one.
        var oldLinking = _linkingCts;
        _linkingCts = new CancellationTokenSource();
        var ct = _linkingCts.Token;
        if (oldLinking != null)
        {
            try { oldLinking.Cancel(); oldLinking.Dispose(); } catch { }
        }

        messageBlock.Text = "Requesting QR code from the gateway…";
        HideDiagnostic();

        var startParams = new { force, timeoutMs = 30000 };
        var start = await client.WebLoginStartAsync(force);
        if (ct.IsCancellationRequested) return;

        if (start == null)
        {
            // Only happens when the websocket is not connected; no gateway response
            // to surface.
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"Couldn't link {channelId}. Not connected to the gateway.";
            return;
        }

        if (!string.IsNullOrEmpty(start.Error))
        {
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"Couldn't link {channelId}. The gateway returned an error — see details below.";
            ShowDiagnostic("web.login.start", startParams, start.Error, start.RawResponse);
            return;
        }
        if (start.Connected)
        {
            messageBlock.Text = !string.IsNullOrEmpty(start.Message)
                ? start.Message
                : $"{channelId} is already linked.";
            qrImage.Visibility = Visibility.Collapsed;
            await RefreshAsync(probe: false);
            return;
        }
        if (string.IsNullOrEmpty(start.QrDataUrl))
        {
            // Gateway accepted the call (no Error) but returned no QR — show the
            // raw response so the user can see what it did say.
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = !string.IsNullOrEmpty(start.Message)
                ? start.Message
                : $"Gateway didn't return a QR for {channelId}. See details below for what it returned.";
            ShowDiagnostic("web.login.start", startParams, null, start.RawResponse);
            return;
        }

        await RenderQrAsync(qrImage, messageBlock, start.QrDataUrl);
        if (ct.IsCancellationRequested) return;
        messageBlock.Text = !string.IsNullOrEmpty(start.Message)
            ? start.Message
            : channelId.Equals("whatsapp", StringComparison.OrdinalIgnoreCase)
                ? "Open WhatsApp on your phone → Settings → Linked devices → scan this QR."
                : "Open the mobile app's linked-devices screen and scan this QR.";

        // Long-poll once for completion
        var waitParams = new { currentQrDataUrl = start.QrDataUrl, timeoutMs = 30000 };
        var waitResult = await client.WebLoginWaitAsync(start.QrDataUrl, timeoutMs: 30000);
        if (ct.IsCancellationRequested) return;
        if (waitResult == null)
        {
            messageBlock.Text = "Still waiting — click Show QR again if the code has expired.";
            return;
        }
        if (!string.IsNullOrEmpty(waitResult.Error))
        {
            messageBlock.Text = $"Link wait failed for {channelId}. See details below.";
            ShowDiagnostic("web.login.wait", waitParams, waitResult.Error, waitResult.RawResponse);
            return;
        }
        if (waitResult.Connected)
        {
            messageBlock.Text = !string.IsNullOrEmpty(waitResult.Message)
                ? waitResult.Message
                : $"{channelId} linked.";
            qrImage.Visibility = Visibility.Collapsed;
            await RefreshAsync(probe: false);
        }
        else if (!string.IsNullOrEmpty(waitResult.QrDataUrl) && waitResult.QrDataUrl != start.QrDataUrl)
        {
            // QR rotated; show the new one.
            await RenderQrAsync(qrImage, messageBlock, waitResult.QrDataUrl);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(waitResult.Message))
                messageBlock.Text = waitResult.Message;
        }
        else
        {
            messageBlock.Text = "Still waiting — click Show QR again if the code has expired.";
        }
    }

    private static async Task RenderQrAsync(Image target, TextBlock messageBlock, string dataUrl)
    {
        try
        {
            var commaIdx = dataUrl.IndexOf(',');
            if (commaIdx <= 0)
            {
                target.Visibility = Visibility.Collapsed;
                messageBlock.Text = "QR decode failed: malformed data URL from gateway.";
                return;
            }
            var base64 = dataUrl[(commaIdx + 1)..];
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                target.Visibility = Visibility.Collapsed;
                messageBlock.Text = "QR decode failed: invalid base64 from gateway.";
                return;
            }

            // The stream must outlive the SetSourceAsync call (BitmapImage reads
            // it during decode) but should be disposed after — its native COM
            // handle leaks otherwise.
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
            target.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Surface decode failures to the user — silent hide makes gateway faults
            // look like UX bugs.
            target.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"QR decode failed: {ex.Message}";
        }
    }

    private FrameworkElement BuildConfigPlaceholder(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = record.IsConfigured
                ? $"Edit this channel's settings in the gateway Config page."
                : $"After following the steps above, save the config to start the channel.",
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        var btn = new Button
        {
            Content = "Open Config page",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
        {
            ((IAppCommands)CurrentApp).Navigate("config");
        };
        stack.Children.Add(btn);
        return stack;
    }

    private async Task LogoutAsync(string channelId)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            ErrorBar.Title = "Not connected";
            ErrorBar.Message = "Connect to a gateway before logging out.";
            ErrorBar.IsOpen = true;
            return;
        }
        var dialog = new ContentDialog
        {
            Title = $"Log out of {channelId}?",
            Content = "This will sign out the channel. You can relink afterwards.",
            PrimaryButtonText = "Log out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var ok = await client.LogoutChannelAsync(channelId);
        if (!ok)
        {
            ErrorBar.Title = "Logout failed";
            ErrorBar.Message = $"Could not log out of {channelId}. The gateway may not support this action.";
            ErrorBar.IsOpen = true;
        }
        await RefreshAsync(probe: false);
    }

    // ─── Header state resolution (dot color, badge, subtitle) ─────────────

    private static (string DotBrushKey, string BadgeText, BadgeSeverity Severity, string Subtitle) ResolveHeaderState(ChannelRecord record)
    {
        var raw = record.RawStatus;

        // Status badge text follows naming.md status vocabulary: lowercase short
        // forms (connected / connecting / disconnected / reconnecting, etc.).
        // Stays consistent with the tray/Mission-Control status lines.

        // Unavailable on this OS short-circuit
        if (record.IsUnavailableOnWindows)
            return ("SystemFillColorNeutralBrush", "unavailable on Windows", BadgeSeverity.Neutral, "Requires a macOS host");

        if (raw.ValueKind != JsonValueKind.Object)
        {
            return record.IsConfigured
                ? ("SystemFillColorSuccessBrush", "configured", BadgeSeverity.Success, "")
                : ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, "Click to expand and configure");
        }

        var running = GetBool(raw, "running");
        var connected = GetBool(raw, "connected");
        var linked = GetBool(raw, "linked");
        var configured = GetBool(raw, "configured");
        var lastError = GetString(raw, "lastError") ?? GetString(raw, "error");

        // Error path
        if (!string.IsNullOrEmpty(lastError) && running != true)
            return ("SystemFillColorCriticalBrush", "error", BadgeSeverity.Critical, BuildErrorSubtitle(raw, lastError!));

        // WhatsApp-style flow: linked/connected
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanShowQr))
        {
            if (configured == false) return ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, "Click to expand and configure");
            if (linked == false) return ("SystemFillColorCriticalBrush", "not linked", BadgeSeverity.Critical, "Scan a QR to link this device");
            if (connected == true) return ("SystemFillColorSuccessBrush", "connected", BadgeSeverity.Success, BuildWhatsAppSubtitle(raw));
            if (running == true) return ("SystemFillColorCautionBrush", "running", BadgeSeverity.Caution, BuildWhatsAppSubtitle(raw));
            return ("SystemFillColorCautionBrush", "linked", BadgeSeverity.Caution, BuildWhatsAppSubtitle(raw));
        }

        // Generic flow
        if (running == true)
        {
            return ("SystemFillColorSuccessBrush", "running", BadgeSeverity.Success, BuildGenericSubtitle(record, raw));
        }
        if (configured == true)
            return ("SystemFillColorCautionBrush", "configured", BadgeSeverity.Caution, BuildGenericSubtitle(record, raw));

        return ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, "Click to expand and configure");
    }

    private static string BuildErrorSubtitle(JsonElement raw, string lastError)
    {
        var parts = new List<string> { Truncate(lastError, 80) };
        if (raw.TryGetProperty("lastProbeAt", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetDouble(out var d))
            parts.Add("Last probe " + FormatRelative(d));
        return string.Join(" · ", parts);
    }

    private static string BuildWhatsAppSubtitle(JsonElement raw)
    {
        var parts = new List<string>();
        if (raw.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object)
        {
            var e164 = GetString(self, "e164") ?? GetString(self, "jid");
            if (!string.IsNullOrEmpty(e164)) parts.Add("Linked as " + e164);
        }
        if (raw.TryGetProperty("authAgeMs", out var age) && age.ValueKind == JsonValueKind.Number && age.TryGetDouble(out var ageD))
            parts.Add("Auth age " + FormatAge(ageD));
        if (raw.TryGetProperty("lastMessageAt", out var lm) && lm.ValueKind == JsonValueKind.Number && lm.TryGetDouble(out var lmd))
            parts.Add("Last message " + FormatRelative(lmd));
        return string.Join(" · ", parts);
    }

    private static string BuildGenericSubtitle(ChannelRecord record, JsonElement raw)
    {
        var parts = new List<string>();

        if (GetString(raw, "botUsername") is { Length: > 0 } bot) parts.Add("@" + bot);
        if (GetString(raw, "webhookUrl") is { Length: > 0 } hook) parts.Add(Truncate(hook, 48));

        if (raw.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
        {
            var ok = GetBool(probe, "ok");
            if (ok == true && probe.TryGetProperty("elapsedMs", out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var elD))
                parts.Add($"Probe {(int)elD} ms");
        }
        if (raw.TryGetProperty("lastProbeAt", out var lp) && lp.ValueKind == JsonValueKind.Number && lp.TryGetDouble(out var lpd))
            parts.Add("Last probe " + FormatRelative(lpd));

        if (parts.Count == 0 && record.IsConfigured)
            parts.Add("Configured");
        return string.Join(" · ", parts);
    }

    // ─── Tiny utility helpers ─────────────────────────────────────────────

    private static FrameworkElement BuildInfoText(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string FormatAge(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays} d";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} m";
        return $"{(int)ts.TotalSeconds} s";
    }

    private static string FormatRelative(double epochMs)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs);
        var diff = DateTimeOffset.UtcNow - when;
        if (diff.TotalSeconds < 0) return "just now";
        if (diff.TotalMinutes < 1) return $"{(int)diff.TotalSeconds} s ago";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} h ago";
        return $"{(int)diff.TotalDays} d ago";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private enum BadgeSeverity { Neutral, Success, Caution, Critical }
}

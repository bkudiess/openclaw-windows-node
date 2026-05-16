using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Diagnostics page (route still "debug" for back-compat with command-palette
/// and deep-link aliases). Organized around three user tasks:
///   1. Share diagnostics with support
///   2. Inspect local diagnostics
///   3. Developer tools
///
/// High-density diagnostic streams (connection event timeline / recent log)
/// open inside the page itself rather than in a separate window — the
/// Visibility-swap pattern matches ConnectionPage.AddGatewayPanel.
///
/// Observes the single application model (AppState) directly per
/// docs/DATA_FLOW_ARCHITECTURE.md — no HubWindow dependency.
/// </summary>
public sealed partial class DebugPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;

    private AppState? _appState;
    private bool _suppressOverrideChange;

    private static readonly string LocalAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray");
    private static readonly string LogPath = Path.Combine(LocalAppData, "openclaw-tray.log");
    private static readonly string DeviceKeyPath = Path.Combine(LocalAppData, "device-key-ed25519.json");

    // Brushes for the colored timeline / log rendering. Use SystemFill*
    // theme tokens (per docs/design/tokens.md) so the colors track
    // light/dark/HC themes and stay consistent with the ConnectionPage
    // status dots. ConnectionStatusWindow:33-40 still uses ARGB literals
    // — flagged as drift in docs/design (see surfaces/diagnostics-page.md
    // "Drift candidates").
    private static Brush ErrorTextBrush => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    private static Brush WarnTextBrush  => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    private static Brush AuthTextBrush  => (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"];
    private static Brush OkTextBrush    => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
    private static Brush DimTextBrush   => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    // Detail view mode tracking. Determines what the toolbar buttons do
    // and what content gets rendered into DetailRichText. Bumped on
    // every EnterDetailView so deferred work (background log read,
    // queued live-event handler) can skip updates that arrive after
    // a mode switch or page navigation (Hanselman v2 review #5, #6).
    private enum DetailMode { None, Timeline, Log }
    private DetailMode _detailMode = DetailMode.None;
    private int _detailGeneration;

    // Hard cap on rows kept in the timeline / log RichTextBlock so a
    // long-running session with high event churn doesn't grow the UI
    // buffer without bound. Mirrors ConnectionDiagnostics.Capacity (500)
    // so the visible buffer matches the upstream ring buffer.
    // Hanselman v1 review finding #5.
    private const int MaxTimelineRows = 500;

    // Plain-text mirror of timeline / log rows for the Copy toolbar
    // action. Replaces the previous StringBuilder so we can cap the
    // mirror to MaxTimelineRows in O(1).
    private readonly Queue<string> _detailPlainLines = new();

    // Events seen during the initial snapshot pass. Only used as a
    // dedupe set during the snapshot/subscribe overlap window — once
    // LoadTimelineEvents() finishes, the set is cleared and the live
    // handler is the sole writer (Hanselman v2 review #1, #2). This
    // avoids two problems with keeping the set forever:
    //   * unbounded growth on long-running sessions, and
    //   * value-equality collisions when ConnectionDiagnosticEvent
    //     records share (Timestamp, Category, Message, Detail) — DateTime
    //     resolution is ~15.6 ms on Windows so reconnect-storm bursts
    //     can produce records that compare equal.
    private readonly HashSet<ConnectionDiagnosticEvent> _renderedEvents = new();
    private bool _renderedEventsActive;

    // Active subscription to live timeline events. Held so we can detach
    // when leaving the detail view OR when navigating off the page —
    // OnNavigatedFrom must call UnsubscribeTimeline or the handler
    // pins the page in memory (Hanselman v1 review finding #1).
    private ConnectionDiagnostics? _subscribedDiagnostics;

    public DebugPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            CurrentApp.SettingsChanged -= OnSettingsChanged;
            UnsubscribeTimeline();
        };
    }

    public void Initialize()
    {
        // Defensive -= before += guards against double-subscription if
        // the page ever gets cached (NavigationCacheMode != Disabled)
        // and Initialize() runs twice on the same instance. Mirrors
        // SessionsPage.xaml.cs:34 (Hanselman v2 review #3).
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        CurrentApp.SettingsChanged -= OnSettingsChanged;

        _appState = CurrentApp.AppState;
        if (_appState != null) _appState.PropertyChanged += OnAppStateChanged;
        // Listen for Settings → Save round-trips so the gateway URL in
        // the top InfoBar updates without waiting for a Status flip
        // (per docs/DATA_FLOW_ARCHITECTURE.md reactive-by-default ethos).
        CurrentApp.SettingsChanged += OnSettingsChanged;
        UpdateStatusInfoBar();
        LoadDeviceIdentity();
        LoadChatSurfaceOverrides();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Status):
            case nameof(AppState.GatewaySelf):
                UpdateStatusInfoBar();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => UpdateStatusInfoBar();

    /// <summary>
    /// Tear down any live ConnectionDiagnostics subscription when the
    /// user navigates to a different page. Without this override the
    /// timeline subscription survives navigation, pins this Page in
    /// memory, and dispatches UI updates to a detached visual tree.
    /// Hanselman review finding #1.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UnsubscribeTimeline();
        _detailMode = DetailMode.None;
        base.OnNavigatedFrom(e);
    }

    // ── Top status InfoBar ───────────────────────────────────────────

    private void UpdateStatusInfoBar()
    {
        var gatewayUrl = CurrentApp.Settings?.GetEffectiveGatewayUrl();
        var gatewayDisplay = string.IsNullOrWhiteSpace(gatewayUrl) ? "no gateway configured" : gatewayUrl;
        var status = _appState?.Status ?? ConnectionStatus.Disconnected;

        switch (status)
        {
            case ConnectionStatus.Connected:
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Connected";
                StatusInfoBar.Message = $"OpenClaw is connected to {gatewayDisplay}.";
                break;
            case ConnectionStatus.Connecting:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Connecting";
                StatusInfoBar.Message = $"Connecting to {gatewayDisplay}…";
                break;
            case ConnectionStatus.Disconnected:
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "Disconnected";
                StatusInfoBar.Message = $"Not connected. Gateway: {gatewayDisplay}.";
                break;
            case ConnectionStatus.Error:
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Connection error";
                StatusInfoBar.Message = $"Last gateway: {gatewayDisplay}. See the event timeline.";
                break;
            default:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Status unknown";
                StatusInfoBar.Message = $"Gateway: {gatewayDisplay}.";
                break;
        }
    }

    private void OnManageOnConnection(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    // ── In-page detail view (timeline / log) ─────────────────────────

    private void OnShowEventTimeline(object sender, RoutedEventArgs e)
        => EnterDetailView(DetailMode.Timeline);

    private void OnShowRecentLog(object sender, RoutedEventArgs e)
        => EnterDetailView(DetailMode.Log);

    private void OnBackToMain(object sender, RoutedEventArgs e)
        => LeaveDetailView();

    private void EnterDetailView(DetailMode mode)
    {
        _detailMode = mode;
        // Bump generation BEFORE clearing buffers so any in-flight
        // queued live event handler or pending log-read continuation
        // sees a stale generation and skips its UI write.
        _detailGeneration++;
        _detailPlainLines.Clear();
        _renderedEvents.Clear();
        _renderedEventsActive = false;
        DetailRichText.Blocks.Clear();

        if (mode == DetailMode.Timeline)
        {
            DetailTitle.Text = "Connection event timeline";
            DetailCaption.Text = "Live diagnostics from the gateway connection manager. Newest entries appear at the bottom.";
            DetailClearButton.Visibility = Visibility.Visible;
            DetailOpenFileButton.Visibility = Visibility.Collapsed;
            DetailRefreshButton.Visibility = Visibility.Collapsed;
            // Subscribe BEFORE snapshotting so any event that arrives
            // mid-load is captured by the live handler and either
            // rendered by the snapshot path or skipped by the dedupe
            // set. Avoids the gap that drops events on busy gateways
            // (Hanselman v1 review finding #3).
            SubscribeTimeline();
            LoadTimelineEvents();
        }
        else if (mode == DetailMode.Log)
        {
            DetailTitle.Text = "Recent log";
            DetailCaption.Text = $"Last 200 lines of {LogPath}. Severity is parsed from [info]/[warn]/[error] tags.";
            DetailClearButton.Visibility = Visibility.Collapsed;
            DetailOpenFileButton.Visibility = Visibility.Visible;
            DetailRefreshButton.Visibility = Visibility.Visible;
            _ = LoadLogFileAsync(_detailGeneration);
        }

        MainView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;

        // Focus the back link so screen readers + keyboard users land on
        // the right element when entering the detail view.
        _ = DetailBackButton.Focus(FocusState.Programmatic);
    }

    private void LeaveDetailView()
    {
        UnsubscribeTimeline();
        _detailMode = DetailMode.None;
        _detailGeneration++;
        _renderedEventsActive = false;
        DetailView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        DetailRichText.Blocks.Clear();
        _detailPlainLines.Clear();
        _renderedEvents.Clear();
    }

    private void OnDetailRefresh(object sender, RoutedEventArgs e)
    {
        if (_detailMode == DetailMode.Log) _ = LoadLogFileAsync(_detailGeneration);
    }

    private void OnDetailCopy(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(string.Concat(_detailPlainLines));
    }

    private void OnDetailClear(object sender, RoutedEventArgs e)
    {
        if (_detailMode == DetailMode.Timeline)
        {
            // Same semantics as ConnectionStatusWindow.OnClearTimeline.
            DetailRichText.Blocks.Clear();
            _detailPlainLines.Clear();
            _renderedEvents.Clear();
            _subscribedDiagnostics?.Clear();
        }
    }

    private void OnDetailOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open log file failed: {ex.Message}");
        }
    }

    // ── Detail mode: timeline ────────────────────────────────────────

    private void LoadTimelineEvents()
    {
        var diagnostics = CurrentApp.ConnectionManager?.Diagnostics;
        if (diagnostics == null)
        {
            AppendPlain("No connection diagnostics available.\n");
            DetailRichText.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = "No connection diagnostics available.", Foreground = DimTextBrush } }
            });
            return;
        }
        // Dedupe set is live ONLY for the snapshot/subscribe overlap
        // window. After the foreach completes the live handler is the
        // sole writer and the set becomes pure overhead (and a risk:
        // record value-equality can collide on same-millisecond reconnect
        // bursts and silently drop legitimate events — Hanselman v2 #2).
        _renderedEventsActive = true;
        try
        {
            foreach (var evt in diagnostics.GetAll())
            {
                // Subscribe-first ordering means the live handler may have
                // already rendered some of these; skip duplicates.
                if (!_renderedEvents.Add(evt)) continue;
                AppendTimelineEvent(evt);
            }
        }
        finally
        {
            // Drop the dedupe set so it can't grow without bound and
            // can't collide with future bursts.
            _renderedEventsActive = false;
            _renderedEvents.Clear();
        }
        ScrollDetailToEnd();
    }

    private void OnTimelineEventRecorded(object? sender, ConnectionDiagnosticEvent evt)
    {
        if (_detailMode != DetailMode.Timeline) return;
        // Capture generation so a queued lambda that runs after a mode
        // switch or LeaveDetailView is a no-op (Hanselman v2 #6).
        var gen = _detailGeneration;
        DispatcherQueue?.TryEnqueue(() =>
        {
            // Re-check mode AND generation inside the dispatched lambda
            // — the view could have changed between Record() and now.
            if (_detailMode != DetailMode.Timeline || _detailGeneration != gen) return;
            // Skip if the snapshot path already rendered this event.
            // Once the snapshot finishes, _renderedEventsActive flips
            // off and the set is cleared — every live event is unique
            // by construction from that point on.
            if (_renderedEventsActive && !_renderedEvents.Add(evt)) return;
            AppendTimelineEvent(evt);
            ScrollDetailToEnd();
        });
    }

    private void AppendTimelineEvent(ConnectionDiagnosticEvent evt)
    {
        DetailRichText.Blocks.Add(CreateTimelineParagraph(evt));
        // Cap UI buffer (Hanselman v1 #5). Blocks + _detailPlainLines
        // stay size-aligned via TrimRendered below + AppendPlain's own
        // cap. _renderedEvents is NOT trimmed because it's cleared
        // wholesale after the snapshot pass (Hanselman v2 #1).
        var detail = evt.Detail != null ? $"\n    {evt.Detail.Replace("\n", "\n    ")}" : "";
        AppendPlain($"{evt.Timestamp:HH:mm:ss.fff} [{evt.Category}] {evt.Message}{detail}\n");
        TrimRendered();
    }

    private void TrimRendered()
    {
        while (DetailRichText.Blocks.Count > MaxTimelineRows)
            DetailRichText.Blocks.RemoveAt(0);
    }

    private void SubscribeTimeline()
    {
        UnsubscribeTimeline();
        var diagnostics = CurrentApp.ConnectionManager?.Diagnostics;
        if (diagnostics == null) return;
        _subscribedDiagnostics = diagnostics;
        diagnostics.EventRecorded += OnTimelineEventRecorded;
    }

    private void UnsubscribeTimeline()
    {
        if (_subscribedDiagnostics != null)
        {
            _subscribedDiagnostics.EventRecorded -= OnTimelineEventRecorded;
            _subscribedDiagnostics = null;
        }
    }

    // Mirrors ConnectionStatusWindow.CreateTimelineParagraph so the two
    // surfaces format events identically. If that helper ever moves to a
    // shared place we should consolidate.
    private static Paragraph CreateTimelineParagraph(ConnectionDiagnosticEvent evt)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };

        para.Inlines.Add(new Run
        {
            Text = evt.Timestamp.ToString("HH:mm:ss.fff") + " ",
            Foreground = DimTextBrush
        });

        var direction = evt.Category switch
        {
            "handshake" when evt.Message.Contains("Sending") => "→ GW",
            "handshake" when evt.Message.Contains("Received") || evt.Message.Contains("hello-ok") => "← GW",
            "handshake" when evt.Message.Contains("Raw error") => "← GW",
            "handshake" when evt.Message.Contains("Connect error") => "← GW",
            "warning" when evt.Message.Contains("Connect error") || evt.Message.Contains("Gateway") => "← GW",
            "warning" when evt.Message.Contains("authentication failed") => "← GW",
            "error" when evt.Message.Contains("Authentication") || evt.Message.Contains("signature") => "← GW",
            "websocket" when evt.Message.Contains("connecting") => "→ GW",
            "websocket" when evt.Message.Contains("connected") => "← GW",
            "websocket" when evt.Message.Contains("disconnected") || evt.Message.Contains("error") => "← GW",
            _ => "    "
        };
        para.Inlines.Add(new Run { Text = direction + " ", Foreground = DimTextBrush });
        para.Inlines.Add(new Run { Text = $"[{evt.Category}] ", Foreground = DimTextBrush });

        Brush? brush = evt.Category switch
        {
            "error" or "warning" => ErrorTextBrush,
            "credential" => AuthTextBrush,
            "handshake" when evt.Message.Contains("hello-ok") => OkTextBrush,
            "handshake" when evt.Message.Contains("error", StringComparison.OrdinalIgnoreCase) => ErrorTextBrush,
            "handshake" => AuthTextBrush,
            "state" when evt.Message.Contains("Connected") || evt.Message.Contains("Ready")
                || evt.Message.Contains("hello-ok") => OkTextBrush,
            "state" when evt.Message.Contains("Error") => ErrorTextBrush,
            "websocket" when evt.Message.Contains("error", StringComparison.OrdinalIgnoreCase) => ErrorTextBrush,
            "websocket" when evt.Message.Contains("connected", StringComparison.OrdinalIgnoreCase) => OkTextBrush,
            _ => null
        };

        para.Inlines.Add(brush != null
            ? new Run { Text = evt.Message, Foreground = brush }
            : new Run { Text = evt.Message });

        if (!string.IsNullOrEmpty(evt.Detail))
        {
            para.Inlines.Add(new Run
            {
                Text = "\n    " + evt.Detail.Replace("\n", "\n    "),
                Foreground = DimTextBrush,
                FontSize = 10
            });
        }
        return para;
    }

    // ── Detail mode: recent log ──────────────────────────────────────

    // Parse the leading "[severity]" or "LEVEL " marker in log lines.
    // Matches "[info]" / "[warn]" / "[error]" / "[debug]" plus a few of
    // the legacy uppercase forms ("INFO", "WARN", "ERROR"). Anything
    // else falls back to default coloring.
    private static readonly Regex LogSeverityPattern = new(
        @"\[(?<sev>info|warn|warning|error|debug|trace)\]|\b(?<bare>INFO|WARN|WARNING|ERROR|DEBUG|TRACE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    // Timestamp prefix recognized in our log lines so we can render it
    // dim without forcing a brittle full grammar.
    private static readonly Regex LogTimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    private async Task LoadLogFileAsync(int generation)
    {
        DetailRichText.Blocks.Clear();
        _detailPlainLines.Clear();

        if (!File.Exists(LogPath))
        {
            DetailRichText.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = "No log file found.", Foreground = DimTextBrush } }
            });
            AppendPlain("No log file found.\n");
            return;
        }

        string[] lines;
        try
        {
            // Hanselman v1 review findings #2 and #4:
            //   #2 — Logger holds the log open with FileAccess.Write +
            //        FileShare.Read (Logger.cs:109). Default File.ReadLines
            //        opens with FileShare.Read which excludes Write — so
            //        every read attempt failed with IOException as long
            //        as Logger was active (essentially always). The
            //        explicit FileShare.ReadWrite below is required for
            //        concurrent read while Logger holds the writer.
            //   #4 — Read tail on a background thread so a 5 MB log
            //        rotation does not stall the UI.
            lines = await Task.Run(() => ReadLogTail(LogPath, 200));
        }
        catch (Exception ex)
        {
            // The user may have switched modes or navigated away while
            // we awaited. Skip writing to the UI if so (Hanselman v2 #5).
            if (_detailMode != DetailMode.Log || _detailGeneration != generation) return;
            DetailRichText.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = $"Failed to read log: {ex.Message}", Foreground = ErrorTextBrush } }
            });
            return;
        }

        // Hanselman v2 #5: re-check generation after the await so a
        // long log read can't clobber a timeline view the user switched
        // to in the meantime.
        if (_detailMode != DetailMode.Log || _detailGeneration != generation) return;

        foreach (var line in lines)
        {
            DetailRichText.Blocks.Add(CreateLogParagraph(line));
            AppendPlain(line + "\n");
        }
        ScrollDetailToEnd();
    }

    private static string[] ReadLogTail(string path, int tailCount)
    {
        // FileShare.ReadWrite lets us coexist with the Logger writer.
        // Rolling Queue<string> keeps memory at O(tailCount) instead of
        // O(file size).
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var queue = new Queue<string>(tailCount);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (queue.Count == tailCount) queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue.ToArray();
    }

    private static Paragraph CreateLogParagraph(string line)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

        // Render the leading timestamp dim if present so the severity-
        // colored portion stands out without busy text.
        var tsMatch = LogTimestampPattern.Match(line);
        var bodyStart = 0;
        if (tsMatch.Success)
        {
            para.Inlines.Add(new Run { Text = tsMatch.Value, Foreground = DimTextBrush });
            bodyStart = tsMatch.Length;
        }

        var rest = line.Substring(bodyStart);
        var sevBrush = ResolveLogSeverityBrush(rest);

        if (sevBrush != null)
            para.Inlines.Add(new Run { Text = rest, Foreground = sevBrush });
        else
            para.Inlines.Add(new Run { Text = rest });

        return para;
    }

    private static SolidColorBrush? ResolveLogSeverityBrush(string text)
    {
        var match = LogSeverityPattern.Match(text);
        if (!match.Success) return null;
        var sev = (match.Groups["sev"].Success ? match.Groups["sev"].Value
                                                : match.Groups["bare"].Value).ToLowerInvariant();
        return sev switch
        {
            "error" => ErrorTextBrush as SolidColorBrush,
            "warn" or "warning" => WarnTextBrush as SolidColorBrush,
            "debug" or "trace" => DimTextBrush as SolidColorBrush,
            _ => null
        };
    }

    // ── Detail view: shared helpers ──────────────────────────────────

    private void AppendPlain(string text)
    {
        _detailPlainLines.Enqueue(text);
        // Keep plain-text mirror in lock-step with the visual buffer
        // cap so Copy never serializes more than MaxTimelineRows.
        while (_detailPlainLines.Count > MaxTimelineRows)
            _detailPlainLines.Dequeue();
    }

    private void ScrollDetailToEnd()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            DetailScroll.UpdateLayout();
            DetailScroll.ChangeView(null, DetailScroll.ScrollableHeight, null);
        });
    }

    // ── Section 1: Share diagnostics with support ────────────────────

    private async void OnCreateDiagnosticsBundle(object sender, RoutedEventArgs e)
    {
        await ShowBundlePreviewAsync(
            title: "Diagnostics bundle",
            buildText: CommandCenterTextHelper.BuildDebugBundle,
            suggestedFileName: $"openclaw-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            headerCaption: "This is the complete bundle that would be copied or saved.");
    }

    private async Task ShowBundlePreviewAsync(
        string title,
        Func<GatewayCommandCenterState, string> buildText,
        string suggestedFileName,
        string headerCaption)
    {
        if (XamlRoot == null) return;
        var state = CurrentApp.BuildCommandCenterState();
        if (state == null) return;

        string text;
        try
        {
            text = buildText(state) ?? string.Empty;
        }
        catch (Exception ex)
        {
            text = $"Failed to build diagnostics bundle: {ex.Message}";
        }

        var dialog = new DiagnosticsBundleDialog { XamlRoot = XamlRoot, Title = title };
        // Just-in-time HWND resolution so a Hub-window close that happens
        // between dialog open and Save click can't land a stale handle in
        // the file picker (Hanselman v2 #4).
        dialog.Configure(text, headerCaption, suggestedFileName,
            hwndProvider: () => CurrentApp.GetHubWindowHandle());
        await dialog.ShowAsync();
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var logsDir = Path.Combine(LocalAppData, "Logs");
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open diagnostics folder failed: {ex.Message}");
        }
    }

    private void OnCopySupportContext(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(CommandCenterTextHelper.BuildSupportContext);

    private void OnCopyDebugBundle(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(CommandCenterTextHelper.BuildDebugBundle);

    private void OnCopyBrowserSetup(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(CommandCenterTextHelper.BuildBrowserSetupGuidance);

    private void OnCopyPortDiagnostics(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(s => CommandCenterTextHelper.BuildPortDiagnosticsSummary(s.PortDiagnostics));

    private void OnCopyCapabilityDiagnostics(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary);

    private void CopyDiagnosticText(Func<GatewayCommandCenterState, string> build)
    {
        var state = CurrentApp.BuildCommandCenterState();
        if (state == null) return;
        try
        {
            ClipboardHelper.CopyText(build(state) ?? string.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy diagnostic failed: {ex.Message}");
        }
    }

    // ── Device identity ──────────────────────────────────────────────

    private void LoadDeviceIdentity()
    {
        try
        {
            if (File.Exists(DeviceKeyPath))
            {
                var json = File.ReadAllText(DeviceKeyPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("deviceId", out var id))
                {
                    var deviceId = id.GetString() ?? "Unknown";
                    DeviceIdText.Text = deviceId.Length > 24
                        ? string.Concat(deviceId.AsSpan(0, 16), "…", deviceId.AsSpan(deviceId.Length - 6))
                        : deviceId;
                    DeviceIdText.Tag = deviceId;
                }
                else
                {
                    DeviceIdText.Text = "Not found";
                }

                if (doc.RootElement.TryGetProperty("publicKey", out var pk))
                {
                    var pkText = pk.GetString() ?? "Unknown";
                    PublicKeyText.Text = pkText;
                    PublicKeyText.Tag = pkText;
                }
                else
                {
                    PublicKeyText.Text = "Not found";
                }
            }
            else
            {
                DeviceIdText.Text = "No device key file";
                PublicKeyText.Text = "—";
            }
        }
        catch (Exception ex)
        {
            DeviceIdText.Text = $"Error: {ex.Message}";
            PublicKeyText.Text = "—";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var full = DeviceIdText.Tag as string ?? DeviceIdText.Text ?? string.Empty;
        ClipboardHelper.CopyText(full);
    }

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        var full = PublicKeyText.Tag as string ?? PublicKeyText.Text ?? string.Empty;
        ClipboardHelper.CopyText(full);
    }

    // ── Section 3: Developer tools ───────────────────────────────────

    private void LoadChatSurfaceOverrides()
    {
        _suppressOverrideChange = true;
        try
        {
            SelectByTag(HubChatOverrideCombo, DebugChatSurfaceOverrides.HubChat.ToString());
            SelectByTag(TrayChatOverrideCombo, DebugChatSurfaceOverrides.TrayChat.ToString());
        }
        finally
        {
            _suppressOverrideChange = false;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static ChatSurfaceOverride ParseOverride(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ChatSurfaceOverride>(item.Tag?.ToString(), out var v))
            return v;
        return ChatSurfaceOverride.NoOverride;
    }

    private void OnHubChatOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverrideChange) return;
        DebugChatSurfaceOverrides.HubChat = ParseOverride(HubChatOverrideCombo);
    }

    private void OnTrayChatOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverrideChange) return;
        DebugChatSurfaceOverrides.TrayChat = ParseOverride(TrayChatOverrideCombo);
    }

    private ChatExplorationsWindow? _explorationsWindow;

    private void OnOpenChatExplorations(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_explorationsWindow is { } existing)
            {
                try { existing.Activate(); return; }
                catch { _explorationsWindow = null; }
            }
            _explorationsWindow = new ChatExplorationsWindow();
            _explorationsWindow.Closed += (_, _) => _explorationsWindow = null;
            _explorationsWindow.Activate();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnOpenChatExplorations failed: {ex}");
        }
    }

    private async void OnRelaunchOnboarding(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Relaunch first-run setup?",
            Content = "This will reopen the OpenClaw onboarding wizard. The current view will close.",
            PrimaryButtonText = "Relaunch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ((IAppCommands)CurrentApp).ShowOnboarding();
        }
    }
}

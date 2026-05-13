using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace OpenClawTray.Pages;

/// <summary>
/// Single inline surface for every privacy/security/capability control on this PC.
/// Replaces the previous Capabilities + Sandbox + Permissions + Voice cards. The
/// Sandbox and Permissions pages still exist in the project for backward compat
/// (no nav entries) but are not the source of truth — this page binds directly
/// to SettingsManager and exec-policy.json.
/// </summary>
public sealed partial class CapabilitiesPage : Page
{
    private HubWindow? _hub;
    private bool _loading;
    private bool _mcpTokenRevealed;
    private List<ExecRuleRow> _execRules = new();
    public ObservableCollection<CustomFolderRow> CustomFolders { get; } = new();

    private const string SavedApiKeySentinel = "••••••••";

    public CapabilitiesPage()
    {
        InitializeComponent();
        CustomFoldersList.ItemsSource = CustomFolders;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadAllFromSettings();
    }

    private void LoadAllFromSettings()
    {
        if (_hub?.Settings is not { } s) return;
        _loading = true;
        try
        {
            // Master node toggle moved to Connection page

            // Run Programs
            RunProgramsToggle.IsOn = s.NodeSystemRunEnabled;
            RunProgramsDetail.Visibility = s.NodeSystemRunEnabled ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunProgramsRadios(s);
            SelectAccessTag(DocsAccessCombo, s.SandboxDocumentsAccess);
            SelectAccessTag(DownloadsAccessCombo, s.SandboxDownloadsAccess);
            SelectAccessTag(DesktopAccessCombo, s.SandboxDesktopAccess);
            UpdateCustomFolderSummary(s);
            NetworkToggle.IsOn = s.SystemRunAllowOutbound;
            (s.SandboxClipboard switch
            {
                SandboxClipboardMode.Read => ClipReadRadio,
                SandboxClipboardMode.Write => ClipWriteRadio,
                SandboxClipboardMode.Both => ClipBothRadio,
                _ => ClipNoneRadio
            }).IsChecked = true;
            var secs = Math.Clamp(s.SandboxTimeoutMs / 1000, 5, 300);
            TimeoutSlider.Value = secs;
            TimeoutLabel.Text = $"Command timeout: {secs} sec";
            SelectMaxOutputTag(s.SandboxMaxOutputBytes);

            // Exec policy
            LoadExecPolicy();

            // Browser / Canvas
            BrowserToggle.IsOn = s.NodeBrowserProxyEnabled;
            CanvasToggle.IsOn = s.NodeCanvasEnabled;

            // MCP
            McpToggle.IsOn = s.EnableMcpServer;
            McpDetailPanel.Visibility = s.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
            UpdateMcpEndpoint();

            // Sensors
            CameraToggle.IsOn = s.NodeCameraEnabled;
            CameraDetailPanel.Visibility = s.NodeCameraEnabled ? Visibility.Visible : Visibility.Collapsed;
            CameraAlwaysAllowCb.IsChecked = s.CameraRecordingConsentGiven;

            ScreenToggle.IsOn = s.NodeScreenEnabled;
            ScreenDetailPanel.Visibility = s.NodeScreenEnabled ? Visibility.Visible : Visibility.Collapsed;
            ScreenAlwaysAllowCb.IsChecked = s.ScreenRecordingConsentGiven;

            SttToggle.IsOn = s.NodeSttEnabled;
            SttDetailPanel.Visibility = s.NodeSttEnabled ? Visibility.Visible : Visibility.Collapsed;

            LocationToggle.IsOn = s.NodeLocationEnabled;
            LocationDetailPanel.Visibility = s.NodeLocationEnabled ? Visibility.Visible : Visibility.Collapsed;

            // TTS lives on the Voice & Audio page now — settings still round-trip there.

            // Gateway allowlist (read-only)
            UpdateGatewayAllowlist();
        }
        finally
        {
            _loading = false;
        }
        UpdateRunProgramsSummary();
        UpdateLevelPicker();
    }

    // ─── Security Level ───────────────────────────────────────────────

    private void UpdateLevelPicker()
    {
        if (_hub?.Settings is not { } s) return;
        var drift = SecurityLevelResolver.DriftCount(s);
        var baseLevel = s.SecurityLevel == SecurityLevel.Custom ? SecurityLevel.Recommended : s.SecurityLevel;

        // Drift hint below the buttons (the only place we mention "Custom" now —
        // the hero card was removed because the blue accent border on the active
        // button already shows which level is selected).
        DriftPanel.Visibility = drift > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (drift > 0)
            DriftText.Text = $"{drift} setting{(drift == 1 ? "" : "s")} differ from {LevelLabel(baseLevel)}.";

        // Selection indicator is an overlay Border above each button so the Button's
        // default Fluent hover/pressed visual states don't override it.
        LockedDownSelectedOverlay.Visibility  = (drift == 0 && baseLevel == SecurityLevel.LockedDown)  ? Visibility.Visible : Visibility.Collapsed;
        RecommendedSelectedOverlay.Visibility = (drift == 0 && baseLevel == SecurityLevel.Recommended) ? Visibility.Visible : Visibility.Collapsed;
        TrustedSelectedOverlay.Visibility     = (drift == 0 && baseLevel == SecurityLevel.Trusted)     ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string LevelLabel(SecurityLevel l) => l switch
    {
        SecurityLevel.LockedDown => "Locked down",
        SecurityLevel.Trusted    => "Unprotected",
        _                        => "Recommended"
    };

    private void OnLevelLockedDownClick(object sender, RoutedEventArgs e) => ApplyLevel(SecurityLevel.LockedDown);
    private void OnLevelRecommendedClick(object sender, RoutedEventArgs e) => ApplyLevel(SecurityLevel.Recommended);

    private async void OnLevelTrustedClick(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Switching to Unprotected will:", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = "• Run programs directly on this PC with no container isolation\n" +
                   "• Pre-approve camera and screen capture\n" +
                   "• Allow outbound internet from agent code\n" +
                   "• Enable the local MCP server",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8
        });
        content.Children.Add(new TextBlock
        {
            Text = "Only use this if you trust every agent that can connect to your gateway.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.7
        });
        var dialog = new ContentDialog
        {
            Title = "Remove all protection?",
            Content = content,
            PrimaryButtonText = "Switch to Unprotected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ApplyLevel(SecurityLevel.Trusted);
    }

    private void OnResetLevelClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings is not { } s) return;
        var baseLevel = s.SecurityLevel == SecurityLevel.Custom ? SecurityLevel.Recommended : s.SecurityLevel;
        ApplyLevel(baseLevel);
    }

    private void ApplyLevel(SecurityLevel level)
    {
        if (_hub?.Settings is not { } s) return;
        SecurityLevelResolver.ApplyTo(s, level);
        s.Save();
        _hub.RaiseSettingsSaved();
        LoadAllFromSettings();
    }

    private void OnAnyLevelDrivenChanged()
    {
        if (_loading || _hub?.Settings is not { } s) return;
        var drift = SecurityLevelResolver.DriftCount(s);
        var baseLevel = s.SecurityLevel == SecurityLevel.Custom ? SecurityLevel.Recommended : s.SecurityLevel;
        s.SecurityLevel = drift > 0 ? SecurityLevel.Custom : baseLevel;
        UpdateLevelPicker();
    }

    // ─── Node Mode moved to Connection page ────────────────────────────────

    // ─── Run Programs ─────────────────────────────────────────────────

    private void OnRunProgramsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeSystemRunEnabled = RunProgramsToggle.IsOn;
        RunProgramsDetail.Visibility = RunProgramsToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
        UpdateRunProgramsSummary();
    }

    private void UpdateRunProgramsRadios(SettingsManager s)
    {
        var inContainer = s.SystemRunSandboxEnabled;
        InContainerSelectedOverlay.Visibility = inContainer ? Visibility.Visible : Visibility.Collapsed;
        DirectSelectedOverlay.Visibility = inContainer ? Visibility.Collapsed : Visibility.Visible;

        // Files / Network / Clipboard / Limits only apply inside the container.
        // Disable each child Expander when Direct is selected so the user can
        // see these settings exist but understands they're inactive. StackPanel
        // itself has no IsEnabled — IsEnabled is a Control property — so we
        // walk the children and set it on each Expander.
        foreach (var child in ContainerOnlyGroup.Children)
        {
            if (child is Control control) control.IsEnabled = inContainer;
        }
    }

    private void OnRunInContainerClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        if (s.SystemRunSandboxEnabled) return; // already in container
        s.SystemRunSandboxEnabled = true;
        s.Save();
        _hub.RaiseSettingsSaved();
        UpdateRunProgramsRadios(s);
        OnAnyLevelDrivenChanged();
        UpdateRunProgramsSummary();
    }

    private async void OnRunDirectClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        if (!s.SystemRunSandboxEnabled) return; // already direct
        // Risk-increasing transition — confirm.
        var dialog = new ContentDialog
        {
            Title = "Run programs directly?",
            Content = "Without the container, programs started by agents will run as you and can read/write any of your files, " +
                      "access your network, and use any device. Are you sure?",
            PrimaryButtonText = "Yes, run directly",
            CloseButtonText = "Cancel (keep container)",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            UpdateRunProgramsRadios(s);
            return;
        }
        s.SystemRunSandboxEnabled = false;
        s.Save();
        _hub.RaiseSettingsSaved();
        UpdateRunProgramsRadios(s);
        OnAnyLevelDrivenChanged();
        UpdateRunProgramsSummary();
    }

    private void OnDocsAccessChanged(object sender, SelectionChangedEventArgs e)
        => SetFolderAccess(s => s.SandboxDocumentsAccess = ParseAccessTag(DocsAccessCombo));
    private void OnDownloadsAccessChanged(object sender, SelectionChangedEventArgs e)
        => SetFolderAccess(s => s.SandboxDownloadsAccess = ParseAccessTag(DownloadsAccessCombo));
    private void OnDesktopAccessChanged(object sender, SelectionChangedEventArgs e)
        => SetFolderAccess(s => s.SandboxDesktopAccess = ParseAccessTag(DesktopAccessCombo));

    private void SetFolderAccess(Action<SettingsManager> mutate)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        mutate(s);
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
        UpdateRunProgramsSummary();
    }

    private void OnNetworkToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.SystemRunAllowOutbound = NetworkToggle.IsOn;
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
        UpdateRunProgramsSummary();
    }

    private void OnClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        if (sender is RadioButton rb && rb.IsChecked == true)
        {
            s.SandboxClipboard = (rb.Tag?.ToString()) switch
            {
                "Read" => SandboxClipboardMode.Read,
                "Write" => SandboxClipboardMode.Write,
                "Both" => SandboxClipboardMode.Both,
                _ => SandboxClipboardMode.None
            };
            s.Save();
            _hub.RaiseSettingsSaved();
            OnAnyLevelDrivenChanged();
        }
    }

    private void OnTimeoutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        var secs = (int)Math.Round(TimeoutSlider.Value);
        TimeoutLabel.Text = $"Command timeout: {secs} sec";
        s.SandboxTimeoutMs = secs * 1000;
        s.Save();
        _hub.RaiseSettingsSaved();
    }

    private void OnMaxOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        if (MaxOutputCombo.SelectedItem is ComboBoxItem item &&
            long.TryParse(item.Tag?.ToString(), out var bytes))
        {
            s.SandboxMaxOutputBytes = bytes;
            s.Save();
            _hub.RaiseSettingsSaved();
        }
    }

    // Custom folder management is fully inline now (above) — no more Sandbox-page detour.

    private void UpdateCustomFolderSummary(SettingsManager s)
    {
        CustomFolders.Clear();
        foreach (var f in s.SandboxCustomFolders ?? new())
            CustomFolders.Add(new CustomFolderRow(f.Path, f.Access));
        RefreshCustomFoldersUi();
    }

    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddCustomFolder(object sender, RoutedEventArgs e)
    {
        if (_hub is null || _hub.Settings is not { } s) return;
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(_hub);
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrWhiteSpace(folder.Path)) return;
        if (CustomFolders.Any(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase))) return;

        var row = new CustomFolderRow(folder.Path, SandboxFolderAccess.ReadOnly);
        row.InitialSelectionFired = true;
        CustomFolders.Add(row);
        RefreshCustomFoldersUi();
        s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = folder.Path, Access = SandboxFolderAccess.ReadOnly });
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
    }

    private void OnCustomFolderAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ComboBox combo || combo.DataContext is not CustomFolderRow row) return;
        if (!row.InitialSelectionFired) { row.InitialSelectionFired = true; return; }
        if (_hub?.Settings is not { } s) return;
        var newAccess = row.AccessIndex switch
        {
            2 => SandboxFolderAccess.ReadWrite,
            1 => SandboxFolderAccess.ReadOnly,
            _ => (SandboxFolderAccess?)null
        };
        var existing = s.SandboxCustomFolders.FirstOrDefault(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (newAccess is null)
        {
            // "Blocked" — remove the grant entirely + remove the row.
            if (existing != null) s.SandboxCustomFolders.Remove(existing);
            var rowToRemove = CustomFolders.FirstOrDefault(f =>
                string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            if (rowToRemove != null) CustomFolders.Remove(rowToRemove);
            RefreshCustomFoldersUi();
        }
        else if (existing != null)
        {
            existing.Access = newAccess.Value;
        }
        else
        {
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = row.Path, Access = newAccess.Value });
        }
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
    }

    private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path || string.IsNullOrEmpty(path)) return;
        var row = CustomFolders.FirstOrDefault(f => f.Path == path);
        if (row != null) CustomFolders.Remove(row);
        RefreshCustomFoldersUi();
        if (_hub?.Settings is not { } s) return;
        s.SandboxCustomFolders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        s.Save();
        _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
    }

    private void UpdateRunProgramsSummary()
    {
        if (_hub?.Settings is not { } s) return;
        if (!s.NodeSystemRunEnabled) { RunProgramsSummary.Text = "Lets agents run shell commands, scripts, and programs on this PC."; return; }
        var mode = s.SystemRunSandboxEnabled ? "In a container" : "Direct (no container)";
        var net  = s.SystemRunAllowOutbound ? "Internet on" : "Internet blocked";
        RunProgramsSummary.Text = $"{mode}  ·  {net}  ·  Files: {FmtAccess(s.SandboxDocumentsAccess)} Documents / {FmtAccess(s.SandboxDownloadsAccess)} Downloads / {FmtAccess(s.SandboxDesktopAccess)} Desktop";

        // Per-expander summaries — visible in collapsed headers so users see state without expanding
        FilesExpanderSummary.Text = $"  ·  {FmtAccess(s.SandboxDocumentsAccess)} / {FmtAccess(s.SandboxDownloadsAccess)} / {FmtAccess(s.SandboxDesktopAccess)}";
        NetworkExpanderSummary.Text = s.SystemRunAllowOutbound ? "  ·  Internet on" : "  ·  Internet blocked";
        ClipboardExpanderSummary.Text = s.SandboxClipboard switch
        {
            SandboxClipboardMode.Read  => "  ·  Read only",
            SandboxClipboardMode.Write => "  ·  Write only",
            SandboxClipboardMode.Both  => "  ·  Read and write",
            _                          => "  ·  None"
        };
        var secs = Math.Clamp(s.SandboxTimeoutMs / 1000, 5, 300);
        LimitsExpanderSummary.Text = $"  ·  {secs}s timeout";
        ApprovalExpanderSummary.Text = $"  ·  {_execRules.Count} rule{(_execRules.Count == 1 ? "" : "s")}";
    }

    private static string FmtAccess(SandboxFolderAccess? a) => a switch
    {
        SandboxFolderAccess.ReadOnly => "Read",
        SandboxFolderAccess.ReadWrite => "RW",
        _ => "None"
    };

    private static void SelectAccessTag(ComboBox combo, SandboxFolderAccess? access)
    {
        var target = access switch
        {
            SandboxFolderAccess.ReadOnly => "ReadOnly",
            SandboxFolderAccess.ReadWrite => "ReadWrite",
            _ => "None"
        };
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (string)item.Tag == target)
            { combo.SelectedIndex = i; return; }
        }
        combo.SelectedIndex = 0;
    }

    private static SandboxFolderAccess? ParseAccessTag(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item) return null;
        return (string)item.Tag switch
        {
            "ReadOnly" => SandboxFolderAccess.ReadOnly,
            "ReadWrite" => SandboxFolderAccess.ReadWrite,
            _ => null
        };
    }

    private void SelectMaxOutputTag(long bytes)
    {
        for (int i = 0; i < MaxOutputCombo.Items.Count; i++)
        {
            if (MaxOutputCombo.Items[i] is ComboBoxItem item &&
                long.TryParse(item.Tag?.ToString(), out var v) && v == bytes)
            { MaxOutputCombo.SelectedIndex = i; return; }
        }
        MaxOutputCombo.SelectedIndex = 1; // default 4 MiB
    }

    // ─── Exec policy (lives in exec-policy.json) ──────────────────────

    private void LoadExecPolicy()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");
            string? defaultAction = "deny";
            _execRules.Clear();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("defaultAction", out var da))
                    defaultAction = da.GetString() ?? "deny";
                if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var r in rules.EnumerateArray())
                    {
                        _execRules.Add(new ExecRuleRow
                        {
                            Pattern = r.TryGetProperty("pattern", out var p) ? p.GetString() ?? "" :
                                      r.TryGetProperty("Pattern", out var p2) ? p2.GetString() ?? "" : "",
                            Action = r.TryGetProperty("action", out var a) ? a.GetString() ?? "deny" : "deny",
                            Index = idx++
                        });
                    }
                }
            }
            for (int i = 0; i < ExecDefaultActionCombo.Items.Count; i++)
            {
                if (ExecDefaultActionCombo.Items[i] is ComboBoxItem item && (string)item.Tag == defaultAction)
                { ExecDefaultActionCombo.SelectedIndex = i; break; }
            }
            if (ExecDefaultActionCombo.SelectedIndex < 0) ExecDefaultActionCombo.SelectedIndex = 0;
            RefreshExecRulesList();
        }
        catch
        {
            ExecDefaultActionCombo.SelectedIndex = 0;
        }
    }

    private void RefreshExecRulesList()
    {
        for (int i = 0; i < _execRules.Count; i++) _execRules[i].Index = i;
        ExecRulesList.ItemsSource = null;
        ExecRulesList.ItemsSource = _execRules.Select(r => new
        {
            r.Pattern,
            r.Action,
            r.Index,
            ActionBrush = new SolidColorBrush(r.Action == "allow"
                ? global::Windows.UI.Color.FromArgb(255, 34, 139, 34)
                : global::Windows.UI.Color.FromArgb(255, 220, 53, 69))
        }).ToList();
    }

    private void OnExecDefaultActionChanged(object sender, SelectionChangedEventArgs e) { /* no-op until save */ }

    private void OnAddExecRule(object sender, RoutedEventArgs e)
    {
        var pattern = NewExecRulePattern.Text.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        var action = (NewExecRuleAction.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "deny";
        _execRules.Add(new ExecRuleRow { Pattern = pattern, Action = action });
        NewExecRulePattern.Text = "";
        RefreshExecRulesList();
    }

    private void OnRemoveExecRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int idx && idx < _execRules.Count)
        {
            _execRules.RemoveAt(idx);
            RefreshExecRulesList();
        }
    }

    private void OnSaveExecPolicy(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenClawTray", "exec-policy.json");
            var defaultAction = (ExecDefaultActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "deny";
            var policy = new
            {
                defaultAction,
                rules = _execRules.Select(r => new { r.Pattern, action = r.Action }).ToArray()
            };
            var json = JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            if (sender is Button btn)
            {
                btn.Content = "✓ Saved";
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Tick += (t, a) => { btn.Content = "Save rules"; timer.Stop(); };
                timer.Start();
            }
        }
        catch { }
    }

    // ─── Browser / Canvas ─────────────────────────────────────────────

    private void OnBrowserToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeBrowserProxyEnabled = BrowserToggle.IsOn;
        s.Save(); _hub.RaiseSettingsSaved();
    }

    private void OnCanvasToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeCanvasEnabled = CanvasToggle.IsOn;
        s.Save(); _hub.RaiseSettingsSaved();
    }

    // ─── MCP ──────────────────────────────────────────────────────────

    private async void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        if (McpToggle.IsOn && !s.EnableMcpServer)
        {
            // Risk-increasing — confirm first.
            var dialog = new ContentDialog
            {
                Title = "Start the local MCP server?",
                Content = "The MCP server lets local CLI tools (e.g., Claude Desktop, Cursor) use this PC's capabilities " +
                          "through a token-gated HTTP endpoint on localhost. Anyone with the token can use it.",
                PrimaryButtonText = "Start MCP server",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _loading = true;
                try { McpToggle.IsOn = false; } finally { _loading = false; }
                return;
            }
        }
        s.EnableMcpServer = McpToggle.IsOn;
        McpDetailPanel.Visibility = McpToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save(); _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
        UpdateMcpEndpoint();
    }

    private void UpdateMcpEndpoint()
    {
        McpEndpointText.Text = NodeService.McpServerUrl;
        if (_hub?.Settings?.EnableMcpServer == true)
        {
            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = File.Exists(tokenPath);
            McpStatusText.Text = tokenExists ? "Server enabled — token ready" : "Server enabled — token will be created on next start";
        }
        else
        {
            McpStatusText.Text = "";
        }
    }

    private static string? ReadMcpToken()
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (File.Exists(tokenPath))
                return File.ReadAllText(tokenPath).Trim();
        }
        catch { }
        return null;
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(McpEndpointText.Text)) return;
        var dp = new DataPackage();
        dp.SetText(McpEndpointText.Text);
        Clipboard.SetContent(dp);
        McpStatusText.Text = "URL copied to clipboard";
    }

    private void OnRevealMcpToken(object sender, RoutedEventArgs e)
    {
        var token = ReadMcpToken();
        if (string.IsNullOrEmpty(token)) { McpTokenText.Text = "(no token — start MCP server first)"; return; }
        if (_mcpTokenRevealed)
        {
            McpTokenText.Text = "•••••••••••••";
            _mcpTokenRevealed = false;
            if (sender is Button b1) b1.Content = "Reveal";
            return;
        }
        McpTokenText.Text = token;
        _mcpTokenRevealed = true;
        if (sender is Button b2) b2.Content = "Hide";
        // Auto-hide after 10s
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(10);
        t.Tick += (_, _) =>
        {
            if (_mcpTokenRevealed)
            {
                McpTokenText.Text = "•••••••••••••";
                _mcpTokenRevealed = false;
                if (sender is Button btn) btn.Content = "Reveal";
            }
            t.Stop();
        };
        t.Start();
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        var token = ReadMcpToken();
        if (string.IsNullOrEmpty(token)) { McpStatusText.Text = "Token file not found — start the MCP server first"; return; }
        var dp = new DataPackage();
        dp.SetText(token);
        Clipboard.SetContent(dp);
        McpStatusText.Text = "Token copied to clipboard";
    }

    // ─── Camera / Screen / Mic / Location ─────────────────────────────

    private void OnCameraToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeCameraEnabled = CameraToggle.IsOn;
        CameraDetailPanel.Visibility = CameraToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save(); _hub.RaiseSettingsSaved();
    }

    private async void OnCameraAlwaysAllowChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        var want = CameraAlwaysAllowCb.IsChecked == true;
        if (want && !s.CameraRecordingConsentGiven)
        {
            var dialog = new ContentDialog
            {
                Title = "Always allow camera?",
                Content = "Agents will be able to take camera photos and clips at any time without a prompt. You can revoke this later.",
                PrimaryButtonText = "Always allow",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _loading = true;
                try { CameraAlwaysAllowCb.IsChecked = false; } finally { _loading = false; }
                return;
            }
        }
        s.CameraRecordingConsentGiven = want;
        s.Save(); _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
    }

    private void OnOpenWindowsCamera(object sender, RoutedEventArgs e)
        => OpenWindowsSettings("ms-settings:privacy-webcam");

    private void OnScreenToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeScreenEnabled = ScreenToggle.IsOn;
        ScreenDetailPanel.Visibility = ScreenToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save(); _hub.RaiseSettingsSaved();
    }

    private async void OnScreenAlwaysAllowChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        var want = ScreenAlwaysAllowCb.IsChecked == true;
        if (want && !s.ScreenRecordingConsentGiven)
        {
            var dialog = new ContentDialog
            {
                Title = "Always allow screen recording?",
                Content = "Agents will be able to capture screenshots and record video of your screen at any time without a prompt.",
                PrimaryButtonText = "Always allow",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _loading = true;
                try { ScreenAlwaysAllowCb.IsChecked = false; } finally { _loading = false; }
                return;
            }
        }
        s.ScreenRecordingConsentGiven = want;
        s.Save(); _hub.RaiseSettingsSaved();
        OnAnyLevelDrivenChanged();
    }

    private void OnOpenWindowsScreen(object sender, RoutedEventArgs e)
        => OpenWindowsSettings("ms-settings:privacy-broadfilesystemaccess");

    private void OnSttToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeSttEnabled = SttToggle.IsOn;
        SttDetailPanel.Visibility = SttToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save(); _hub.RaiseSettingsSaved();
        UpdateSttEngineHint();
    }

    private void UpdateSttEngineHint()
    {
        SttEngineHint.Text = SttToggle.IsOn
            ? "Using Whisper (local). Model downloads once on first transcription."
            : "";
    }

    private void OnSttMoreSettingsClick(object sender, RoutedEventArgs e)
        => _hub?.NavigateTo("voice");

    private void OnOpenWindowsMic(object sender, RoutedEventArgs e)
        => OpenWindowsSettings("ms-settings:privacy-microphone");

    private void OnLocationToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _hub?.Settings is not { } s) return;
        s.NodeLocationEnabled = LocationToggle.IsOn;
        LocationDetailPanel.Visibility = LocationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        s.Save(); _hub.RaiseSettingsSaved();
    }

    private void OnOpenWindowsLocation(object sender, RoutedEventArgs e)
        => OpenWindowsSettings("ms-settings:privacy-location");

    private static void OpenWindowsSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // ─── TTS moved to Voice & Audio page ──────────────────────────────

    // ─── Gateway allowlist (read-only echo) ───────────────────────────

    private void UpdateGatewayAllowlist()
    {
        var config = _hub?.LastConfig;
        if (!config.HasValue)
        {
            GatewayAllowlistEmpty.Visibility = Visibility.Visible;
            GatewayAllowlistRepeater.ItemsSource = null;
            return;
        }
        try
        {
            var cmds = new List<string>();
            var cfg = config.Value;
            if (cfg.TryGetProperty("gateway", out var gw) &&
                gw.TryGetProperty("nodes", out var nodes) &&
                nodes.TryGetProperty("allowCommands", out var ac) &&
                ac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ac.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) cmds.Add(v);
                }
            }
            if (cmds.Count == 0)
            {
                GatewayAllowlistEmpty.Text = "No allowed commands configured in gateway.";
                GatewayAllowlistEmpty.Visibility = Visibility.Visible;
                GatewayAllowlistRepeater.ItemsSource = null;
                return;
            }
            GatewayAllowlistEmpty.Visibility = Visibility.Collapsed;
            GatewayAllowlistRepeater.ItemsSource = cmds.Select(c => new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212)),
                Margin = new Thickness(0, 0, 4, 4),
                Child = new TextBlock
                {
                    Text = c,
                    FontSize = 12,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
                }
            }).ToList();
        }
        catch
        {
            GatewayAllowlistEmpty.Text = "Failed to parse allowlist from gateway config.";
            GatewayAllowlistEmpty.Visibility = Visibility.Visible;
        }
    }

    // ─── Types ────────────────────────────────────────────────────────

    private class ExecRuleRow
    {
        public string Pattern { get; set; } = "";
        public string Action { get; set; } = "deny";
        public int Index { get; set; }
    }

    public sealed class CustomFolderRow
    {
        public string Path { get; }
        public int AccessIndex { get; set; }
        /// <summary>
        /// Tracks whether the ComboBox's SelectedIndex-binding has already fired
        /// its initial SelectionChanged event during materialization. Used by
        /// OnCustomFolderAccessChanged to skip that "fake" change.
        /// </summary>
        public bool InitialSelectionFired { get; set; }

        public CustomFolderRow(string path, SandboxFolderAccess access)
        {
            Path = path;
            AccessIndex = access switch
            {
                SandboxFolderAccess.ReadWrite => 2,
                SandboxFolderAccess.ReadOnly => 1,
                _ => 0
            };
        }
    }
}

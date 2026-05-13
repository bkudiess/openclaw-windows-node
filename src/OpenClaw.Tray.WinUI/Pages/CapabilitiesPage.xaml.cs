using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OpenClawTray.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private HubWindow? _hub;
    private bool _suppressMcpToggle;
    private bool _suppressTtsProviderChange;

    // Sentinel rendered into the API key PasswordBox so the user can see
    // that a key is already saved without us ever surfacing the plaintext.
    // Saving the form treats this exact value as "keep current key".
    private const string SavedApiKeySentinel = "••••••••";

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        HostnameText.Text = Environment.MachineName;

        BuildCapabilityToggles(hub);
        UpdateMcpStatus(hub);
        UpdateSttCard(hub);
        UpdateTtsCard(hub);
        UpdateNodeStatus(hub);
        UpdateLevelPicker(hub);
    }

    // ============================================================
    // Security Level picker
    // ============================================================

    private void UpdateLevelPicker(HubWindow hub)
    {
        if (hub.Settings == null) return;
        var settings = hub.Settings;
        var drift = SecurityLevelResolver.DriftCount(settings);
        var baseLevel = settings.SecurityLevel == SecurityLevel.Custom
            ? SecurityLevel.Recommended
            : settings.SecurityLevel;

        var (label, brush) = (settings.SecurityLevel, drift) switch
        {
            (_, > 0)                          => ($"{LevelLabel(baseLevel)} + {drift} change{(drift == 1 ? "" : "s")}", "SystemFillColorCautionBackgroundBrush"),
            (SecurityLevel.LockedDown, _)     => ("Locked Down",  "AccentFillColorTertiaryBrush"),
            (SecurityLevel.Trusted, _)        => ("Trusted",      "AccentFillColorTertiaryBrush"),
            _                                 => ("Recommended",  "AccentFillColorTertiaryBrush"),
        };
        LevelBadgeText.Text = label;
        if (Application.Current.Resources[brush] is Microsoft.UI.Xaml.Media.Brush b)
            LevelBadge.Background = b;

        DriftPanel.Visibility = drift > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (drift > 0)
        {
            DriftText.Text = $"{drift} setting{(drift == 1 ? "" : "s")} differ{(drift == 1 ? "s" : "")} from {LevelLabel(baseLevel)}.";
        }
    }

    private static string LevelLabel(SecurityLevel l) => l switch
    {
        SecurityLevel.LockedDown => "Locked Down",
        SecurityLevel.Trusted    => "Trusted",
        _                        => "Recommended",
    };

    private void OnLevelLockedDownClick(object sender, RoutedEventArgs e)
        => ApplyLevel(SecurityLevel.LockedDown, requireConfirm: false);

    private void OnLevelRecommendedClick(object sender, RoutedEventArgs e)
        => ApplyLevel(SecurityLevel.Recommended, requireConfirm: false);

    private async void OnLevelTrustedClick(object sender, RoutedEventArgs e)
    {
        // Trusted is dangerous as a one-click choice — confirm what the
        // user is about to opt into so it's never accidental.
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Switching to Trusted (developer) will:",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "• Run programs directly on this PC, with no container isolation\n" +
                   "• Pre-approve camera and screen capture (no per-call prompt)\n" +
                   "• Allow outbound internet access from agent code\n" +
                   "• Enable the local MCP server",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        content.Children.Add(new TextBlock
        {
            Text = "Use this only if you trust every agent that connects to your gateway. You can switch back at any time.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.7
        });

        var dialog = new ContentDialog
        {
            Title = "Switch to Trusted?",
            Content = content,
            PrimaryButtonText = "Switch to Trusted",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            ApplyLevel(SecurityLevel.Trusted, requireConfirm: false);
    }

    private void OnResetLevelClick(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        var baseLevel = _hub.Settings.SecurityLevel == SecurityLevel.Custom
            ? SecurityLevel.Recommended
            : _hub.Settings.SecurityLevel;
        ApplyLevel(baseLevel, requireConfirm: false);
    }

    private void ApplyLevel(SecurityLevel level, bool requireConfirm)
    {
        if (_hub?.Settings == null) return;
        SecurityLevelResolver.ApplyTo(_hub.Settings, level);
        _hub.Settings.Save();
        _hub.RaiseSettingsSaved();
        // Rebuild toggles so per-row IsOn reflects the newly applied defaults
        BuildCapabilityToggles(_hub);
        UpdateMcpStatus(_hub);
        UpdateSttCard(_hub);
        UpdateTtsCard(_hub);
        UpdateNodeStatus(_hub);
        UpdateLevelPicker(_hub);
    }

    /// <summary>
    /// Called by per-row toggles after they mutate level-driven settings,
    /// so the level badge can flip to "+ N changes" or back to base.
    /// </summary>
    private void OnLevelDrivenSettingChanged()
    {
        if (_hub?.Settings == null) return;
        var drift = SecurityLevelResolver.DriftCount(_hub.Settings);
        // Promote stored level to Custom only when the user has actually drifted;
        // demote back to base level when drift returns to zero.
        var baseLevel = _hub.Settings.SecurityLevel == SecurityLevel.Custom
            ? SecurityLevel.Recommended
            : _hub.Settings.SecurityLevel;
        _hub.Settings.SecurityLevel = drift > 0 ? SecurityLevel.Custom : baseLevel;
        UpdateLevelPicker(_hub);
    }

    // ============================================================
    // Advanced settings deep-links
    // ============================================================

    private void OnOpenSandbox(object sender, RoutedEventArgs e)
        => _hub?.NavigateTo("sandbox");

    private void OnOpenPermissions(object sender, RoutedEventArgs e)
        => _hub?.NavigateTo("permissions");

    private void OnOpenWindowsPrivacy(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy") { UseShellExecute = true }); }
        catch { }
    }

    private void BuildCapabilityToggles(HubWindow hub)
    {
        if (hub.Settings == null) return;
        var settings = hub.Settings;

        // Captions: tiny hint rendered under the toggle. Used to surface
        // OS permission status for camera/screen/mic so users see the
        // dependency without opening another page. Run Programs gets an
        // honest hint about container status.
        var capabilities = new (string Icon, string Label, bool Value, Action<bool> Setter, FrameworkElement? Sub, string? Caption)[]
        {
            ("🔌", "Node Mode",        settings.EnableNodeMode,           v => settings.EnableNodeMode = v,           null, null),
            ("🌐", "Browser Control",  settings.NodeBrowserProxyEnabled,  v => settings.NodeBrowserProxyEnabled = v,  null, null),
            ("📷", "Camera",           settings.NodeCameraEnabled,        v => settings.NodeCameraEnabled = v,        BuildAlwaysAllowSub(this, "Always allow camera (no per-call prompt)", settings.CameraRecordingConsentGiven, v => settings.CameraRecordingConsentGiven = v, hub), "🪟 Also requires Windows camera permission · check Windows Privacy settings"),
            ("🎨", "Canvas",           settings.NodeCanvasEnabled,        v => settings.NodeCanvasEnabled = v,        null, null),
            ("🖥️", "Screen Capture",   settings.NodeScreenEnabled,        v => settings.NodeScreenEnabled = v,        BuildAlwaysAllowSub(this, "Always allow screen recording (no per-call prompt)", settings.ScreenRecordingConsentGiven, v => settings.ScreenRecordingConsentGiven = v, hub), "🪟 Available · Windows screen capture is system-wide"),
            ("📍", "Location",         settings.NodeLocationEnabled,      v => settings.NodeLocationEnabled = v,      null, "🪟 Also requires Windows location permission"),
            ("⌨️", "Run Programs",     settings.NodeSystemRunEnabled,     v => settings.NodeSystemRunEnabled = v,     null, settings.SystemRunSandboxEnabled ? "📦 Runs in a Windows AppContainer · advanced sandbox options below" : "⚠️ Runs directly as you (no container) · advanced options below"),
            ("🔊", "Text-to-Speech",   settings.NodeTtsEnabled,           v => settings.NodeTtsEnabled = v,           null, null),
            ("🎤", "Speech-to-Text",   settings.NodeSttEnabled,           v => settings.NodeSttEnabled = v,           null, "🪟 Also requires Windows microphone permission"),
        };

        var items = new List<UIElement>();
        foreach (var (icon, label, value, setter, sub, caption) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"{icon}  {label}",
                IsOn = value,
                MinWidth = 200
            };

            var stack = new StackPanel { Spacing = 0 };
            stack.Children.Add(toggle);

            TextBlock? captionBlock = null;
            if (caption != null)
            {
                captionBlock = new TextBlock
                {
                    Text = caption,
                    FontSize = 11,
                    Margin = new Thickness(48, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Visibility = value ? Visibility.Visible : Visibility.Collapsed
                };
                stack.Children.Add(captionBlock);
            }

            if (sub != null)
            {
                sub.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                sub.Margin = new Thickness(48, 4, 0, 4);
                stack.Children.Add(sub);
            }

            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                if (captionBlock != null) captionBlock.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
                if (sub != null) sub.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
                settings.Save();
                hub.RaiseSettingsSaved();
                UpdateSttCard(hub);
                UpdateTtsCard(hub);
                UpdateNodeStatus(hub);
                OnLevelDrivenSettingChanged();
            };

            items.Add(stack);
        }

        CapabilityRepeater.ItemsSource = items;
    }

    private static CheckBox BuildAlwaysAllowSub(CapabilitiesPage page, string label, bool value, Action<bool> setter, HubWindow hub)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            FontSize = 12
        };
        ToolTipService.SetToolTip(cb, "When checked, agents can use this capability without a per-call permission prompt. You'll still see a recording indicator.");
        cb.Checked += (s, e) =>
        {
            setter(true);
            hub.Settings?.Save();
            hub.RaiseSettingsSaved();
            page.OnLevelDrivenSettingChanged();
        };
        cb.Unchecked += (s, e) =>
        {
            setter(false);
            hub.Settings?.Save();
            hub.RaiseSettingsSaved();
            page.OnLevelDrivenSettingChanged();
        };
        return cb;
    }

    // ============================================================
    // Speech-to-Text settings card
    // ============================================================

    private void UpdateSttCard(HubWindow hub)
    {
        var enabled = hub.Settings?.NodeSttEnabled == true;
        SttCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || hub.Settings == null) return;

        UpdateSttEngineHint(hub);
    }

    private void UpdateSttEngineHint(HubWindow hub)
    {
        // Whisper is the only engine. Surface model-readiness so the user
        // knows what (if anything) needs to happen before stt.* will work.
        //
        // Check the file directly via WhisperModelManager rather than going
        // through hub.VoiceServiceInstance — that instance is only created
        // by NodeService.RegisterCapabilities() at Connect time, so a user
        // who toggled STT on but hasn't reconnected yet would see a stale
        // "not downloaded" message even with the file on disk.
        var modelName = hub.Settings?.SttModelName ?? "base";
        var modelManager = new OpenClaw.Shared.Audio.WhisperModelManager(
            SettingsManager.SettingsDirectoryPath, new AppLogger());
        var modelDownloaded = modelManager.IsModelDownloaded(modelName);
        var modelDownloading = hub.VoiceServiceInstance?.IsWhisperDownloadingModel ?? false;

        if (modelDownloaded)
        {
            SttEngineHint.Text = "Whisper model is ready. Speech-to-text runs fully on this PC; no audio leaves the device.";
        }
        else if (modelDownloading)
        {
            SttEngineHint.Text = "Whisper model is downloading. Speech-to-text will be available once it's ready.";
        }
        else
        {
            SttEngineHint.Text = "Whisper model is not downloaded. Open More voice settings… to download it before using speech-to-text.";
        }
    }

    private void OnSttMoreSettingsClick(object sender, RoutedEventArgs e)
    {
        // Navigate the Hub to the dedicated voice settings page.
        _hub?.NavigateTo("voice");
    }

    // ============================================================
    // Text-to-Speech settings card
    // ============================================================

    private void UpdateTtsCard(HubWindow hub)
    {
        var enabled = hub.Settings?.NodeTtsEnabled == true;
        TtsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || hub.Settings == null) return;

        var settings = hub.Settings;

        _suppressTtsProviderChange = true;
        // ComboBox order: 0=Piper, 1=Windows, 2=ElevenLabs.
        TtsProviderComboBox.SelectedIndex = settings.TtsProvider switch
        {
            var p when string.Equals(p, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase) => 2,
            var p when string.Equals(p, TtsCapability.WindowsProvider, StringComparison.OrdinalIgnoreCase)    => 1,
            _ => 0  // default to Piper for unknown / null / whitespace
        };
        _suppressTtsProviderChange = false;

        // PasswordBox shows a masked sentinel when we already have a saved
        // key, so the user can tell something is set without us ever
        // putting plaintext on screen.
        TtsElevenLabsApiKeyBox.Password =
            string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
        TtsElevenLabsVoiceIdBox.Text = settings.TtsElevenLabsVoiceId;
        TtsElevenLabsModelBox.Text = settings.TtsElevenLabsModel;

        UpdateTtsElevenLabsPanelVisibility();
        TtsStatusText.Text = "";
    }

    private void UpdateTtsElevenLabsPanelVisibility()
    {
        var isEleven = (TtsProviderComboBox.SelectedItem is ComboBoxItem item)
            && string.Equals(item.Tag as string, TtsCapability.ElevenLabsProvider, StringComparison.OrdinalIgnoreCase);
        TtsElevenLabsPanel.Visibility = isEleven ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTtsProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTtsProviderChange) return;
        if (_hub?.Settings == null) return;

        var newProvider = (TtsProviderComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ? tag
            : TtsCapability.WindowsProvider;

        if (!string.Equals(_hub.Settings.TtsProvider, newProvider, StringComparison.OrdinalIgnoreCase))
        {
            _hub.Settings.TtsProvider = newProvider;
            _hub.Settings.Save();
            _hub.RaiseSettingsSaved();
            TtsStatusText.Text = $"Default provider: {newProvider}";
        }

        UpdateTtsElevenLabsPanelVisibility();
    }

    private void OnTtsElevenLabsCommitted(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;
        var settings = _hub.Settings;

        var changed = false;

        // Treat the sentinel as "keep existing"; only overwrite when the
        // user has typed a real key.
        var typedKey = TtsElevenLabsApiKeyBox.Password ?? "";
        if (!string.Equals(typedKey, SavedApiKeySentinel, StringComparison.Ordinal))
        {
            var trimmedKey = typedKey.Trim();
            if (!string.Equals(settings.TtsElevenLabsApiKey, trimmedKey, StringComparison.Ordinal))
            {
                settings.TtsElevenLabsApiKey = trimmedKey;
                changed = true;
            }
        }

        var voiceId = TtsElevenLabsVoiceIdBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsVoiceId, voiceId, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsVoiceId = voiceId;
            changed = true;
        }

        var model = TtsElevenLabsModelBox.Text?.Trim() ?? "";
        if (!string.Equals(settings.TtsElevenLabsModel, model, StringComparison.Ordinal))
        {
            settings.TtsElevenLabsModel = model;
            changed = true;
        }

        if (changed)
        {
            settings.Save();
            _hub.RaiseSettingsSaved();
            // Re-render the API key field so the sentinel tracks the newly
            // saved state instead of leaving the typed key visible.
            TtsElevenLabsApiKeyBox.Password =
                string.IsNullOrEmpty(settings.TtsElevenLabsApiKey) ? "" : SavedApiKeySentinel;
            TtsStatusText.Text = "ElevenLabs settings saved.";
        }
    }

    private void UpdateNodeStatus(HubWindow hub)
    {
        var nodeEnabled = hub.Settings?.EnableNodeMode ?? false;
        var isConnected = hub.CurrentStatus == ConnectionStatus.Connected;

        if (!nodeEnabled)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            NodeStatusText.Text = "Node mode disabled";
            NodeDetailsText.Text = "Enable Node Mode to provide device capabilities to agents.";
        }
        else if (isConnected)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = "Node active";

            var caps = new List<string>();
            if (hub.Settings?.NodeBrowserProxyEnabled == true) caps.Add("browser");
            if (hub.Settings?.NodeCameraEnabled == true) caps.Add("camera");
            if (hub.Settings?.NodeCanvasEnabled == true) caps.Add("canvas");
            if (hub.Settings?.NodeScreenEnabled == true) caps.Add("screen");
            if (hub.Settings?.NodeLocationEnabled == true) caps.Add("location");
            if (hub.Settings?.NodeTtsEnabled == true) caps.Add("tts");
            if (hub.Settings?.NodeSttEnabled == true) caps.Add("stt");
            NodeDetailsText.Text = caps.Count > 0
                ? $"Providing {caps.Count} capabilities: {string.Join(", ", caps)}"
                : "No capabilities enabled.";
        }
        else
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = "Node mode enabled, not connected";
            NodeDetailsText.Text = "Connect to a gateway to start providing device capabilities.";
        }
    }

    private void UpdateMcpStatus(HubWindow hub)
    {
        var settings = hub.Settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = System.IO.File.Exists(tokenPath);
            McpStatusText.Text = tokenExists ? "Server enabled — token ready" : "Server enabled — token will be created on next start";
        }
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle) return;
        if (_hub?.Settings == null) return;
        _hub.Settings.EnableMcpServer = McpToggle.IsOn;
        _hub.Settings.Save();
        _hub.RaiseSettingsSaved();
        UpdateMcpStatus(_hub);
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (System.IO.File.Exists(tokenPath))
            {
                var token = System.IO.File.ReadAllText(tokenPath).Trim();
                var dp = new DataPackage();
                dp.SetText(token);
                Clipboard.SetContent(dp);
                McpStatusText.Text = "Token copied to clipboard";
            }
            else
            {
                McpStatusText.Text = "Token file not found — start the MCP server first";
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Failed to read token: {ex.Message}";
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(NodeService.McpServerUrl);
        Clipboard.SetContent(dp);
        McpStatusText.Text = "URL copied to clipboard";
    }
}
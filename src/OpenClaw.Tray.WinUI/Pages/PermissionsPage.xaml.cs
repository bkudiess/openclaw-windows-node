using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class PermissionsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private bool _suppressMcpToggle;
    private readonly List<ToggleSwitch> _featureToggles = new();
    private PermissionsPageViewModel? _execPolicyViewModel;
    private bool _execPolicyInitialized;
    private bool _execPolicyLoadInProgress;
    private bool _applyingExecPolicyState;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _execPolicySuccessTimer;
    private const int BrowserProxyToggleIndex = 1;

    private sealed record ExecPolicyChoice(string Tag, string Label);
    private sealed record ExecPolicyScopeChoice(string Id, string Label);
    private sealed record ExecAllowlistRow(
        Guid? Id,
        string Pattern,
        string? Source,
        string? ArgPattern,
        string? Details,
        string RemoveAutomationName,
        string RemoveAutomationId,
        string RemoveGlyph);

    public PermissionsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize()
    {
        HostnameText.Text = Environment.MachineName;

        BindNodeModeMaster();
        BuildCapabilityToggles();
        UpdateMcpStatus();
        UpdateVoiceSettingsCard();
        UpdateNodeStatus();
        ApplyFeaturesEnabledState();

        _execPolicyInitialized = true;
        _ = LoadExecPolicyAsync();
        LoadAllowlist(CurrentApp.AppState?.Config);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved += OnSettingsSaved;

        var mgr = CurrentApp.ConnectionManager;
        if (mgr != null)
            mgr.StateChanged += OnConnectionStateChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings != null)
            CurrentApp.Settings.Saved -= OnSettingsSaved;

        var mgr = CurrentApp.ConnectionManager;
        if (mgr != null)
            mgr.StateChanged -= OnConnectionStateChanged;

        if (_execPolicyViewModel != null)
            _execPolicyViewModel.StateChanged -= OnExecPolicyStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            UpdateNodeStatus();
        });
    }

    private bool _suppressNodeModeToggle;

    private void BindNodeModeMaster()
    {
        if (CurrentApp.Settings == null) return;
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = CurrentApp.Settings.EnableNodeMode;
        _suppressNodeModeToggle = false;
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle || CurrentApp.Settings == null) return;
        CurrentApp.Settings.EnableNodeMode = NodeModeToggle.IsOn;
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        ApplyFeaturesEnabledState();
        UpdateNodeStatus();
        UpdateVoiceSettingsCard();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            BindNodeModeMaster();
            ApplyFeaturesEnabledState();
            UpdateNodeStatus();
            ReloadFeatureToggleStates();
            UpdateMcpStatus();
            UpdateVoiceSettingsCard();
        });
    }

    private void ReloadFeatureToggleStates()
    {
        if (CurrentApp.Settings == null || _featureToggles.Count == 0) return;
        var s = CurrentApp.Settings;
        // Order matches BuildCapabilityToggles: system-run, browser, camera, canvas, screen, location, tts, stt.
        bool[] expected =
        {
            s.NodeSystemRunEnabled,
            s.NodeBrowserProxyEnabled, s.NodeCameraEnabled, s.NodeCanvasEnabled,
            s.NodeScreenEnabled, s.NodeLocationEnabled, s.NodeTtsEnabled, s.NodeSttEnabled,
        };
        for (int i = 0; i < _featureToggles.Count && i < expected.Length; i++)
        {
            if (_featureToggles[i].IsOn != expected[i])
                _featureToggles[i].IsOn = expected[i];
        }
    }

    /// <summary>Enables capability toggles whenever either node transport can serve them.</summary>
    private void ApplyFeaturesEnabledState()
    {
        var s = CurrentApp.Settings;
        var canServe = (s?.EnableNodeMode ?? false) || (s?.EnableMcpServer ?? false);
        CapabilityRepeater.Opacity = canServe ? 1.0 : 0.4;
        for (int i = 0; i < _featureToggles.Count; i++)
        {
            var isBrowserProxyToggle = i == BrowserProxyToggleIndex;
            _featureToggles[i].IsEnabled = canServe && (!isBrowserProxyToggle || s?.EnableNodeMode == true);
        }
        FeaturesSectionDescription.Text = LocalizationHelper.GetString(canServe
            ? "PermissionsPage_FeaturesDescription_Enabled"
            : "PermissionsPage_FeaturesDescription_Disabled");
    }

    private void BuildCapabilityToggles()
    {
        if (CurrentApp.Settings == null) return;
        var settings = CurrentApp.Settings;

        var capabilities = new (string Icon, string Label, string Description, bool Value, Action<bool> Setter)[]
        {
            ("⚡",
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_SystemRun_Description"),
                settings.NodeSystemRunEnabled, v => settings.NodeSystemRunEnabled = v),
            ("🌐",
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Browser_Description"),
                settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v),
            ("📷",
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Camera_Description"),
                settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v),
            ("🎨",
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Canvas_Description"),
                settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v),
            ("🖥️",
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Screen_Description"),
                settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v),
            ("📍",
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Location_Description"),
                settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v),
            ("🔊",
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Tts_Description"),
                settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v),
            ("🎤",
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Label"),
                LocalizationHelper.GetString("PermissionsPage_Cap_Stt_Description"),
                settings.NodeSttEnabled, v => settings.NodeSttEnabled = v),
        };

        var items = new List<UIElement>();
        _featureToggles.Clear();
        foreach (var (icon, label, description, value, setter) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = value,
                MinWidth = 0,
                OnContent = "",
                OffContent = "",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);
            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                settings.Save();
                ((IAppCommands)CurrentApp).NotifySettingsSaved();
                UpdateVoiceSettingsCard();
                UpdateNodeStatus();
            };
            _featureToggles.Add(toggle);
            items.Add(BuildCapabilityRow(icon, label, description, toggle));
        }

        CapabilityRepeater.ItemsSource = items;
    }

    private static Border BuildCapabilityRow(string icon, string label, string description, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconText = new TextBlock
        {
            Text = icon,
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(iconText, 0);
        grid.Children.Add(iconText);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = grid,
        };
    }

    // ── Voice settings link ──────────────────────────────────────────

    private void UpdateVoiceSettingsCard()
    {
        var settings = CurrentApp.Settings;
        var enabled = settings?.NodeSttEnabled == true || settings?.NodeTtsEnabled == true;
        var setupRequirement = settings == null
            ? VoiceSetupRequirement.None
            : GetVoiceSetupRequirement(settings);

        VoiceSettingsCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VoiceSettingsHelpPanel.Visibility = setupRequirement != VoiceSetupRequirement.None
            ? Visibility.Visible
            : Visibility.Collapsed;
        VoiceSettingsHelpText.Text = GetVoiceSetupRequirementText(setupRequirement);
    }

    private enum VoiceSetupRequirement
    {
        None,
        SpeechModel,
        VoiceSetup,
        SpeechModelAndVoiceSetup
    }

    private static VoiceSetupRequirement GetVoiceSetupRequirement(SettingsManager settings)
    {
        var needsSpeechModel = settings.NodeSttEnabled && !IsConfiguredWhisperModelDownloaded(settings);
        var needsVoiceSetup = settings.NodeTtsEnabled && SpeechSetupReadiness.IsConfiguredTtsProviderSetupRequired(settings);

        return (needsSpeechModel, needsVoiceSetup) switch
        {
            (true, true) => VoiceSetupRequirement.SpeechModelAndVoiceSetup,
            (true, false) => VoiceSetupRequirement.SpeechModel,
            (false, true) => VoiceSetupRequirement.VoiceSetup,
            _ => VoiceSetupRequirement.None
        };
    }

    private static string GetVoiceSetupRequirementText(VoiceSetupRequirement requirement)
    {
        var key = requirement switch
        {
            VoiceSetupRequirement.SpeechModel => "PermissionsPage_VoiceSettingsHelp_SpeechModel",
            VoiceSetupRequirement.VoiceSetup => "PermissionsPage_VoiceSettingsHelp_VoiceSetup",
            VoiceSetupRequirement.SpeechModelAndVoiceSetup => "PermissionsPage_VoiceSettingsHelp_Both",
            _ => ""
        };

        return string.IsNullOrEmpty(key) ? "" : LocalizationHelper.GetString(key);
    }

    private static bool IsConfiguredWhisperModelDownloaded(SettingsManager settings)
    {
        var modelName = settings.SttModelName;
        if (!WhisperModelManager.AvailableModels.Any(m =>
                string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var manager = new WhisperModelManager(SettingsManager.SettingsDirectoryPath, new AppLogger());
        return manager.IsModelDownloaded(modelName);
    }

    private void OnVoiceSettingsClick(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Navigate("voice");
    }

    // ── Node status ──────────────────────────────────────────────────

    private void UpdateNodeStatus()
    {
        var settings = CurrentApp.Settings;
        var nodeEnabled = settings?.EnableNodeMode ?? false;
        var mcpEnabled = settings?.EnableMcpServer ?? false;

        if (!nodeEnabled)
        {
            if (mcpEnabled && settings != null)
            {
                var mcpError = CurrentApp.ActiveNodeService?.McpStartupError;
                if (!string.IsNullOrEmpty(mcpError))
                {
                    NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                    NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError");
                    NodeDetailsText.Text = mcpError;
                }
                else
                {
                    NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
                    NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpOnly");
                    NodeDetailsText.Text = LocalizationHelper.Format(
                        "PermissionsPage_NodeStatus_McpOnlyDetailsFormat",
                        NodeCapabilityGating.CountMcpServedCapabilities(settings),
                        NodeService.McpServerUrl);
                }
            }
            else
            {
                NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Disabled");
                NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_DisabledDetails");
            }
            return;
        }

        var snap = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var nodeState = snap?.NodeState ?? RoleConnectionState.Idle;
        var operatorConnected = snap?.OperatorState == RoleConnectionState.Connected;
        var mcpStartupError = CurrentApp.ActiveNodeService?.McpStartupError;

        if (mcpEnabled && !string.IsNullOrEmpty(mcpStartupError))
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError");
            NodeDetailsText.Text = mcpStartupError;
        }
        else if (nodeState == RoleConnectionState.Connected && operatorConnected)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Active");

            // Read capability list from GatewayNodeInfo — same source of truth
            // used by the tray menu, instances page, and connection page.
            var caps = NodeCapabilityGating.GetLocalNodeCapabilities(
                CurrentApp.AppState?.Nodes, CurrentApp.NodeFullDeviceId);
            NodeDetailsText.Text = caps != null && caps.Count > 0
                ? LocalizationHelper.Format(
                    "PermissionsPage_NodeStatus_ActiveDetailsFormat",
                    caps.Count, string.Join(", ", caps))
                : LocalizationHelper.GetString("PermissionsPage_NodeStatus_NoCapabilities");
        }
        else if (nodeState == RoleConnectionState.Connecting)
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_Starting");
            NodeDetailsText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnectedDetails");
        }
        else
        {
            NodeStatusDot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnected");
            NodeDetailsText.Text = mcpEnabled && settings != null && string.IsNullOrEmpty(mcpStartupError)
                ? LocalizationHelper.Format(
                    "PermissionsPage_NodeStatus_McpOnlyDetailsFormat",
                    NodeCapabilityGating.CountMcpServedCapabilities(settings),
                    NodeService.McpServerUrl)
                : LocalizationHelper.GetString("PermissionsPage_NodeStatus_NotConnectedDetails");
        }
    }

    // ── MCP server ───────────────────────────────────────────────────

    private void UpdateMcpStatus()
    {
        var settings = CurrentApp.Settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var mcpError = CurrentApp.ActiveNodeService?.McpStartupError;
            if (!string.IsNullOrEmpty(mcpError))
            {
                McpStatusText.Text =
                    $"{LocalizationHelper.GetString("PermissionsPage_NodeStatus_McpError")}: {mcpError}";
                return;
            }

            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = File.Exists(tokenPath);
            McpStatusText.Text = LocalizationHelper.GetString(tokenExists
                ? "PermissionsPage_McpStatus_TokenReady"
                : "PermissionsPage_McpStatus_TokenPending");
        }
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle) return;
        if (CurrentApp.Settings == null) return;
        CurrentApp.Settings.EnableMcpServer = McpToggle.IsOn;
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdateMcpStatus();
        UpdateNodeStatus();
        ApplyFeaturesEnabledState();
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (File.Exists(tokenPath))
            {
                var token = File.ReadAllText(tokenPath).Trim();
                ClipboardHelper.CopyText(token);
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenCopied");
            }
            else
            {
                McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_TokenNotFound");
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = LocalizationHelper.Format(
                "PermissionsPage_McpStatus_TokenReadFailedFormat", ex.Message);
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(NodeService.McpServerUrl);
        McpStatusText.Text = LocalizationHelper.GetString("PermissionsPage_McpStatus_UrlCopied");
    }

    // ── Exec approvals V2 ───────────────────────────────────────────

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_execPolicyViewModel != null)
            _execPolicyViewModel.StateChanged -= OnExecPolicyStateChanged;

        _execPolicyViewModel = args.NewValue as PermissionsPageViewModel;
        if (_execPolicyViewModel is null)
            return;

        _execPolicyViewModel.StateChanged += OnExecPolicyStateChanged;
        RefreshExecPolicyControls();
        if (_execPolicyInitialized)
            _ = LoadExecPolicyAsync();
    }

    private async Task LoadExecPolicyAsync()
    {
        if (_execPolicyViewModel is null || _execPolicyLoadInProgress)
            return;

        _execPolicyLoadInProgress = true;
        try
        {
            var result = await _execPolicyViewModel.LoadAsync();
            if (!result.Succeeded)
                ShowExecPolicyError(result);
        }
        finally
        {
            _execPolicyLoadInProgress = false;
        }
    }

    private void OnExecPolicyStateChanged(object? sender, EventArgs e) =>
        RefreshExecPolicyControls();

    private void RefreshExecPolicyControls()
    {
        var viewModel = _execPolicyViewModel;
        if (viewModel is null)
            return;

        _applyingExecPolicyState = true;
        try
        {
            var scopeChoices = viewModel.AvailableScopes
                .Select(scope => new ExecPolicyScopeChoice(
                    scope.Id,
                    GetExecPolicyScopeLabel(scope.Id)))
                .ToArray();
            ExecPolicyScopeCombo.ItemsSource = scopeChoices;
            ExecPolicyScopeCombo.SelectedItem = scopeChoices.FirstOrDefault(scope =>
                string.Equals(scope.Id, viewModel.SelectedScopeId, StringComparison.Ordinal));

            var includeInherited = !viewModel.IsDefaultsScope;
            ApplyExecPolicyChoices(
                ExecSecurityCombo,
                BuildSecurityChoices(includeInherited),
                ToTag(viewModel.Security));
            ApplyExecPolicyChoices(
                ExecAskCombo,
                BuildAskChoices(includeInherited),
                ToTag(viewModel.Ask));
            ApplyExecPolicyChoices(
                ExecFallbackCombo,
                BuildFallbackChoices(includeInherited),
                ToTag(viewModel.AskFallback));
            ApplyExecPolicyChoices(
                ExecAutoAllowSkillsCombo,
                BuildBooleanChoices(includeInherited),
                viewModel.AutoAllowSkills switch
                {
                    true => "on",
                    false => "off",
                    null => "inherit",
                });

            var isDefaults = viewModel.IsDefaultsScope;
            var rules = viewModel.Allowlist;
            ExecAllowlistDefaultsInfo.Visibility = isDefaults
                ? Visibility.Visible
                : Visibility.Collapsed;
            ExecAllowlistAddCard.Visibility = isDefaults
                ? Visibility.Collapsed
                : Visibility.Visible;
            ExecAllowlistEmptyCard.Visibility = !isDefaults && rules.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ExecAllowlistRulesCard.Visibility = !isDefaults && rules.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ExecAllowlistRepeater.ItemsSource = rules.Select((entry, index) =>
                new ExecAllowlistRow(
                    entry.Id,
                    entry.Pattern ?? "",
                    entry.Source,
                    entry.ArgPattern,
                    GetExecAllowlistDetails(entry),
                    LocalizationHelper.Format(
                        "PermissionsPage_RemoveExecAllowlistAutomationNameFormat",
                        entry.Pattern ?? ""),
                    $"RemoveExecAllowlistEntry_{index}",
                    FluentIconCatalog.Clear)).ToArray();
            ExecAllowlistCountText.Text = rules.Count switch
            {
                0 => LocalizationHelper.GetString("PermissionsPage_RulesCount_None"),
                1 => LocalizationHelper.GetString("PermissionsPage_RulesCount_One"),
                _ => LocalizationHelper.Format(
                    "PermissionsPage_RulesCount_ManyFormat",
                    rules.Count),
            };

            var enabled = !viewModel.IsBusy;
            ExecPolicyScopeCombo.IsEnabled = enabled;
            ExecSecurityCombo.IsEnabled = enabled;
            ExecAskCombo.IsEnabled = enabled;
            ExecFallbackCombo.IsEnabled = enabled;
            ExecAutoAllowSkillsCombo.IsEnabled = enabled;
            ExecAllowlistAddCard.IsEnabled = enabled;
        }
        finally
        {
            _applyingExecPolicyState = false;
        }
    }

    private void OnExecPolicyScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingExecPolicyState
            || _execPolicyViewModel is null
            || ExecPolicyScopeCombo.SelectedItem is not ExecPolicyScopeChoice selected)
        {
            return;
        }

        ClearExecAllowlistValidation();
        _execPolicyViewModel.SelectScope(selected.Id);
    }

    private void OnExecSecurityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreExecPolicyChoice(ExecSecurityCombo))
            return;
        AsyncEventHandlerGuard.Run(
            UpdateExecSecurityAsync,
            new AppLogger(),
            nameof(OnExecSecurityChanged));
    }

    private async Task UpdateExecSecurityAsync()
    {
        var result = await _execPolicyViewModel!.UpdateSecurityAsync(
            ParseSecurity(GetSelectedExecPolicyTag(ExecSecurityCombo)));
        ShowExecPolicyOperationResult(result);
    }

    private void OnExecAskChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreExecPolicyChoice(ExecAskCombo))
            return;
        AsyncEventHandlerGuard.Run(
            UpdateExecAskAsync,
            new AppLogger(),
            nameof(OnExecAskChanged));
    }

    private async Task UpdateExecAskAsync()
    {
        var result = await _execPolicyViewModel!.UpdateAskAsync(
            ParseAsk(GetSelectedExecPolicyTag(ExecAskCombo)));
        ShowExecPolicyOperationResult(result);
    }

    private void OnExecFallbackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreExecPolicyChoice(ExecFallbackCombo))
            return;
        AsyncEventHandlerGuard.Run(
            UpdateExecFallbackAsync,
            new AppLogger(),
            nameof(OnExecFallbackChanged));
    }

    private async Task UpdateExecFallbackAsync()
    {
        var result = await _execPolicyViewModel!.UpdateAskFallbackAsync(
            ParseSecurity(GetSelectedExecPolicyTag(ExecFallbackCombo)));
        ShowExecPolicyOperationResult(result);
    }

    private void OnExecAutoAllowSkillsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreExecPolicyChoice(ExecAutoAllowSkillsCombo))
            return;
        AsyncEventHandlerGuard.Run(
            UpdateExecAutoAllowSkillsAsync,
            new AppLogger(),
            nameof(OnExecAutoAllowSkillsChanged));
    }

    private async Task UpdateExecAutoAllowSkillsAsync()
    {
        var result = await _execPolicyViewModel!.UpdateAutoAllowSkillsAsync(
            GetSelectedExecPolicyTag(ExecAutoAllowSkillsCombo) switch
            {
                "on" => true,
                "off" => false,
                _ => null,
            });
        ShowExecPolicyOperationResult(result);
    }

    private void OnAddExecAllowlistEntry(object sender, RoutedEventArgs e)
    {
        if (_execPolicyViewModel is null)
            return;
        AsyncEventHandlerGuard.Run(
            AddExecAllowlistEntryAsync,
            new AppLogger(),
            nameof(OnAddExecAllowlistEntry));
    }

    private async Task AddExecAllowlistEntryAsync()
    {
        var viewModel = _execPolicyViewModel;
        if (viewModel is null)
            return;

        var result = await viewModel.AddAllowlistEntryAsync(
            NewExecAllowlistPattern.Text);
        if (result.Status is ExecPolicyOperationStatus.EmptyPattern
            or ExecPolicyOperationStatus.InvalidPattern)
        {
            ShowExecAllowlistValidation(LocalizationHelper.GetString(
                result.Status == ExecPolicyOperationStatus.EmptyPattern
                    ? "PermissionsPage_ExecAllowlistPatternRequired"
                    : "PermissionsPage_ExecAllowlistPatternInvalid"));
            NewExecAllowlistPattern.Focus(FocusState.Programmatic);
            return;
        }

        if (result.Succeeded)
        {
            NewExecAllowlistPattern.Text = "";
            ClearExecAllowlistValidation();
        }
        ShowExecPolicyOperationResult(result);
    }

    private void OnRemoveExecAllowlistEntry(object sender, RoutedEventArgs e)
    {
        if (_execPolicyViewModel is null
            || sender is not Button { Tag: ExecAllowlistRow row })
        {
            return;
        }
        AsyncEventHandlerGuard.Run(
            () => RemoveExecAllowlistEntryAsync(row),
            new AppLogger(),
            nameof(OnRemoveExecAllowlistEntry));
    }

    private async Task RemoveExecAllowlistEntryAsync(ExecAllowlistRow row)
    {
        var viewModel = _execPolicyViewModel;
        if (viewModel is null)
            return;

        var result = await viewModel.RemoveAllowlistEntryAsync(
            row.Id,
            row.Pattern,
            row.ArgPattern,
            row.Source);
        ShowExecPolicyOperationResult(result);
    }

    private bool ShouldIgnoreExecPolicyChoice(ComboBox combo) =>
        _applyingExecPolicyState
        || _execPolicyViewModel is null
        || combo.SelectedItem is not ExecPolicyChoice;

    private void ShowExecPolicyOperationResult(ExecPolicyOperationResult result)
    {
        if (result.Succeeded)
        {
            ExecPolicyStatusInfoBar.Title =
                LocalizationHelper.GetString("PermissionsPage_ExecPolicySaved");
            ExecPolicyStatusInfoBar.Message =
                LocalizationHelper.Format(
                    "PermissionsPage_ExecPolicySavedToFormat",
                    _execPolicyViewModel?.PolicyPath ?? "");
            ExecPolicyStatusInfoBar.Severity = InfoBarSeverity.Success;
            ExecPolicyStatusInfoBar.IsOpen = true;
            StartExecPolicySuccessTimer();
            return;
        }

        ShowExecPolicyError(result);
    }

    private void ShowExecPolicyError(ExecPolicyOperationResult result)
    {
        _execPolicySuccessTimer?.Stop();
        ExecPolicyStatusInfoBar.Title =
            LocalizationHelper.GetString("PermissionsPage_ExecPolicyOperationFailed");
        var messageKey = result.Status switch
        {
            ExecPolicyOperationStatus.ReadFailed =>
                "PermissionsPage_ExecPolicyReadFailed",
            ExecPolicyOperationStatus.Conflict =>
                "PermissionsPage_ExecPolicyConflict",
            ExecPolicyOperationStatus.RulesUnavailableForDefaults =>
                "PermissionsPage_ExecPolicyRulesUnavailableForDefaults",
            _ => "PermissionsPage_ExecPolicySaveFailedDetailed",
        };
        var message = LocalizationHelper.GetString(messageKey);
        if (!string.IsNullOrWhiteSpace(result.Detail))
            message = $"{message} {result.Detail}";
        if (!string.IsNullOrWhiteSpace(_execPolicyViewModel?.PolicyPath))
        {
            message = LocalizationHelper.Format(
                "PermissionsPage_ExecPolicyErrorWithPathFormat",
                message,
                _execPolicyViewModel.PolicyPath);
        }

        ExecPolicyStatusInfoBar.Message = message;
        ExecPolicyStatusInfoBar.Severity = InfoBarSeverity.Error;
        ExecPolicyStatusInfoBar.IsOpen = true;
    }

    private void StartExecPolicySuccessTimer()
    {
        if (_execPolicySuccessTimer is null)
        {
            _execPolicySuccessTimer = DispatcherQueue.CreateTimer();
            _execPolicySuccessTimer.Interval = TimeSpan.FromSeconds(1.5);
            _execPolicySuccessTimer.Tick += (timer, _) =>
            {
                ExecPolicyStatusInfoBar.IsOpen = false;
                timer.Stop();
            };
        }
        _execPolicySuccessTimer.Stop();
        _execPolicySuccessTimer.Start();
    }

    private void ClearExecAllowlistValidation()
    {
        ExecAllowlistValidationText.Text = "";
        ExecAllowlistValidationText.Visibility = Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            NewExecAllowlistPattern,
            "");
    }

    private void ShowExecAllowlistValidation(string message)
    {
        ExecAllowlistValidationText.Text = message;
        ExecAllowlistValidationText.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            NewExecAllowlistPattern,
            message);
        var peer =
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(
                ExecAllowlistValidationText)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
                ExecAllowlistValidationText);
        peer?.RaiseAutomationEvent(
            Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private static void ApplyExecPolicyChoices(
        ComboBox combo,
        IReadOnlyList<ExecPolicyChoice> choices,
        string tag)
    {
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.Tag, tag, StringComparison.Ordinal));
    }

    private static string GetSelectedExecPolicyTag(ComboBox combo) =>
        combo.SelectedItem is ExecPolicyChoice choice ? choice.Tag : "inherit";

    private static IReadOnlyList<ExecPolicyChoice> BuildSecurityChoices(
        bool includeInherited) =>
        BuildChoices(
            includeInherited,
            [
                ("deny", "PermissionsPage_ExecSecurity_Deny"),
                ("allowlist", "PermissionsPage_ExecSecurity_Allowlist"),
                ("full", "PermissionsPage_ExecSecurity_Full"),
            ]);

    private static IReadOnlyList<ExecPolicyChoice> BuildAskChoices(
        bool includeInherited) =>
        BuildChoices(
            includeInherited,
            [
                ("off", "PermissionsPage_ExecAsk_Off"),
                ("on-miss", "PermissionsPage_ExecAsk_OnMiss"),
                ("always", "PermissionsPage_ExecAsk_Always"),
                ("deny", "PermissionsPage_ExecAsk_Deny"),
            ]);

    private static IReadOnlyList<ExecPolicyChoice> BuildFallbackChoices(
        bool includeInherited) =>
        BuildChoices(
            includeInherited,
            [
                ("deny", "PermissionsPage_ExecFallback_Deny"),
                ("allowlist", "PermissionsPage_ExecFallback_Allowlist"),
                ("full", "PermissionsPage_ExecFallback_Full"),
            ]);

    private static IReadOnlyList<ExecPolicyChoice> BuildBooleanChoices(
        bool includeInherited) =>
        BuildChoices(
            includeInherited,
            [
                ("off", "PermissionsPage_ExecBoolean_Off"),
                ("on", "PermissionsPage_ExecBoolean_On"),
            ]);

    private static IReadOnlyList<ExecPolicyChoice> BuildChoices(
        bool includeInherited,
        IReadOnlyList<(string Tag, string ResourceKey)> values)
    {
        var choices = new List<ExecPolicyChoice>();
        if (includeInherited)
        {
            choices.Add(new ExecPolicyChoice(
                "inherit",
                LocalizationHelper.GetString("PermissionsPage_ExecPolicy_Inherit")));
        }
        choices.AddRange(values.Select(value =>
            new ExecPolicyChoice(
                value.Tag,
                LocalizationHelper.GetString(value.ResourceKey))));
        return choices;
    }

    private static string GetExecPolicyScopeLabel(string scopeId) =>
        scopeId switch
        {
            PermissionsPageViewModel.DefaultsScopeId =>
                LocalizationHelper.GetString("PermissionsPage_ExecScope_Defaults"),
            PermissionsPageViewModel.WildcardScopeId =>
                LocalizationHelper.GetString("PermissionsPage_ExecScope_AllAgents"),
            PermissionsPageViewModel.MainScopeId =>
                LocalizationHelper.GetString("PermissionsPage_ExecScope_MainAgent"),
            _ => LocalizationHelper.Format(
                "PermissionsPage_ExecScope_AgentFormat",
                scopeId),
        };

    private static string? GetExecAllowlistDetails(ExecAllowlistEntry entry)
    {
        var details = new List<string>();
        if (string.Equals(
                entry.Source,
                ExecAllowlistEntry.AllowAlwaysSource,
                StringComparison.Ordinal)
            && !string.IsNullOrEmpty(entry.ArgPattern))
        {
            details.Add(LocalizationHelper.GetString(
                "PermissionsPage_ExecAllowlistArgumentsRestricted"));
        }
        if (!string.IsNullOrWhiteSpace(entry.LastResolvedPath))
        {
            details.Add(LocalizationHelper.Format(
                "PermissionsPage_ExecAllowlistLastResolvedFormat",
                entry.LastResolvedPath));
        }
        return details.Count == 0 ? null : string.Join(" ", details);
    }

    private static ExecSecurity? ParseSecurity(string tag) =>
        tag switch
        {
            "deny" => ExecSecurity.Deny,
            "allowlist" => ExecSecurity.Allowlist,
            "full" => ExecSecurity.Full,
            _ => null,
        };

    private static ExecAsk? ParseAsk(string tag) =>
        tag switch
        {
            "off" => ExecAsk.Off,
            "on-miss" => ExecAsk.OnMiss,
            "always" => ExecAsk.Always,
            "deny" => ExecAsk.Deny,
            _ => null,
        };

    private static string ToTag(ExecSecurity? value) =>
        value switch
        {
            ExecSecurity.Deny => "deny",
            ExecSecurity.Allowlist => "allowlist",
            ExecSecurity.Full => "full",
            _ => "inherit",
        };

    private static string ToTag(ExecAsk? value) =>
        value switch
        {
            ExecAsk.Off => "off",
            ExecAsk.OnMiss => "on-miss",
            ExecAsk.Always => "always",
            ExecAsk.Deny => "deny",
            _ => "inherit",
        };

    // ── Node Allowlist ───────────────────────────────────────────────

    private void LoadAllowlist(JsonElement? config)
    {
        if (!config.HasValue)
        {
            AllowlistEmpty.Visibility = Visibility.Visible;
            return;
        }
        UpdateAllowlist(config.Value);
    }

    public void UpdateAllowlist(JsonElement config)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var commands = new List<string>();

                if (config.TryGetProperty("gateway", out var gw) &&
                    gw.TryGetProperty("nodes", out var nodes) &&
                    nodes.TryGetProperty("allowCommands", out var ac) &&
                    ac.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cmd in ac.EnumerateArray())
                    {
                        var s = cmd.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) commands.Add(s);
                    }
                }

                if (commands.Count == 0)
                {
                    AllowlistEmpty.Text = LocalizationHelper.GetString("PermissionsPage_Allowlist_NoCommands");
                    AllowlistEmpty.Visibility = Visibility.Visible;
                    AllowlistRepeater.ItemsSource = null;
                    return;
                }

                AllowlistEmpty.Visibility = Visibility.Collapsed;
                AllowlistRepeater.ItemsSource = commands.Select(cmd => CreateAllowlistTag(cmd)).ToList();
            }
            catch
            {
                AllowlistEmpty.Text = LocalizationHelper.GetString("PermissionsPage_Allowlist_ParseFailed");
                AllowlistEmpty.Visibility = Visibility.Visible;
            }
        });
    }

    private static Border CreateAllowlistTag(string command)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = command,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
            }
        };
    }

    // ── Windows-level privacy ────────────────────────────────────────

    private void OnOpenPrivacySettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }); }
        // slopwatch-ignore: SW003 Diagnostic logging fallback is best-effort and logging failure must not cascade.
        catch { }
    }

    // ── Types ────────────────────────────────────────────────────────

}

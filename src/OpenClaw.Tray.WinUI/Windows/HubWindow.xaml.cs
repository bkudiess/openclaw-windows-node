using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class HubWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    // Shared state accessible by pages
    private SettingsManager? _settings;
    public SettingsManager? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            // Apply persisted nav-pane state. NavView starts with its XAML
            // default of IsPaneOpen=true; honor the user's last preference
            // here so they don't re-toggle on every Hub open.
            if (value != null && NavView != null)
            {
                NavView.IsPaneOpen = value.HubNavPaneOpen;
            }
        }
    }
    public IOperatorGatewayClient? GatewayClient { get; set; }
    public ConnectionStatus CurrentStatus { get; set; }
    internal AppState? AppModel { get; set; }
    private string _currentAgentId = "main";
    public string CurrentAgentId => _currentAgentId;

    // Legacy compatibility alias
    public string SelectedAgentId => _currentAgentId;
    public Action<string?>? OpenDashboardAction { get; set; }
    public Action? CheckForUpdatesAction { get; set; }
    public Action? ConnectAction { get; set; }
    public Action? DisconnectAction { get; set; }
    public Action? ReconnectAction { get; set; }
    public Action? OpenSetupAction { get; set; }
    public Action? OpenConnectionStatusAction { get; set; }
    public Action? OpenVoiceAction { get; set; }
    public OpenClaw.Connection.IGatewayConnectionManager? ConnectionManager { get; set; }
    public OpenClaw.Connection.GatewayRegistry? GatewayRegistry { get; set; }

    // Node service state (set by App.xaml.cs in ShowHub)
    public bool NodeIsConnected { get; set; }
    public bool NodeIsPaired { get; set; }
    public bool NodeIsPendingApproval { get; set; }
    public string? LastAuthError { get; set; }
    public string? NodeShortDeviceId { get; set; }
    public VoiceService? VoiceServiceInstance { get; set; }
    public string? NodeFullDeviceId { get; set; }

    // Event for settings saved (App.xaml.cs subscribes)
    public event EventHandler? SettingsSaved;

    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    public HubWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Closed += (s, e) => IsClosed = true;

        this.SetWindowSize(900, 650);
        this.CenterOnScreen();
        this.SetIcon(IconHelper.GetStatusIconPath(ConnectionStatus.Connected));

        RootGrid.SizeChanged += OnRootGridSizeChanged;

        // Don't select a nav item here — Settings/GatewayClient aren't set yet.
        // ShowHub() in App.xaml.cs calls NavigateToDefault() after setting properties.
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minPane = 200;
        const double maxPane = 260;
        const double ratio = 0.25;

        double desired = e.NewSize.Width * ratio;
        NavView.OpenPaneLength = Math.Clamp(desired, minPane, maxPane);
    }

    private void OnTitleBarStatusTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        NavigateTo("connection");
    }

    /// <summary>
    /// Navigate to the default page. Call after setting Settings/GatewayClient.
    /// </summary>
    public void NavigateToDefault()
    {
        if (ContentFrame.Content == null)
        {
            // Connection is the landing page (Home was removed; legacy
            // "home"/"general" tags alias to "connection" in NavigateTo).
            NavigateTo("connection");
        }
    }

    /// <summary>
    /// Navigate to a specific page by tag name (e.g. "connection", "sessions", "channels").
    /// </summary>
    public void NavigateTo(string tag)
    {
        // Map legacy tags — Home page was retired in favor of the Lobby/Cockpit
        // layout on Connection. Any caller still using "home" or "general"
        // (deep links, persisted nav state, command palette) lands here.
        if (tag == "home" || tag == "general") tag = "connection";
        if (tag == "about") tag = "info";
        if (tag == "nodes") tag = "instances";
        // Map legacy agent-scoped workspace/cron tags
        if (tag == "cron") tag = $"agent:{_currentAgentId}:cron";
        if (tag == "workspace") tag = $"agent:{_currentAgentId}:workspace";

        // Search all nav items including nested
        if (FindAndSelectNavItem(NavView.MenuItems, tag)) return;
        if (FindAndSelectNavItem(NavView.FooterMenuItems, tag)) return;

        // Fallback: navigate directly
        if (tag.StartsWith("agent:")) { _currentAgentId = ParseAgentIdFromTag(tag); _cachedCommands = null; }
        var pageType = TagToPageType(tag);
        if (pageType != null)
        {
            ContentFrame.Navigate(pageType);
            InitializeCurrentPage();
        }
    }

    private bool FindAndSelectNavItem(IList<object> items, string tag)
    {
        foreach (var item in items)
        {
            if (item is NavigationViewItem navItem)
            {
                if (navItem.Tag as string == tag) { NavView.SelectedItem = navItem; return true; }
                if (navItem.MenuItems.Count > 0 && FindAndSelectNavItem(navItem.MenuItems, tag))
                {
                    navItem.IsExpanded = true;
                    return true;
                }
            }
        }
        return false;
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                CurrentStatus = status;
                _cachedCommands = null;
                UpdateTitleBarStatus(status);
                if (ContentFrame?.Content is ConnectionPage connectionPage)
                {
                    connectionPage.UpdateStatus(status);
                }
            });
        }
        catch { }
    }

    private void UpdateTitleBarStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        TitleStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        // Build status text with version when connected
        if (status == ConnectionStatus.Connected && LastGatewaySelf is { ServerVersion: { Length: > 0 } ver })
            TitleStatusText.Text = $"v{ver}";
        else
            TitleStatusText.Text = text;

        // Update role indicator dots
        var snapshot = ConnectionManager?.CurrentSnapshot;
        if (snapshot != null)
        {
            TitleOpDot.Fill = RoleDotBrush(snapshot.OperatorState);
            TitleNodeDot.Fill = RoleDotBrush(snapshot.NodeState);
        }
        else
        {
            var gray = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            TitleOpDot.Fill = gray;
            TitleNodeDot.Fill = gray;
        }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush RoleDotBrush(OpenClaw.Connection.RoleConnectionState state) => state switch
    {
        OpenClaw.Connection.RoleConnectionState.Connected =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
        OpenClaw.Connection.RoleConnectionState.Connecting =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        OpenClaw.Connection.RoleConnectionState.PairingRequired =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        OpenClaw.Connection.RoleConnectionState.Error or
        OpenClaw.Connection.RoleConnectionState.PairingRejected or
        OpenClaw.Connection.RoleConnectionState.RateLimited =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
    };

    public GatewaySelfInfo? LastGatewaySelf => AppModel?.GatewaySelf;

    public void UpdateGatewaySelf(GatewaySelfInfo self)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                UpdateTitleBarStatus(CurrentStatus);
                if (ContentFrame?.Content is AboutPage about)
                    about.RefreshGatewayInfo();
            });
        }
        catch { }
    }

    public void UpdateAgentsList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                // Rebuild nav sidebar agent items
                RebuildAgentNavItems(data);
            });
        }
        catch { }
    }

    private void RebuildAgentNavItems(System.Text.Json.JsonElement data)
    {
        if (!data.TryGetProperty("agents", out var agentsEl) ||
            agentsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        AgentsNavItem.MenuItems.Clear();

        foreach (var agent in agentsEl.EnumerateArray())
        {
            var id = agent.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = agent.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            var agentItem = new NavigationViewItem
            {
                Content = name ?? id,
                Tag = $"agent:{id}",
                Icon = new FontIcon { Glyph = "\uE99A" }
            };

            AgentsNavItem.MenuItems.Add(agentItem);
        }
    }

    /// <summary>Extract agent IDs from cached agents data.</summary>
    public List<string> GetAgentIds() => AppModel?.GetAgentIds() ?? new List<string> { "main" };

    public System.Text.Json.JsonElement? LastAgentsData => AppModel?.AgentsList;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag as string;
            if (tag?.StartsWith("agent:") == true)
            { _currentAgentId = ParseAgentIdFromTag(tag); _cachedCommands = null; }
            var pageType = TagToPageType(tag);
            if (pageType != null)
            {
                ContentFrame.Navigate(pageType);
                InitializeCurrentPage();
            }
        }
    }

    /// <summary>
    /// Persist the NavigationView's expanded/compact state on every toggle.
    /// Both PaneOpening and PaneClosing route here; we read the current
    /// state from the sender so we don't have to distinguish the two.
    /// </summary>
    private void OnNavPaneStateChanged(NavigationView sender, object args)
    {
        if (_settings == null) return;
        // PaneOpening fires BEFORE IsPaneOpen flips, PaneClosing fires
        // BEFORE it flips the other way. Use the event identity to know
        // the new state rather than reading IsPaneOpen.
        var newState = args is NavigationViewPaneClosingEventArgs ? false : true;
        if (_settings.HubNavPaneOpen == newState) return;
        _settings.HubNavPaneOpen = newState;
        try { _settings.Save(); } catch { /* swallow — don't block UI */ }
    }

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case ChatPage chat: chat.Initialize(); break;
            case SessionsPage sessions: sessions.Initialize(); break;
            case ConnectionPage connection: connection.Initialize(); break;
            case ChannelsPage channels: channels.Initialize(); break;
            case UsagePage usage: usage.Initialize(); break;
            case CronPage cron: cron.Initialize(); break;
            case SkillsPage skills: skills.Initialize(); break;
            case ConfigPage config:
                try { config.Initialize(); }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Error($"[HubWindow] ConfigPage seed failed: {ex}");
                }
                break;
            case InstancesPage instances: instances.Initialize(); break;
            case PermissionsPage permissions: permissions.Initialize(); break;
            case SandboxPage sandbox: sandbox.Initialize(); break;
            case VoiceSettingsPage voice: voice.Initialize(VoiceServiceInstance); break;
            case ActivityPage activity: activity.Initialize(); break;
            case AgentEventsPage agentEvents:
                agentEvents.ClearCentralCache = () => AppModel?.ClearAgentEvents();
                agentEvents.PopulateAgentFilter(this);
                var agentEventsTag = (NavView?.SelectedItem as NavigationViewItem)?.Tag as string;
                var eventsAgentFilter = agentEventsTag?.StartsWith("agent:") == true ? _currentAgentId : null;
                agentEvents.SetAgentFilter(eventsAgentFilter);
                break;
            case WorkspacePage workspace:
                workspace.AgentId = _currentAgentId;
                workspace.Initialize();
                break;
            case BindingsPage bindings: bindings.Initialize(); break;
            case SettingsPage settings: settings.Initialize(); break;
            case DebugPage debug: debug.Initialize(); break;
            case AboutPage about: about.Initialize(); break;
        }
    }

    public void SetActivityFilter(string? filter)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ActivityPage activity)
                activity.SetFilter(filter);
        });
    }

    private static Type? TagToPageType(string? tag) => tag switch
    {
        "chat" => typeof(ChatPage),
        "connection" => typeof(ConnectionPage),
        "channels" => typeof(ChannelsPage),
        "nodes" => typeof(InstancesPage),
        "instances" => typeof(InstancesPage),
        "config" => typeof(ConfigPage),
        "usage" => typeof(UsagePage),
        "bindings" => typeof(BindingsPage),
        "capabilities" => typeof(PermissionsPage),
        "voice" => typeof(VoiceSettingsPage),
        "permissions" => typeof(PermissionsPage),
        "sandbox" => typeof(SandboxPage),
        "activity" => typeof(ActivityPage),
        "settings" => typeof(SettingsPage),
        "debug" => typeof(DebugPage),
        "info" => typeof(AboutPage),
        // Legacy tags
        "home" => typeof(ConnectionPage),
        "general" => typeof(ConnectionPage),
        "conversations" => typeof(SessionsPage), // legacy redirect
        "sessions" => typeof(SessionsPage),
        "agentevents" => typeof(AgentEventsPage),
        "skills" => typeof(SkillsPage),
        "cron" => typeof(CronPage),
        "workspace" => typeof(WorkspacePage),
        "about" => typeof(AboutPage),
        // Agent-scoped pages
        _ when tag?.StartsWith("agent:") == true => ResolveAgentPageType(tag),
        _ => null
    };

    private static Type? ResolveAgentPageType(string tag)
    {
        var parts = tag.Split(':');
        // "agent:main" (2 parts) → workspace page for that agent
        if (parts.Length == 2) return typeof(WorkspacePage);
        // "agent:main:workspace" etc (3 parts)
        return parts[2] switch
        {
            "sessions" => typeof(SessionsPage),
            "agentevents" => typeof(AgentEventsPage),
            "skills" => typeof(SkillsPage),
            "cron" => typeof(CronPage),
            "workspace" => typeof(WorkspacePage),
            _ => null
        };
    }

    private static string ParseAgentIdFromTag(string? tag)
    {
        if (tag == null || !tag.StartsWith("agent:")) return "main";
        var parts = tag.Split(':');
        return parts.Length >= 2 ? parts[1] : "main";
    }

    // ── Command Search (Ctrl+E / Ctrl+K / Ctrl+F) — title bar AutoSuggestBox ──

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            global::Windows.System.VirtualKey.Control).HasFlag(
            global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && (e.Key == global::Windows.System.VirtualKey.E ||
                     e.Key == global::Windows.System.VirtualKey.K ||
                     e.Key == global::Windows.System.VirtualKey.F))
        {
            e.Handled = true;
            TitleSearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            TitleSearchBox.Text = "";
        }
    }

    private List<CommandItem>? _cachedCommands;

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _cachedCommands ??= BuildCommandList();
        var query = sender.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _cachedCommands.Take(8).ToList()
            : _cachedCommands.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10).ToList();
        sender.ItemsSource = filtered;
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is CommandItem cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
        else if (sender.ItemsSource is List<CommandItem> items && items.Count > 0)
        {
            // Enter pressed without selecting — execute first match
            var first = items[0];
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(first);
        }
    }

    internal List<CommandItem> BuildCommandList()
    {
        var agentId = _currentAgentId;
        var commands = new List<CommandItem>
        {
            // Navigation
            new() { Icon = "🔌", Title = "Go to Connection", Subtitle = "Gateway, node, saved gateways", Tag = "connection" },
            new() { Icon = "💬", Title = "Go to Chat", Subtitle = "Open chat", Tag = "chat" },
            new() { Icon = "🧠", Title = "Go to Sessions", Subtitle = "All sessions", Tag = "sessions" },
            new() { Icon = "🧠", Title = "Go to Agent Events", Subtitle = "Agent event log", Tag = "agentevents" },
            new() { Icon = "🧠", Title = "Go to Skills", Subtitle = "Registered skills", Tag = "skills" },
            new() { Icon = "🧠", Title = $"Go to Cron ({agentId})", Subtitle = "Scheduled tasks", Tag = $"agent:{agentId}:cron" },
            new() { Icon = "🧠", Title = $"Go to Workspace ({agentId})", Subtitle = "Workspace files", Tag = $"agent:{agentId}" },
            new() { Icon = "📡", Title = "Go to Channels", Subtitle = "Gateway channels", Tag = "channels" },
            new() { Icon = "📡", Title = "Go to Instances", Subtitle = "Gateway instances", Tag = "instances" },
            new() { Icon = "📡", Title = "Go to Config", Subtitle = "Gateway configuration", Tag = "config" },
            new() { Icon = "📡", Title = "Go to Usage", Subtitle = "Usage statistics", Tag = "usage" },
            new() { Icon = "📡", Title = "Go to Bindings", Subtitle = "Gateway bindings", Tag = "bindings" },
            new() { Icon = "🛡️", Title = "Go to Permissions", Subtitle = "Capabilities, exec policy & allowlists", Tag = "permissions" },
            new() { Icon = "🕐", Title = "Go to Activity", Subtitle = "Activity stream", Tag = "activity" },
            new() { Icon = "⚙️", Title = "Go to Settings", Subtitle = "Application settings", Tag = "settings" },
            new() { Icon = "🐛", Title = "Go to Debug", Subtitle = "Debug information", Tag = "debug" },
            new() { Icon = "ℹ️", Title = "Go to Info", Subtitle = "About this app", Tag = "info" },

            // Actions
            new() { Icon = "💬", Title = "Open Chat Window", Subtitle = "Open standalone chat", Tag = "chat" },
            new() { Icon = "🌐", Title = "Open Dashboard", Subtitle = "Open web dashboard", Execute = () => OpenDashboardAction?.Invoke(null) },
            new() { Icon = "📤", Title = "Quick Send", Subtitle = "Send a quick message", Execute = () => QuickSendAction?.Invoke() },
        };

        // Toggle commands
        if (Settings != null)
        {
            commands.Add(new CommandItem
            {
                Icon = "🔌", Title = "Toggle Node Mode",
                Subtitle = Settings.EnableNodeMode ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.EnableNodeMode = !Settings.EnableNodeMode; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "📷", Title = "Toggle Camera",
                Subtitle = Settings.NodeCameraEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeCameraEnabled = !Settings.NodeCameraEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🎨", Title = "Toggle Canvas",
                Subtitle = Settings.NodeCanvasEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeCanvasEnabled = !Settings.NodeCanvasEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🖥️", Title = "Toggle Screen Capture",
                Subtitle = Settings.NodeScreenEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeScreenEnabled = !Settings.NodeScreenEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
            commands.Add(new CommandItem
            {
                Icon = "🌐", Title = "Toggle Browser Control",
                Subtitle = Settings.NodeBrowserProxyEnabled ? "Currently ON" : "Currently OFF",
                Execute = () => { Settings.NodeBrowserProxyEnabled = !Settings.NodeBrowserProxyEnabled; Settings.Save(); RaiseSettingsSaved(); }
            });
        }

        // Dynamic session commands
        var sessions = AppModel?.Sessions;
        if (sessions != null)
        {
            foreach (var session in sessions)
            {
                var key = session.Key;
                commands.Add(new CommandItem
                {
                    Icon = "🧠", Title = $"Go to session: {key}",
                    Subtitle = "Open in dashboard",
                    Execute = () => OpenDashboardAction?.Invoke($"sessions/{key}")
                });
            }
        }

        return commands;
    }

    private void ExecuteCommand(CommandItem cmd)
    {
        if (cmd.Execute != null)
        {
            cmd.Execute();
            return;
        }

        if (!string.IsNullOrEmpty(cmd.Tag))
        {
            NavigateTo(cmd.Tag);
        }
    }

    /// <summary>Action to open the QuickSend dialog, set by App.xaml.cs.</summary>
    public Action? QuickSendAction { get; set; }
}

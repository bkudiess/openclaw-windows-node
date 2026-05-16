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

    // Gateway data — read-through from AppModel (single source of truth)
    public SessionInfo[]? LastSessions => AppModel?.Sessions;
    public ChannelHealth[]? LastChannels => AppModel?.Channels;
    public GatewayUsageInfo? LastUsage => AppModel?.Usage;
    public GatewayCostUsageInfo? LastUsageCost => AppModel?.UsageCost;
    public GatewayUsageStatusInfo? LastUsageStatus => AppModel?.UsageStatus;
    public GatewayNodeInfo[]? LastNodes => AppModel?.Nodes;

    public System.Text.Json.JsonElement? LastConfig => AppModel?.Config;
    public System.Text.Json.JsonElement? LastConfigSchema => AppModel?.ConfigSchema;
    public System.Text.Json.JsonElement? LastSkillsData => AppModel?.SkillsData;
    public string? LastSkillsAgentId => AppModel?.SkillsAgentId;
    public System.Text.Json.JsonElement? LastAgentFilesList => AppModel?.AgentFilesList;
    public string? LastAgentFilesListAgentId => AppModel?.AgentFilesListAgentId;
    private string? _pendingAgentFilesListAgentId;

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

    public void UpdateSessions(SessionInfo[] sessions)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is SessionsPage sp) sp.UpdateSessions(sessions);
            else if (ContentFrame?.Content is ConnectionPage cp) cp.OnGlanceDataChanged();
        });
    }

    public void UpdateChannelHealth(ChannelHealth[] channels)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ChannelsPage cp) cp.UpdateChannels(channels);
            else if (ContentFrame?.Content is ConnectionPage connection) connection.OnGlanceDataChanged();
        });
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsage(usage);
        });
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsageCost(cost);
            else if (ContentFrame?.Content is ConnectionPage cp) cp.OnGlanceDataChanged();
        });
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is UsagePage up) up.UpdateUsageStatus(status);
        });
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is InstancesPage ip) ip.UpdateNodes(nodes);
        });
    }

    // Cron data — read-through from AppModel (no private cache needed)

    public void UpdateCronList(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is CronPage cp) cp.UpdateFromGateway(data);
            });
        }
        catch { }
    }

    public void UpdateCronStatus(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is CronPage cp) cp.UpdateFromGateway(data);
            });
        }
        catch { }
    }

    public void UpdateCronRuns(System.Text.Json.JsonElement data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is CronPage cp) cp.UpdateCronRuns(data);
            });
        }
        catch { }
    }

    public void SeedCronData(CronPage page)
    {
        if (AppModel?.CronList.HasValue == true) page.UpdateFromGateway(AppModel.CronList.Value);
        if (AppModel?.CronStatus.HasValue == true) page.UpdateFromGateway(AppModel.CronStatus.Value);
    }

    public void UpdateConfig(System.Text.Json.JsonElement config)
    {
        var snapshot = config.Clone();
        if (IsClosed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is ConfigPage cp) cp.UpdateConfig(snapshot);
            else if (ContentFrame?.Content is BindingsPage bp) bp.UpdateConfig(snapshot);
        });
    }

    public void UpdateConfigSchema(System.Text.Json.JsonElement schema)
    {
        var snapshot = schema.Clone();
        if (IsClosed) return;
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is ConfigPage cp) cp.UpdateConfigSchema(snapshot);
            });
        }
        catch { }
    }

    public void UpdateSkillsStatus(System.Text.Json.JsonElement data)
    {
        try
        {
            var snapshot = data.Clone();
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is SkillsPage sp)
                {
                    sp.UpdateFromGateway(snapshot);
                }
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

    public void RecordAgentFilesListRequest(string agentId)
    {
        _pendingAgentFilesListAgentId = string.IsNullOrWhiteSpace(agentId) ? "main" : agentId;
    }

    public void UpdateAgentFilesList(System.Text.Json.JsonElement data)
    {
        try
        {
            var snapshot = data.Clone();
            var responseAgentId = _pendingAgentFilesListAgentId ?? _currentAgentId;
            _pendingAgentFilesListAgentId = null;
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is WorkspacePage wp &&
                    string.Equals(wp.CurrentAgentId, responseAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    wp.UpdateAgentFilesList(snapshot);
                }
            });
        }
        catch { }
    }

    public void UpdateAgentFileContent(System.Text.Json.JsonElement data)
    {
        try
        {
            var snapshot = data.Clone();
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is WorkspacePage wp) wp.UpdateAgentFileContent(snapshot);
            });
        }
        catch { }
    }

    public System.Collections.Generic.IReadOnlyList<AgentEventInfo> LastAgentEvents => AppModel?.AgentEvents ?? (System.Collections.Generic.IReadOnlyList<AgentEventInfo>)Array.Empty<AgentEventInfo>();

    /// <summary>Called by App to also clear its own agent event cache when Clear is invoked.</summary>
    public Action? ClearAppAgentEventsCache { get; set; }

    public void ClearAgentEvents()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            AppModel?.ClearAgentEvents();
            ClearAppAgentEventsCache?.Invoke();
        });
    }

    /// <summary>Seed the currently visible AgentEventsPage from AppModel.</summary>
    public void SeedAgentEvents()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ContentFrame?.Content is AgentEventsPage page)
            {
                var events = AppModel?.AgentEvents ?? (System.Collections.Generic.IReadOnlyList<AgentEventInfo>)Array.Empty<AgentEventInfo>();
                foreach (var evt in events)
                    page.AddEvent(evt);
            }
        });
    }

    public void UpdateAgentEvent(AgentEventInfo evt)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is AgentEventsPage agentEvents) agentEvents.AddEvent(evt);
            });
        }
        catch { }
    }

    // Pairing data — read-through from AppModel
    public PairingListInfo? LastNodePairList => AppModel?.NodePairList;
    public DevicePairingListInfo? LastDevicePairList => AppModel?.DevicePairList;
    public ModelsListInfo? LastModelsList => AppModel?.ModelsList;

    public void UpdateNodePairList(PairingListInfo data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                // Operator/node pairing approval moved from NodesPage to ConnectionPage
                // (single home for all pairing approvals).
                if (ContentFrame?.Content is ConnectionPage cp) cp.UpdatePairingRequests(data);
            });
        }
        catch { }
    }

    public void UpdateDevicePairList(DevicePairingListInfo data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is ConnectionPage cp) cp.UpdateDevicePairingRequests(data);
            });
        }
        catch { }
    }

    public void UpdateModelsList(ModelsListInfo data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is SessionsPage sp) sp.UpdateModelsList(data);
            });
        }
        catch { }
    }

    public PresenceEntry[]? LastPresence => AppModel?.Presence;
    public System.Text.Json.JsonElement? LastAgentsData => AppModel?.AgentsList;

    public void UpdatePresence(PresenceEntry[] data)
    {
        try
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsClosed) return;
                if (ContentFrame?.Content is InstancesPage ip) ip.UpdatePresence(data);
            });
        }
        catch { }
    }

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
            case ChatPage chat: chat.Initialize(this); break;
            case SessionsPage sessions:
                sessions.Initialize(this);
                if (LastModelsList != null) sessions.UpdateModelsList(LastModelsList);
                break;
            case ConnectionPage connection:
                connection.Initialize(this);
                if (LastNodePairList != null) connection.UpdatePairingRequests(LastNodePairList);
                if (LastDevicePairList != null) connection.UpdateDevicePairingRequests(LastDevicePairList);
                break;
            case ChannelsPage channels: channels.Initialize(this); break;
            case UsagePage usage: usage.Initialize(this); break;
            case CronPage cron: cron.Initialize(this); SeedCronData(cron); break;
            case SkillsPage skills:
                skills.Initialize(this);
                if (LastSkillsData.HasValue && LastSkillsAgentId == skills.CurrentAgentId)
                    skills.UpdateFromGateway(LastSkillsData.Value);
                break;
            case ConfigPage config:
                try
                {
                    config.Initialize(this);
                    if (LastConfigSchema.HasValue) config.UpdateConfigSchema(LastConfigSchema.Value);
                    if (LastConfig.HasValue) config.UpdateConfig(LastConfig.Value);
                }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Error($"[HubWindow] ConfigPage seed failed: {ex}");
                }
                break;
            case InstancesPage instances:
                // Initialize already seeds _lastNodes/_lastPresence from
                // hub.LastNodes/hub.LastPresence and triggers a single
                // Rerender. Calling UpdateNodes/UpdatePresence here would
                // cause two additional dispatcher-queued rebuilds on every
                // page entry — visible flicker on lists with many cards.
                instances.Initialize(this);
                break;
            case PermissionsPage permissions: permissions.Initialize(this); break;
            case SandboxPage sandbox: sandbox.Initialize(this); break;
            case VoiceSettingsPage voice: voice.Initialize(this, VoiceServiceInstance); break;
            case ActivityPage activity: activity.Initialize(this); break;
            case AgentEventsPage agentEvents:
                agentEvents.ClearCentralCache = ClearAgentEvents;
                agentEvents.PopulateAgentFilter(this);
                // When navigated via top-level nav (tag "agentevents"), show all agents
                var agentEventsTag = (NavView?.SelectedItem as NavigationViewItem)?.Tag as string;
                var eventsAgentFilter = agentEventsTag?.StartsWith("agent:") == true ? _currentAgentId : null;
                agentEvents.SetAgentFilter(eventsAgentFilter);
                if (agentEvents.EventCount == 0 && LastAgentEvents != null)
                {
                    for (int i = LastAgentEvents.Count - 1; i >= 0; i--)
                        agentEvents.AddEvent(LastAgentEvents[i]);
                }
                break;
            case WorkspacePage workspace:
                workspace.Initialize(this);
                if (LastAgentFilesList.HasValue &&
                    string.Equals(LastAgentFilesListAgentId, workspace.CurrentAgentId, StringComparison.OrdinalIgnoreCase))
                {
                    workspace.UpdateAgentFilesList(LastAgentFilesList.Value);
                }
                break;
            case BindingsPage bindings:
                bindings.Initialize(this);
                if (LastConfig.HasValue) bindings.UpdateConfig(LastConfig.Value);
                break;
            case SettingsPage settings: settings.Initialize(this); break;
            case DebugPage debug: debug.Initialize(this); break;
            case AboutPage about: about.Initialize(this); break;
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
        if (LastSessions != null)
        {
            foreach (var session in LastSessions)
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

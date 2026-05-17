using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class BindingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    public BindingsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;
        // Use cached config if available
        if (_appState?.Config.HasValue == true)
            ParseBindings(_appState.Config.Value);
        // Request fresh config
        if (CurrentApp.GatewayClient != null)
            _ = CurrentApp.GatewayClient.RequestConfigAsync();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Config):
                if (_appState!.Config.HasValue) UpdateConfig(_appState.Config.Value);
                break;
        }
    }

    public void UpdateConfig(JsonElement config)
    {
        ParseBindings(config);
    }

    private void ParseBindings(JsonElement config)
    {
        var bindings = new List<BindingViewModel>();

        try
        {
            if (config.ValueKind == JsonValueKind.Object &&
                config.TryGetProperty("bindings", out var bindingsEl) &&
                bindingsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bindingsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var vm = new BindingViewModel();

                    if (item.TryGetProperty("channel", out var ch))
                        vm.Channel = ch.GetString() ?? "";
                    if (item.TryGetProperty("accountId", out var acc))
                        vm.AccountId = acc.GetString() ?? "*";
                    if (item.TryGetProperty("agentId", out var agent))
                        vm.AgentId = agent.GetString() ?? "main";
                    if (item.TryGetProperty("peer", out var peer))
                        vm.Peer = peer.GetString();
                    if (item.TryGetProperty("priority", out var prio) && prio.TryGetInt32(out var prioVal))
                        vm.Priority = prioVal;

                    bindings.Add(vm);
                }
            }
        }
        catch { }

        if (bindings.Count == 0)
        {
            SingleAgentInfoBar.IsOpen = true;
            BindingsList.ItemsSource = null;
        }
        else
        {
            SingleAgentInfoBar.IsOpen = false;
            BindingsList.ItemsSource = bindings;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestConfigAsync();
        }
    }

    private class BindingViewModel
    {
        public string Channel { get; set; } = "";
        public string AccountId { get; set; } = "*";
        public string? Peer { get; set; }
        public string AgentId { get; set; } = "main";
        public int? Priority { get; set; }

        public string ChannelIcon => Channel switch
        {
            "whatsapp" => "📱",
            "telegram" => "✈️",
            "discord" => "🎮",
            "slack" => "💼",
            "signal" => "🔒",
            _ => "📡"
        };

        public string PeerDisplay => string.IsNullOrEmpty(Peer) ? "All" : Peer;
        public string RouteDisplay => $"{ChannelIcon} {Channel} ({AccountId}) → {AgentId}";
    }
}

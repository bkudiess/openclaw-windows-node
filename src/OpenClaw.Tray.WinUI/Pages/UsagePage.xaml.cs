using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class UsagePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;
    // Default matches the XAML-selected Period7DaysItem (IsSelected="True").
    private int _currentPeriodDays = 7;

    public UsagePage()
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
        ConnectionWarning.Visibility = CurrentApp.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (CurrentApp.GatewayClient != null)
        {
            // Apply cached data immediately, then request fresh.
            if (_appState?.Usage != null) UpdateUsage(_appState.Usage);
            // Only apply cached cost data when its period matches the current
            // selection — otherwise the daily list briefly shows e.g. 30-day
            // data while the selector reads "7 Days".
            if (_appState?.UsageCost != null && _appState.UsageCost.Days == _currentPeriodDays)
            {
                UpdateUsageCost(_appState.UsageCost);
            }
            if (_appState?.UsageStatus != null) UpdateUsageStatus(_appState.UsageStatus);
            _ = CurrentApp.GatewayClient.RequestUsageAsync();
            _ = CurrentApp.GatewayClient.RequestUsageCostAsync(_currentPeriodDays);
            _ = CurrentApp.GatewayClient.RequestUsageStatusAsync();
        }
        else
        {
            // Not connected — nothing to load, hide the loading skeleton.
            ProviderLoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Usage):
                if (_appState!.Usage != null) UpdateUsage(_appState.Usage);
                break;
            case nameof(AppState.UsageCost):
                if (_appState!.UsageCost != null) UpdateUsageCost(_appState.UsageCost);
                break;
            case nameof(AppState.UsageStatus):
                if (_appState!.UsageStatus != null) UpdateUsageStatus(_appState.UsageStatus);
                break;
        }
    }

    public void UpdateUsage(GatewayUsageInfo usage)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            RequestCountText.Text = usage.RequestCount.ToString();
            // Note: TotalCostText and TokenCountText are owned by UpdateUsageCost
            // (period-scoped), not UpdateUsage (all-time). Writing them from both
            // sources caused a race where the last response to arrive won — see
            // Hanselman review #1 (HIGH).
        });
    }

    public void UpdateUsageCost(GatewayCostUsageInfo cost)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            TotalCostText.Text = $"${cost.Totals.TotalCost:F2}";
            TokenCountText.Text = FormatLargeNumber(cost.Totals.TotalTokens);

            DailyListView.ItemsSource = cost.Daily.Select(d => new DailyRow
            {
                Date = d.Date,
                Cost = $"${d.TotalCost:F2}",
            }).ToList();
        });
    }

    public void UpdateUsageStatus(GatewayUsageStatusInfo status)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ProviderCountText.Text = status.Providers.Count.ToString();
            ProviderListView.ItemsSource = status.Providers.Select(p => new ProviderRow
            {
                Name = p.DisplayName,
                Plan = p.Plan ?? "",
                Usage = p.Windows.Count > 0 ? $"{p.Windows[0].UsedPercent:F0}% used" : "",
                Status = p.Error ?? "",
            }).ToList();

            bool hasProviders = status.Providers.Count > 0;
            ProviderLoadingPanel.Visibility = Visibility.Collapsed;
            ProviderListView.Visibility = hasProviders ? Visibility.Visible : Visibility.Collapsed;
            ProviderEmptyText.Visibility = hasProviders ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void OnPeriodSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var days = ReferenceEquals(sender.SelectedItem, Period30DaysItem) ? 30 : 7;
        SelectPeriod(days);
    }

    private void SelectPeriod(int days)
    {
        if (days == _currentPeriodDays) return;
        _currentPeriodDays = days;

        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestUsageCostAsync(days);
        }
    }

    private static string FormatLargeNumber(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K";
        return n.ToString();
    }

    private class ProviderRow
    {
        public string Name { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Usage { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private class DailyRow
    {
        public string Date { get; set; } = "";
        public string Cost { get; set; } = "";
    }
}

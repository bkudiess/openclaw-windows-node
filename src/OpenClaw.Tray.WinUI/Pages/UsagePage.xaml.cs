using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class UsagePage : Page
{
    private HubWindow? _hub;
    // Default matches the XAML-selected Period7DaysItem (IsSelected="True").
    private int _currentPeriodDays = 7;

    public UsagePage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        ConnectionWarning.Visibility = hub.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (hub.GatewayClient != null)
        {
            // Apply cached data immediately, then request fresh.
            if (hub.LastUsage != null) UpdateUsage(hub.LastUsage);
            // Only apply cached cost data when its period matches the current
            // selection — otherwise the daily list briefly shows e.g. 30-day
            // data while the selector reads "7 Days".
            if (hub.LastUsageCost != null && hub.LastUsageCost.Days == _currentPeriodDays)
            {
                UpdateUsageCost(hub.LastUsageCost);
            }
            if (hub.LastUsageStatus != null) UpdateUsageStatus(hub.LastUsageStatus);
            _ = hub.GatewayClient.RequestUsageAsync();
            _ = hub.GatewayClient.RequestUsageCostAsync(_currentPeriodDays);
            _ = hub.GatewayClient.RequestUsageStatusAsync();
        }
        else
        {
            // Not connected — nothing to load, hide the loading skeleton.
            ProviderLoadingPanel.Visibility = Visibility.Collapsed;
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

        if (_hub?.GatewayClient != null)
        {
            _ = _hub.GatewayClient.RequestUsageCostAsync(days);
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

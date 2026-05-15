using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Windows.UI;

namespace OpenClawTray.Pages;

public sealed partial class SkillsPage : Page
{
    private HubWindow? _hub;
    private IReadOnlyList<SkillStatusRow> _allRows = Array.Empty<SkillStatusRow>();
    private SkillsFilter _filter = SkillsFilter.All;

    public string? CurrentAgentId => GetSelectedAgentId();

    public SkillsPage()
    {
        InitializeComponent();
        StatusFilterCombo.SelectionChanged += OnStatusFilterChanged;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        PopulateAgentFilter(hub);
        if (hub.GatewayClient != null)
        {
            _ = hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
        }
    }

    private void PopulateAgentFilter(HubWindow hub)
    {
        AgentFilterCombo.SelectionChanged -= OnAgentFilterChanged;
        AgentFilterCombo.Items.Clear();
        AgentFilterCombo.Items.Add(new ComboBoxItem { Content = "All Agents", Tag = "" });
        foreach (var id in hub.GetAgentIds())
            AgentFilterCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        AgentFilterCombo.SelectedIndex = 0;
        AgentFilterCombo.SelectionChanged += OnAgentFilterChanged;
    }

    private string? GetSelectedAgentId()
    {
        if (AgentFilterCombo.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string;
            return string.IsNullOrEmpty(tag) ? null : tag;
        }
        return null;
    }

    private void OnAgentFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var client = _hub?.GatewayClient;
        if (client != null)
            _ = client.RequestSkillsStatusAsync(GetSelectedAgentId());
    }

    private void OnStatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusFilterCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<SkillsFilter>(tag, out var parsed))
        {
            _filter = parsed;
            RebuildCards();
        }
    }

    public void UpdateFromGateway(JsonElement data)
    {
        OpenClawTray.Services.Logger.Info("[SkillsPage] Received gateway skills data");
        var rows = SkillStatusPresenter.Parse(data);
        DispatcherQueue?.TryEnqueue(() =>
        {
            _allRows = rows;
            RebuildCards();
        });
    }

    // ---- Card rendering -------------------------------------------------------------------

    private bool _readyExpanded = true;
    private bool _disabledExpanded = true;

    private void RebuildCards()
    {
        var filtered = SkillStatusPresenter.Filter(_allRows, _filter);
        var byState = filtered.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.ToList());

        var readyRows        = byState.GetValueOrDefault(SkillRowState.Ready)        ?? new();
        var needsInstallRows = byState.GetValueOrDefault(SkillRowState.NeedsInstall) ?? new();
        var needsEnvRows     = byState.GetValueOrDefault(SkillRowState.NeedsEnv)     ?? new();
        var needsSetupRows   = byState.GetValueOrDefault(SkillRowState.NeedsSetup)   ?? new();
        var disabledRows     = byState.GetValueOrDefault(SkillRowState.Disabled)     ?? new();

        // "Needs Setup" in the UI groups all unready-but-not-admin-disabled states together,
        // mirroring how Mac's SkillsFilter.needsSetup is computed.
        var setupGroup = needsInstallRows.Concat(needsEnvRows).Concat(needsSetupRows)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EnabledPanel.Children.Clear();
        DisabledPanel.Children.Clear();

        foreach (var r in readyRows)    EnabledPanel.Children.Add(BuildCard(r));
        foreach (var r in setupGroup)   EnabledPanel.Children.Add(BuildCard(r));
        foreach (var r in disabledRows) DisabledPanel.Children.Add(BuildCard(r));

        EnabledHeaderText.Text = setupGroup.Count > 0
            ? $"Ready ({readyRows.Count}) · Needs setup ({setupGroup.Count})"
            : $"Ready ({readyRows.Count})";
        DisabledHeaderText.Text = $"Disabled ({disabledRows.Count})";
        DisabledHeaderBtn.Visibility = disabledRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisabledPanel.Visibility = _disabledExpanded ? Visibility.Visible : Visibility.Collapsed;
        EnabledPanel.Visibility = _readyExpanded ? Visibility.Visible : Visibility.Collapsed;

        var total = _allRows.Count;
        var shown = filtered.Count;
        CountText.Text = total > 0
            ? (shown == total ? $"({readyRows.Count}/{total} ready)" : $"({shown}/{total} shown)")
            : "";

        if (total > 0)
        {
            SkillsGroups.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            SkillsGroups.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private Grid BuildCard(SkillStatusRow r)
    {
        var card = new Grid
        {
            Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(0, 2, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var opacity = (r.State == SkillRowState.Disabled) ? 0.5 : 1.0;

        // Row 0 col 0: name + source + state badges
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = opacity };
        titleRow.Children.Add(new TextBlock
        {
            Text = r.DisplayName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(BuildSourceBadge(r));
        titleRow.Children.Add(BuildStateBadge(r));
        Grid.SetRow(titleRow, 0);
        Grid.SetColumn(titleRow, 0);
        card.Children.Add(titleRow);

        // Row 0 col 1: trailing actions (install / set-env / toggle)
        var trailing = BuildTrailingActions(r);
        Grid.SetRow(trailing, 0);
        Grid.SetColumn(trailing, 1);
        Grid.SetRowSpan(trailing, 2);
        card.Children.Add(trailing);

        // Row 1: description
        if (!string.IsNullOrEmpty(r.Description))
        {
            var desc = new TextBlock
            {
                Text = r.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Opacity = opacity,
            };
            Grid.SetRow(desc, 1);
            Grid.SetColumn(desc, 0);
            card.Children.Add(desc);
        }

        // Row 2: missing summary (only when not directly addressed by a trailing action)
        var summary = BuildMissingSummary(r);
        if (summary != null)
        {
            Grid.SetRow(summary, 2);
            Grid.SetColumn(summary, 0);
            Grid.SetColumnSpan(summary, 2);
            card.Children.Add(summary);
        }

        // Row 3: config checks
        if (r.ConfigChecks.Count > 0)
        {
            var checks = BuildConfigChecks(r);
            Grid.SetRow(checks, 3);
            Grid.SetColumn(checks, 0);
            Grid.SetColumnSpan(checks, 2);
            card.Children.Add(checks);
        }

        return card;
    }

    private static Border BuildSourceBadge(SkillStatusRow r)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = r.SourceLabel,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static Border BuildStateBadge(SkillStatusRow r)
    {
        var (label, fg, bg) = r.State switch
        {
            SkillRowState.Ready        => ("ready", Colors.LimeGreen, Color.FromArgb(40, 76, 175, 80)),
            SkillRowState.NeedsInstall => ("needs install", Color.FromArgb(255, 230, 168, 23), Color.FromArgb(40, 230, 168, 23)),
            SkillRowState.NeedsEnv     => ("needs key", Color.FromArgb(255, 230, 168, 23), Color.FromArgb(40, 230, 168, 23)),
            SkillRowState.NeedsSetup   => ("needs setup", Color.FromArgb(255, 230, 168, 23), Color.FromArgb(40, 230, 168, 23)),
            SkillRowState.Disabled     => ("disabled", Color.FromArgb(255, 170, 170, 170), Color.FromArgb(40, 170, 170, 170)),
            _ => ("unknown", Colors.Gray, Color.FromArgb(40, 128, 128, 128)),
        };
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(bg),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private StackPanel BuildTrailingActions(SkillStatusRow r)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        switch (r.State)
        {
            case SkillRowState.NeedsInstall:
                foreach (var opt in r.InstallOptionsForMissingBins)
                {
                    panel.Children.Add(BuildInstallButton(r, opt));
                }
                break;

            case SkillRowState.NeedsEnv:
                foreach (var envKey in r.MissingEnv)
                {
                    panel.Children.Add(BuildEnvButton(r, envKey));
                }
                break;

            case SkillRowState.Ready:
            case SkillRowState.Disabled:
                panel.Children.Add(BuildToggleButton(r));
                break;

            case SkillRowState.NeedsSetup:
                // No actionable button — diagnostic-only state (e.g. missing bin with no install
                // recipe). The missing-summary row below explains what's wrong.
                break;
        }

        return panel;
    }

    private Button BuildInstallButton(SkillStatusRow r, SkillInstallOption opt)
    {
        var label = string.IsNullOrEmpty(opt.Label) ? $"Install ({opt.Kind})" : opt.Label;
        var btn = new Button
        {
            Content = label,
            Tag = (r.Name, opt.Id),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        ToolTipService.SetToolTip(btn, $"Run {opt.Kind} install for {string.Join(", ", opt.Bins)}");
        btn.Click += async (s, e) =>
        {
            if (_hub?.GatewayClient == null) return;
            btn.IsEnabled = false;
            btn.Content = "Installing…";
            var ok = await _hub.GatewayClient.InstallSkillAsync(r.Name, opt.Id);
            btn.Content = label;
            btn.IsEnabled = true;
            if (ok)
            {
                _ = _hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
            }
        };
        return btn;
    }

    private Button BuildEnvButton(SkillStatusRow r, string envKey)
    {
        var isPrimary = !string.IsNullOrEmpty(r.PrimaryEnv) &&
                        string.Equals(r.PrimaryEnv, envKey, StringComparison.Ordinal);
        var label = isPrimary ? "Set API Key" : $"Set {envKey}";
        var btn = new Button { Content = label };
        btn.Click += async (s, e) =>
        {
            if (_hub?.GatewayClient == null) return;
            var dialog = new Dialogs.SkillEnvDialog(r.Name, envKey, r.Homepage)
            {
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            var value = dialog.EnteredValue;
            if (string.IsNullOrEmpty(value)) return;

            btn.IsEnabled = false;
            var ok = isPrimary
                ? await _hub.GatewayClient.SetSkillApiKeyAsync(r.SkillKey, value)
                : await _hub.GatewayClient.SetSkillEnvAsync(
                    r.SkillKey,
                    new Dictionary<string, string> { [envKey] = value });
            btn.IsEnabled = true;
            if (ok)
            {
                _ = _hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
            }
        };
        return btn;
    }

    private Button BuildToggleButton(SkillStatusRow r)
    {
        var isEnabled = !r.Disabled;
        var btn = new Button
        {
            Tag = r.SkillKey,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
        };
        ToolTipService.SetToolTip(btn, isEnabled ? "Disable" : "Enable");
        btn.Content = new FontIcon { Glyph = isEnabled ? "\uE769" : "\uE768", FontSize = 12 };
        btn.Click += async (s, e) =>
        {
            if (_hub?.GatewayClient == null) return;
            btn.IsEnabled = false;
            var ok = await _hub.GatewayClient.SetSkillEnabledAsync(r.SkillKey, !isEnabled);
            btn.IsEnabled = true;
            if (ok)
            {
                _ = _hub.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
            }
        };
        return btn;
    }

    private static StackPanel? BuildMissingSummary(SkillStatusRow r)
    {
        // For NeedsInstall / NeedsEnv the trailing action already names what's missing — don't
        // duplicate. Only show the diagnostic block when no actionable button covered the gap
        // or when missing-config is present (no action exists for that yet).
        var showBins = r.MissingBins.Count > 0 && r.State == SkillRowState.NeedsSetup;
        var showEnv  = r.MissingEnv.Count > 0 && r.State != SkillRowState.NeedsEnv;
        var showCfg  = r.MissingConfig.Count > 0;
        if (!showBins && !showEnv && !showCfg) return null;

        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        if (showBins)
        {
            panel.Children.Add(BuildCaption($"Missing binaries: {string.Join(", ", r.MissingBins)}"));
        }
        if (showEnv)
        {
            panel.Children.Add(BuildCaption($"Missing env: {string.Join(", ", r.MissingEnv)}"));
        }
        if (showCfg)
        {
            panel.Children.Add(BuildCaption($"Requires config: {string.Join(", ", r.MissingConfig)}"));
        }
        return panel;
    }

    private static TextBlock BuildCaption(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
    }

    private static StackPanel BuildConfigChecks(SkillStatusRow r)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var check in r.ConfigChecks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new FontIcon
            {
                Glyph = check.Satisfied ? "\uE73E" : "\uE711",
                FontSize = 12,
                Foreground = new SolidColorBrush(check.Satisfied ? Colors.LimeGreen : Color.FromArgb(255, 230, 168, 23)),
            });
            row.Children.Add(new TextBlock
            {
                Text = check.Path,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            if (!string.IsNullOrEmpty(check.ValueDisplay))
            {
                row.Children.Add(new TextBlock
                {
                    Text = check.ValueDisplay,
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
            }
            panel.Children.Add(row);
        }
        return panel;
    }
}

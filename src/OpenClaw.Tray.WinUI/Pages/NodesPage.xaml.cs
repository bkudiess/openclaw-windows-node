using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinColor = global::Windows.UI.Color;

namespace OpenClawTray.Pages;

public sealed partial class NodesPage : Page
{
    private HubWindow? _hub;
    // Page-wide guard for ContentDialog reentrancy. WinUI 3 only permits one
    // ContentDialog per XamlRoot at a time, so a per-node guard is not enough
    // (Rename on node A and Forget on node B in quick succession would
    // otherwise throw inside the second ShowAsync). Match the convention used
    // by SandboxPage's _confirmDialogOpen field.
    private bool _dialogOpen;

    public NodesPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        ConnectionWarning.Visibility = hub.GatewayClient != null ? Visibility.Collapsed : Visibility.Visible;
        if (hub.GatewayClient != null)
        {
            // Apply cached data immediately, then request fresh
            if (hub.LastNodes != null)
                UpdateNodes(hub.LastNodes);
            else
                NodesList.Children.Clear();
            _ = hub.GatewayClient.RequestNodesAsync();
        }
    }

    public void UpdateNodes(GatewayNodeInfo[] nodes)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (nodes.Length == 0)
            {
                NodesList.Children.Clear();
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            // Render straight from the gateway model: Capabilities, Commands
            // and Permissions are already populated by ParseNodeList and the
            // card layout consumes them as-is.
            RenderNodes(nodes);
        });
    }

    private void RenderNodes(IReadOnlyList<GatewayNodeInfo> nodes)
    {
        NodesList.Children.Clear();
        if (nodes.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;

        foreach (var node in nodes)
        {
            NodesList.Children.Add(BuildNodeCard(node));
        }
    }

    private Border BuildNodeCard(GatewayNodeInfo node)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        var expander = new Expander
        {
            IsExpanded = node.IsOnline,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = BuildCardHeader(node),
            Content = BuildCardDetails(node),
        };

        card.Child = expander;
        return card;
    }

    private static Grid BuildCardHeader(GatewayNodeInfo node)
    {
        // Header is purely informational: dot · name · platform badge ·
        // status caption. Actions live in the body footer (Win11 Settings
        // pattern: see Settings → Accounts → Email & accounts → Manage /
        // Remove buttons at the bottom).
        //
        // Use a Grid (not a horizontal StackPanel) so a long display name
        // ellipsizes at the available width instead of pushing the platform
        // badge and caption off the right edge.
        var header = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 10,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // dot
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name (ellipsizes)
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // platform badge
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // status caption

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = node.IsOnline ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Gray),
        };
        Grid.SetColumn(dot, 0);
        header.Children.Add(dot);

        var nameLabel = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName;
        var nameText = new TextBlock
        {
            Text = nameLabel,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        ToolTipService.SetToolTip(nameText, nameLabel);
        Grid.SetColumn(nameText, 1);
        header.Children.Add(nameText);

        var platform = node.Platform ?? "unknown";
        var platformBadge = new Border
        {
            Background = new SolidColorBrush(GetPlatformColor(platform)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        platformBadge.Child = new TextBlock
        {
            Text = platform,
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.White),
        };
        Grid.SetColumn(platformBadge, 2);
        header.Children.Add(platformBadge);

        var detailText = new TextBlock
        {
            Text = node.DetailText,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detailText, 3);
        header.Children.Add(detailText);

        return header;
    }

    private StackPanel BuildCardDetails(GatewayNodeInfo node)
    {
        var stack = new StackPanel { Spacing = 10 };

        // Short identity row
        stack.Children.Add(BuildIdentityRow(node));

        // Optional one-line fact rows. Each helper returns null when the
        // backing data is empty so we don't render empty labels.
        AppendIfNotNull(stack, BuildVersionRow(node));
        AppendIfNotNull(stack, BuildHardwareRow(node));
        AppendIfNotNull(stack, BuildNetworkRow(node));
        AppendIfNotNull(stack, BuildTimestampsRow(node));
        AppendIfNotNull(stack, BuildCapabilitiesSection(node));
        AppendIfNotNull(stack, BuildCommandsSection(node));
        AppendIfNotNull(stack, BuildPermissionsSection(node));
        AppendIfNotNull(stack, BuildPathEnvSection(node));

        // Actions footer — separator line then right-aligned Rename/Forget.
        // This mirrors how Win11 Settings places "Manage" / "Remove" actions
        // at the bottom of an account or device card: they're visually
        // separated from the informational content so destructive actions
        // are deliberate, not accidental.
        stack.Children.Add(BuildActionFooter(node));

        return stack;
    }

    private static void AppendIfNotNull(StackPanel stack, UIElement? element)
    {
        if (element != null) stack.Children.Add(element);
    }

    private StackPanel BuildActionFooter(GatewayNodeInfo node)
    {
        var clientAvailable = _hub?.GatewayClient != null;

        var footer = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // Subtle 1px divider above the actions. Uses the same stroke colour
        // the card itself uses for its border so the line feels native.
        footer.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var renameBtn = new Button
        {
            Content = LocalizationHelper.GetString("NodesPage_Action_Rename"),
            IsEnabled = clientAvailable,
            MinWidth = 96,
        };
        renameBtn.Click += async (s, e) =>
        {
            try { await OnRenameClickedAsync(node); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Rename click failed: {ex}"); }
        };
        actions.Children.Add(renameBtn);

        var forgetBtn = new Button
        {
            Content = LocalizationHelper.GetString("NodesPage_Action_Forget"),
            IsEnabled = clientAvailable,
            MinWidth = 96,
            // Critical-coloured text marks destructive intent without
            // turning the whole button red — destructive primary buttons
            // are reserved for the confirmation dialog.
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        forgetBtn.Click += async (s, e) =>
        {
            try { await OnForgetClickedAsync(node); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Forget click failed: {ex}"); }
        };
        actions.Children.Add(forgetBtn);

        footer.Children.Add(actions);
        return footer;
    }

    private Grid BuildIdentityRow(GatewayNodeInfo node)
    {
        // Use a Grid so a long node id (full GUIDs are 36+ chars) ellipsizes
        // instead of pushing the copy button off-screen or out of the card.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idText = new TextBlock
        {
            Text = node.NodeId,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, monospace"),
            // Body text style so the id sits at the same visual weight as
            // the rest of the card body (Version / Hardware / Network rows).
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolTipService.SetToolTip(idText, node.NodeId);
        Grid.SetColumn(idText, 0);
        grid.Children.Add(idText);

        var copyBtn = new Button
        {
            Content = "📋",
            Padding = new Thickness(6, 2, 6, 2),
            Tag = node.NodeId,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copyBtn.Click += OnCopyDeviceId;
        Grid.SetColumn(copyBtn, 1);
        grid.Children.Add(copyBtn);
        return grid;
    }

    private static FrameworkElement? BuildVersionRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(node.Version)) parts.Add(node.Version!);
        if (!string.IsNullOrWhiteSpace(node.CoreVersion)) parts.Add($"core {node.CoreVersion}");
        if (!string.IsNullOrWhiteSpace(node.UiVersion)) parts.Add($"ui {node.UiVersion}");
        if (parts.Count == 0) return null;
        return MakeLabeledRow(
            LocalizationHelper.GetString("NodesPage_Label_Version"),
            string.Join(" · ", parts));
    }

    private static FrameworkElement? BuildHardwareRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(node.DeviceFamily)) parts.Add(node.DeviceFamily!);
        if (!string.IsNullOrWhiteSpace(node.ModelIdentifier)) parts.Add(node.ModelIdentifier!);
        if (parts.Count == 0) return null;
        return MakeLabeledRow(
            LocalizationHelper.GetString("NodesPage_Label_Hardware"),
            string.Join(" · ", parts));
    }

    private static FrameworkElement? BuildNetworkRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(node.RemoteIp)) parts.Add(node.RemoteIp!);
        if (!string.IsNullOrWhiteSpace(node.ClientId)) parts.Add(node.ClientId!);
        if (!string.IsNullOrWhiteSpace(node.ClientMode)) parts.Add(node.ClientMode!);
        if (parts.Count == 0) return null;
        return MakeLabeledRow(
            LocalizationHelper.GetString("NodesPage_Label_Network"),
            string.Join(" · ", parts));
    }

    private static FrameworkElement? BuildTimestampsRow(GatewayNodeInfo node)
    {
        var parts = new List<string>(3);
        if (node.ApprovedAt.HasValue)
            parts.Add($"{LocalizationHelper.GetString("NodesPage_Label_Approved")} {FormatAge(node.ApprovedAt.Value)}");
        if (node.ConnectedAt.HasValue)
            parts.Add($"{LocalizationHelper.GetString("NodesPage_Label_Connected")} {FormatAge(node.ConnectedAt.Value)}");
        if (node.LastSeen.HasValue)
        {
            var label = $"{LocalizationHelper.GetString("NodesPage_Label_LastSeen")} {FormatAge(node.LastSeen.Value)}";
            if (!string.IsNullOrWhiteSpace(node.LastSeenReason))
                label += $" ({node.LastSeenReason})";
            parts.Add(label);
        }
        if (parts.Count == 0) return null;
        return MakeSingleLine(string.Join(" · ", parts));
    }

    private static StackPanel? BuildCapabilitiesSection(GatewayNodeInfo node)
    {
        if (node.Capabilities is not { Count: > 0 } caps) return null;
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("NodesPage_Label_Capabilities"),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var cap in caps)
        {
            var badge = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
            };
            badge.Child = new TextBlock
            {
                Text = cap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            wrap.Children.Add(badge);
        }
        section.Children.Add(wrap);
        return section;
    }

    private static Expander? BuildCommandsSection(GatewayNodeInfo node)
    {
        if (node.Commands is not { Count: > 0 } cmds) return null;
        var disabled = new HashSet<string>(node.DisabledCommands ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var expander = new Expander
        {
            Header = string.Format(LocalizationHelper.GetString("NodesPage_Commands_Header"), cmds.Count),
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var stack = new StackPanel { Spacing = 2 };
        foreach (var cmd in cmds)
        {
            var isDisabled = disabled.Contains(cmd);
            stack.Children.Add(new TextBlock
            {
                Text = isDisabled
                    ? $"  • {cmd}{LocalizationHelper.GetString("NodesPage_Command_DisabledSuffix")}"
                    : $"  • {cmd}",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources[
                    isDisabled ? "TextFillColorTertiaryBrush" : "TextFillColorSecondaryBrush"],
            });
        }
        expander.Content = stack;
        return expander;
    }

    private static StackPanel? BuildPermissionsSection(GatewayNodeInfo node)
    {
        if (node.Permissions is not { Count: > 0 } perms) return null;
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("NodesPage_Label_Permissions"),
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        foreach (var kv in perms)
        {
            section.Children.Add(new TextBlock
            {
                Text = $"  {kv.Key}: {(kv.Value ? "✅" : "❌")}",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            });
        }
        return section;
    }

    private static Expander? BuildPathEnvSection(GatewayNodeInfo node)
    {
        if (string.IsNullOrWhiteSpace(node.PathEnv)) return null;
        // PATH can contain usernames, network shares, build tool locations.
        // Keep it collapsed by default so it doesn't reveal those at a glance.
        var expander = new Expander
        {
            Header = LocalizationHelper.GetString("NodesPage_Label_PathEnv"),
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Content = new TextBlock
        {
            Text = node.PathEnv,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, monospace"),
            // PATH lines are very dense; staying at Caption keeps long
            // entries readable without dominating the card.
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        return expander;
    }

    private static Grid MakeLabeledRow(string label, string value)
    {
        // Use a Grid with star value column so long values wrap properly. A
        // horizontal StackPanel gives children unbounded width, which makes
        // TextWrapping=Wrap a no-op and lets long network/hardware strings
        // overflow the card.
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private static TextBlock MakeSingleLine(string value)
    {
        // No wrapping container needed: a TextBlock placed directly in a
        // vertical StackPanel gets the full width and wraps cleanly.
        return new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string FormatAge(DateTime utc)
    {
        // Single canonical age formatter shared with GatewayNodeInfo.DetailText
        // (so a node never shows "1d ago" in one place and "36h ago" in
        // another for the same timestamp).
        return ModelFormatting.FormatAge(utc);
    }

    private async Task OnRenameClickedAsync(GatewayNodeInfo node)
    {
        if (_hub?.GatewayClient is not { } client) return;
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var input = new TextBox
            {
                // Pre-fill ONLY when the gateway gave us an explicit display
                // name. If we fell back to the id (HasExplicitDisplayName=
                // false), seeding the textbox with the id would cause Enter
                // to persist that id as the new display name.
                Text = node.HasExplicitDisplayName ? node.DisplayName : string.Empty,
                MaxLength = 64,
                AcceptsReturn = false,
                SelectionStart = 0,
                PlaceholderText = LocalizationHelper.GetString("NodesPage_Rename_Placeholder"),
            };
            // Focus + select-all only after the TextBox is actually attached
            // to the visual tree; calling these before Loaded is a no-op.
            input.Loaded += (_, _) =>
            {
                input.Focus(FocusState.Programmatic);
                input.SelectAll();
            };

            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = string.Format(
                    LocalizationHelper.GetString("NodesPage_Rename_Body"),
                    string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(input);
            content.Children.Add(errorBlock);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("NodesPage_Rename_Title"),
                Content = content,
                PrimaryButtonText = LocalizationHelper.GetString("NodesPage_Rename_Primary"),
                CloseButtonText = LocalizationHelper.GetString("NodesPage_Common_Cancel"),
                // Rename is non-destructive — Enter should confirm. (Forget
                // is destructive and intentionally keeps Close as default.)
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var newName = input.Text?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        errorBlock.Text = LocalizationHelper.GetString("NodesPage_Rename_Error_Empty");
                        errorBlock.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }
                    // Don't short-circuit on "name unchanged" — our local
                    // node.DisplayName might be stale (another operator may
                    // have renamed in the interim). Always send to the
                    // gateway and let it decide.

                    s.IsPrimaryButtonEnabled = false;
                    input.IsEnabled = false;
                    errorBlock.Visibility = Visibility.Collapsed;

                    var result = await client.NodeRenameAsync(node.NodeId, newName);
                    if (!result.Success)
                    {
                        errorBlock.Text = result.ErrorMessage ?? LocalizationHelper.GetString("NodesPage_Rename_Error_Generic");
                        errorBlock.Visibility = Visibility.Visible;
                        s.IsPrimaryButtonEnabled = true;
                        input.IsEnabled = true;
                        args.Cancel = true;
                        return;
                    }

                    // Gateway does not broadcast rename; trigger an explicit
                    // list refresh so this card reflects the new display name
                    // (and any other state that changed in the meantime).
                    _ = client.RequestNodesAsync();
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task OnForgetClickedAsync(GatewayNodeInfo node)
    {
        if (_hub?.GatewayClient is not { } client) return;
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("NodesPage_Forget_Body"),
                TextWrapping = TextWrapping.Wrap,
            });
            // Surface the identity prominently so the user is forgetting the
            // node they think they're forgetting.
            var identity = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
            identity.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(node.DisplayName) ? node.ShortId : node.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            var subtitle = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(node.Platform)) subtitle.Add(node.Platform!);
            subtitle.Add(node.ShortId);
            identity.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", subtitle),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                FontSize = 12,
            });
            body.Children.Add(identity);
            body.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("NodesPage_Forget_Warning"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
            };
            body.Children.Add(errorBlock);

            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("NodesPage_Forget_Title"),
                Content = body,
                PrimaryButtonText = LocalizationHelper.GetString("NodesPage_Forget_Primary"),
                CloseButtonText = LocalizationHelper.GetString("NodesPage_Common_Cancel"),
                // Cancel is the default so pressing Enter does NOT confirm a
                // destructive action. Leaving the primary button at its
                // default style (no accent fill) keeps the destructive label
                // visually muted — Cancel remains the recommended action.
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            // Use the deferral pattern so we can keep the dialog open and
            // surface an inline error when NodePairRemoveAsync reports
            // failure (disconnected, missing scope, unknown nodeId). On
            // success the dialog closes and the gateway's
            // node.pair.resolved broadcast triggers a node.list refresh.
            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    s.IsPrimaryButtonEnabled = false;
                    errorBlock.Visibility = Visibility.Collapsed;

                    var result = await client.NodePairRemoveAsync(node.NodeId);
                    if (!result.Success)
                    {
                        // Surface the actual gateway error message when we
                        // have one (e.g. "missing scope: operator.pairing"),
                        // falling back to a generic message for unrecognised
                        // failures so non-English locales never see a raw
                        // English string from the server.
                        errorBlock.Text = result.ErrorMessage
                            ?? LocalizationHelper.GetString("NodesPage_Forget_Error_Generic");
                        errorBlock.Visibility = Visibility.Visible;
                        s.IsPrimaryButtonEnabled = true;
                        args.Cancel = true;
                    }
                    // On success: dialog closes; gateway's node.pair.resolved
                    // broadcast triggers a node.list refresh.
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string deviceId)
        {
            ClipboardHelper.CopyText(deviceId);
            btn.Content = "✓";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, a) => { btn.Content = "📋"; timer.Stop(); };
            timer.Start();
        }
    }

    private static WinColor GetPlatformColor(string platform) => platform.ToLowerInvariant() switch
    {
        "windows" => WinColor.FromArgb(255, 0, 120, 215),
        "macos" => WinColor.FromArgb(255, 162, 132, 94),
        "linux" => WinColor.FromArgb(255, 221, 72, 20),
        "ios" => WinColor.FromArgb(255, 0, 122, 255),
        "android" => WinColor.FromArgb(255, 61, 220, 132),
        _ => WinColor.FromArgb(255, 128, 128, 128),
    };

    public void UpdatePairingRequests(PairingListInfo data)
    {
        PairingList.Children.Clear();
        if (data.Pending.Count == 0)
        {
            PairingSection.Visibility = Visibility.Collapsed;
            return;
        }
        PairingSection.Visibility = Visibility.Visible;

        foreach (var req in data.Pending)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text = req.DisplayName ?? req.NodeId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{req.Platform ?? "unknown"} · {req.RemoteIp ?? ""}",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            if (req.IsRepair)
            {
                info.Children.Add(new TextBlock
                {
                    Text = "⚠️ Repair request",
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Reject" };
            var capturedId = req.RequestId;
            approveBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.NodePairApproveAsync(capturedId); };
            rejectBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.NodePairRejectAsync(capturedId); };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            card.Child = grid;
            PairingList.Children.Add(card);
        }
    }

    public void UpdateDevicePairingRequests(DevicePairingListInfo data)
    {
        DevicePairingList.Children.Clear();
        if (data.Pending.Count == 0)
        {
            DevicePairingSection.Visibility = Visibility.Collapsed;
            return;
        }
        DevicePairingSection.Visibility = Visibility.Visible;

        foreach (var req in data.Pending)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text = req.DisplayName ?? req.DeviceId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var detail = $"{req.Platform ?? "unknown"}";
            if (!string.IsNullOrEmpty(req.Role)) detail += $" · {req.Role}";
            if (!string.IsNullOrEmpty(req.RemoteIp)) detail += $" · {req.RemoteIp}";
            info.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            if (req.Scopes is { Length: > 0 })
            {
                info.Children.Add(new TextBlock
                {
                    Text = $"Scopes: {string.Join(", ", req.Scopes)}",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Reject" };
            var capturedId = req.RequestId;
            approveBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.DevicePairApproveAsync(capturedId); };
            rejectBtn.Click += async (s, e) => { if (_hub?.GatewayClient != null) await _hub.GatewayClient.DevicePairRejectAsync(capturedId); };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            card.Child = grid;
            DevicePairingList.Children.Add(card);
        }
    }

    public void UpdatePresence(PresenceEntry[] entries)
    {
        DispatcherQueue?.TryEnqueue(() => RenderPresence(entries));
    }

    private void RenderPresence(PresenceEntry[] entries)
    {
        PresenceList.Children.Clear();

        if (entries.Length == 0)
        {
            PresenceSection.Visibility = Visibility.Collapsed;
            return;
        }

        PresenceSection.Visibility = Visibility.Visible;

        foreach (var entry in entries)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

            row.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrEmpty(entry.Platform))
                row.Children.Add(new TextBlock
                {
                    Text = entry.PlatformLabel,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            if (!string.IsNullOrEmpty(entry.Mode))
                row.Children.Add(new TextBlock
                {
                    Text = entry.ModeLabel,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            if (!string.IsNullOrEmpty(entry.LastSeenText))
                row.Children.Add(new TextBlock
                {
                    Text = entry.LastSeenText,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                });

            card.Child = row;
            PresenceList.Children.Add(card);
        }
    }
}

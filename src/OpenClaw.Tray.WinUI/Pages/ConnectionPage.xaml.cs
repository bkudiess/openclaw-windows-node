using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Services.Connection;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class ConnectionPage : Page
{
    private HubWindow? _hub;
    private IGatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    private int _connectionAttempts;

    public ConnectionPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        _connectionManager = hub.ConnectionManager;
        _gatewayRegistry = hub.GatewayRegistry;
        var settings = hub.Settings;
        if (settings == null) return;

        // Subscribe to live state changes from the connection manager
        if (_connectionManager != null)
            _connectionManager.StateChanged += OnManagerStateChanged;

        // Populate manual connection fields
        GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";
        TokenTextBox.Text = settings.Token ?? "";
        SshToggle.IsOn = settings.UseSshTunnel;
        SshDetailsPanel.Visibility = settings.UseSshTunnel ? Visibility.Visible : Visibility.Collapsed;
        SshUserBox.Text = settings.SshTunnelUser ?? "";
        SshHostBox.Text = settings.SshTunnelHost ?? "";
        SshRemotePortBox.Text = settings.SshTunnelRemotePort.ToString();
        SshLocalPortBox.Text = settings.SshTunnelLocalPort.ToString();

        UpdateStatus(hub.CurrentStatus);
        UpdateDeviceIdentity();
        LoadConnectionLog();
        LoadRecentGateways();
    }

    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            var status = snapshot.OverallState switch
            {
                OverallConnectionState.Connected or OverallConnectionState.Ready or OverallConnectionState.Degraded
                    => ConnectionStatus.Connected,
                OverallConnectionState.Connecting => ConnectionStatus.Connecting,
                OverallConnectionState.PairingRequired => ConnectionStatus.Connecting,
                OverallConnectionState.Error => ConnectionStatus.Error,
                _ => ConnectionStatus.Disconnected
            };
            UpdateStatus(status);
            LoadRecentGateways();
        });
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        var (color, text) = status switch
        {
            ConnectionStatus.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            ConnectionStatus.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            ConnectionStatus.Error => (Microsoft.UI.Colors.Red, "Error"),
            _ => (Microsoft.UI.Colors.Gray, "Disconnected")
        };

        StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        StatusText.Text = text;

        var isConnected = status == ConnectionStatus.Connected;
        ReconnectButton.IsEnabled = status != ConnectionStatus.Connecting;
        ReconnectButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;

        if (status == ConnectionStatus.Connecting)
        {
            _connectionAttempts++;
            ConnectionAttemptsText.Text = $"Connection attempt {_connectionAttempts}…";
            ConnectionAttemptsText.Visibility = Visibility.Visible;
        }
        else
        {
            if (isConnected) _connectionAttempts = 0;
            ConnectionAttemptsText.Visibility = Visibility.Collapsed;
        }

        // Gateway details
        var self = _hub?.LastGatewaySelf;
        var effectiveUrl = _hub?.Settings?.GetEffectiveGatewayUrl() ?? "";

        if (self != null && status == ConnectionStatus.Connected)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(self.ServerVersion))
                parts.Add($"v{self.ServerVersion}");
            parts.Add($"Up {self.UptimeText}");
            if (self.PresenceCount is > 0)
                parts.Add($"{self.PresenceCount} clients");
            GatewayDetailText.Text = string.Join(" · ", parts);

            var authLabel = string.IsNullOrWhiteSpace(self.AuthMode) ? "" : $" · {self.AuthMode} auth";
            GatewayUrlDetail.Text = $"{SanitizeUrl(effectiveUrl)}{authLabel}";
        }
        else
        {
            GatewayDetailText.Text = "";
            GatewayUrlDetail.Text = !string.IsNullOrEmpty(effectiveUrl)
                ? SanitizeUrl(effectiveUrl) : "";
        }

        // Operator status
        OperatorStatusText.Text = status switch
        {
            ConnectionStatus.Connected => "Operator: ✓ Connected",
            ConnectionStatus.Connecting => "Operator: ⏳ Connecting",
            ConnectionStatus.Error => "Operator: ✗ Error",
            _ => "Operator: — Disconnected"
        };

        // Node status
        if (_hub != null && _hub.Settings?.EnableNodeMode == true)
        {
            if (_hub.NodeIsPaired)
                NodeStatusText.Text = "Node: ✓ Paired";
            else if (_hub.NodeIsPendingApproval)
                NodeStatusText.Text = "Node: ⏳ Pending approval";
            else if (_hub.NodeIsConnected)
                NodeStatusText.Text = "Node: ✓ Connected";
            else
                NodeStatusText.Text = "Node: — Disconnected";
        }
        else
        {
            NodeStatusText.Text = "Node: — Disabled";
        }

        UpdateDeviceIdentity();
        LoadConnectionLog();

        // Show auth error if present
        var authError = _hub?.LastAuthError;
        if (!string.IsNullOrEmpty(authError))
        {
            AuthErrorBar.Message = GetAuthErrorGuidance(authError!);
            AuthErrorBar.IsOpen = true;
        }
        else
        {
            AuthErrorBar.IsOpen = false;
        }
    }

    private static string GetAuthErrorGuidance(string error)
    {
        if (error.Contains("token", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nCheck your token in the settings below, or paste a new setup code.";
        if (error.Contains("pairing", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nYour device needs approval on the gateway host.";
        if (error.Contains("password", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThis gateway requires password authentication.";
        if (error.Contains("signature", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThe gateway may require a different auth protocol version.";
        return $"{error}\n\nCheck your connection settings and try again.";
    }

    private void UpdateDeviceIdentity()
    {
        if (_hub == null) return;

        var shortId = _hub.NodeShortDeviceId;
        var fullId = _hub.NodeFullDeviceId;

        if (!string.IsNullOrEmpty(shortId) || !string.IsNullOrEmpty(fullId))
        {
            DeviceIdentityCard.Visibility = Visibility.Visible;
            DeviceIdText.Text = shortId ?? fullId ?? "";

            if (_hub.NodeIsPaired)
            {
                PairingStatusText.Text = "Pairing: ✓ Paired";
                ApprovalHelpPanel.Visibility = Visibility.Collapsed;
            }
            else if (_hub.NodeIsPendingApproval)
            {
                PairingStatusText.Text = "Pairing: ⏳ Pending approval";
                ApprovalHelpPanel.Visibility = Visibility.Visible;
                var deviceRef = fullId ?? shortId ?? "";
                ApprovalCommandText.Text = $"openclaw devices approve {deviceRef}";
            }
            else
            {
                PairingStatusText.Text = "Pairing: — Not paired";
                ApprovalHelpPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            DeviceIdentityCard.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadConnectionLog()
    {
        ConnectionLogPanel.Children.Clear();
        // Pull from node + error categories which contain connection-related events
        var nodeItems = ActivityStreamService.GetItems(10, "node");
        var errorItems = ActivityStreamService.GetItems(5, "error");
        var items = nodeItems.Concat(errorItems)
            .OrderByDescending(i => i.Timestamp)
            .Take(10)
            .ToList();

        if (items.Count == 0)
        {
            ConnectionLogEmpty.Visibility = Visibility.Visible;
            return;
        }

        ConnectionLogEmpty.Visibility = Visibility.Collapsed;
        foreach (var item in items)
        {
            var tb = new TextBlock
            {
                Text = $"{item.Timestamp:HH:mm:ss}  {item.Title}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            ConnectionLogPanel.Children.Add(tb);
        }
    }

    private static string SanitizeUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Port > 0 ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : $"{uri.Scheme}://{uri.Host}";
        }
        catch { }
        return url;
    }

    // ─── Event Handlers ───

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        _hub?.DisconnectAction?.Invoke();
    }

    private void OnReconnect(object sender, RoutedEventArgs e)
    {
        _connectionAttempts = 0;
        _hub?.ReconnectAction?.Invoke();
    }

    private void OnSshToggled(object sender, RoutedEventArgs e)
    {
        SshDetailsPanel.Visibility = SshToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Testing…";
        TestButton.IsEnabled = false;
        try
        {
            if (_hub?.GatewayClient != null)
            {
                await _hub.GatewayClient.CheckHealthAsync();
                TestResultText.Text = "✓ Connection successful";
            }
            else
            {
                TestResultText.Text = "Not connected — save settings and reconnect";
            }
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = _hub?.Settings;
        if (settings == null) return;

        settings.GatewayUrl = GatewayUrlTextBox.Text.Trim();
        settings.Token = TokenTextBox.Text.Trim();
        settings.UseSshTunnel = SshToggle.IsOn;
        settings.SshTunnelUser = SshUserBox.Text.Trim();
        settings.SshTunnelHost = SshHostBox.Text.Trim();
        if (int.TryParse(SshRemotePortBox.Text, out var rp)) settings.SshTunnelRemotePort = rp;
        if (int.TryParse(SshLocalPortBox.Text, out var lp)) settings.SshTunnelLocalPort = lp;

        settings.Save();
        _hub?.RaiseSettingsSaved();
    }

    private async void OnApplySetupCode(object sender, RoutedEventArgs e)
    {
        var code = SetupCodeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetupCodeResultText.Text = "Please paste a setup code.";
            return;
        }

        if (_connectionManager != null)
        {
            // Use the unified manager path
            ApplySetupCodeButton.IsEnabled = false;
            SetupCodeResultText.Text = "Applying…";
            try
            {
                var result = await _connectionManager.ApplySetupCodeAsync(code);
                SetupCodeResultText.Text = result.Outcome switch
                {
                    SetupCodeOutcome.Success => $"✓ Applied — gateway: {SanitizeUrl(result.GatewayUrl ?? "")}",
                    SetupCodeOutcome.InvalidCode => $"✗ {result.ErrorMessage ?? "Invalid setup code"}",
                    SetupCodeOutcome.InvalidUrl => $"✗ {result.ErrorMessage ?? "Invalid URL"}",
                    SetupCodeOutcome.ConnectionFailed => $"✗ {result.ErrorMessage ?? "Connection failed"}",
                    _ => $"✗ {result.ErrorMessage ?? "Unknown error"}"
                };
                if (result.Outcome == SetupCodeOutcome.Success && result.GatewayUrl != null)
                    GatewayUrlTextBox.Text = result.GatewayUrl;
            }
            finally
            {
                ApplySetupCodeButton.IsEnabled = true;
            }
        }
        else
        {
            // Fallback: decode and apply via settings
            var decoded = SetupCodeDecoder.Decode(code);
            if (!decoded.Success)
            {
                SetupCodeResultText.Text = $"✗ {decoded.Error}";
                return;
            }

            var settings = _hub?.Settings;
            if (settings == null) return;

            if (!string.IsNullOrEmpty(decoded.Url))
                settings.GatewayUrl = decoded.Url;
            if (!string.IsNullOrEmpty(decoded.Token))
                settings.BootstrapToken = decoded.Token;

            settings.Save();
            SetupCodeResultText.Text = $"✓ Applied — gateway: {SanitizeUrl(decoded.Url ?? settings.GatewayUrl ?? "")}";
            GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";
            _hub?.RaiseSettingsSaved();
        }
    }

    private void OnSetupCodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var code = SetupCodeTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length < 10)
        {
            SetupCodePreviewPanel.Visibility = Visibility.Collapsed;
            SetupCodeResultText.Text = "";
            return;
        }

        var decoded = SetupCodeDecoder.Decode(code);
        if (decoded.Success)
        {
            SetupCodePreviewUrl.Text = $"Gateway: {decoded.Url ?? "(not specified)"}";
            SetupCodePreviewToken.Text = $"Token: {decoded.Token?[..Math.Min(8, decoded.Token?.Length ?? 0)]}…";
            SetupCodePreviewPanel.Visibility = Visibility.Visible;
            SetupCodeResultText.Text = "";
        }
        else
        {
            SetupCodePreviewPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadRecentGateways()
    {
        RecentGatewayListPanel.Children.Clear();
        if (_gatewayRegistry == null)
        {
            RecentGatewaysCard.Visibility = Visibility.Collapsed;
            return;
        }

        var gateways = _gatewayRegistry.GetAll();
        if (gateways.Count == 0)
        {
            RecentGatewaysCard.Visibility = Visibility.Collapsed;
            return;
        }

        RecentGatewaysCard.Visibility = Visibility.Visible;
        var active = _gatewayRegistry.GetActive();

        foreach (var gw in gateways)
        {
            var isActive = gw.Id == active?.Id;
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var indicator = new TextBlock
            {
                Text = isActive ? "✓" : "",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 16,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            Grid.SetColumn(indicator, 0);
            row.Children.Add(indicator);

            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoPanel.Children.Add(new TextBlock
            {
                Text = GatewayUrlHelper.SanitizeForDisplay(gw.Url),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var statusParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(gw.SharedGatewayToken)) statusParts.Add("shared");
            if (!string.IsNullOrWhiteSpace(gw.BootstrapToken)) statusParts.Add("bootstrap");
            if (gw.SshTunnel != null) statusParts.Add("SSH");
            var suffix = statusParts.Count > 0 ? $"  ({string.Join(", ", statusParts)})" : "";
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{gw.Id[..Math.Min(8, gw.Id.Length)]}…{suffix}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            Grid.SetColumn(infoPanel, 1);
            row.Children.Add(infoPanel);

            var connectBtn = new Button
            {
                Content = isActive ? "Active" : "Connect",
                IsEnabled = !isActive,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = gw.Id,
            };
            connectBtn.Click += OnConnectRecentGateway;
            Grid.SetColumn(connectBtn, 2);
            row.Children.Add(connectBtn);

            var removeBtn = new Button
            {
                Content = "✕",
                VerticalAlignment = VerticalAlignment.Center,
                Tag = gw.Id,
                Padding = new Thickness(6, 4, 6, 4),
            };
            removeBtn.Click += OnRemoveRecentGateway;
            Grid.SetColumn(removeBtn, 3);
            row.Children.Add(removeBtn);

            RecentGatewayListPanel.Children.Add(row);
        }
    }

    private void OnConnectRecentGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gwId) return;
        if (_gatewayRegistry == null || _connectionManager == null) return;

        _gatewayRegistry.SetActive(gwId);
        _ = _connectionManager.SwitchGatewayAsync(gwId);
        LoadRecentGateways();
    }

    private void OnRemoveRecentGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gwId) return;
        _gatewayRegistry?.Remove(gwId);
        _gatewayRegistry?.Save();
        LoadRecentGateways();
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var id = _hub?.NodeFullDeviceId ?? _hub?.NodeShortDeviceId;
        if (string.IsNullOrEmpty(id)) return;
        CopyToClipboard(id);
    }

    private void OnCopyApprovalCommand(object sender, RoutedEventArgs e)
    {
        var cmd = ApprovalCommandText.Text;
        if (!string.IsNullOrEmpty(cmd))
            CopyToClipboard(cmd);
    }

    private static void CopyToClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }
}

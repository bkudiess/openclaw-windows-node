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
    private GatewayDiscoveryService? _discoveryService;
    private List<DiscoveredGateway> _discoveredGateways = new();
    private int _connectionAttempts;
    private string? _pendingGatewayUrl; // URL waiting for token input
    private string? _pendingGatewayId;

    public ConnectionPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            _discoveryService?.Dispose();
            _discoveryService = null;
        };
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

        // Auto-scan for gateways when disconnected or when no URL configured
        if (hub.CurrentStatus != ConnectionStatus.Connected ||
            string.IsNullOrWhiteSpace(settings.GatewayUrl))
        {
            _ = AutoScanAsync();
        }
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

    private async System.Threading.Tasks.Task AutoScanAsync()
    {
        try
        {
            _discoveryService?.Dispose();
            _discoveryService = new GatewayDiscoveryService();
            ScanProgressRing.IsActive = true;
            ScanProgressRing.Visibility = Visibility.Visible;
            GatewayEmptyText.Text = "Scanning for gateways…";

            await _discoveryService.StartDiscoveryAsync();
            _discoveredGateways = _discoveryService.Gateways.ToList();
            PopulateGatewayList();
        }
        catch
        {
            // Silently fail on auto-scan — user can manually scan
        }
        finally
        {
            ScanProgressRing.IsActive = false;
            ScanProgressRing.Visibility = Visibility.Collapsed;
        }
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

    private async void OnScanForGateways(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanProgressRing.IsActive = true;
        ScanProgressRing.Visibility = Visibility.Visible;
        GatewayEmptyText.Visibility = Visibility.Collapsed;

        try
        {
            _discoveryService?.Dispose();
            _discoveryService = new GatewayDiscoveryService();
            await _discoveryService.StartDiscoveryAsync();
            _discoveredGateways = _discoveryService.Gateways.ToList();
            PopulateGatewayList();
        }
        catch (Exception ex)
        {
            GatewayEmptyText.Text = $"Discovery failed: {ex.Message}";
            GatewayEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ScanProgressRing.IsActive = false;
            ScanProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateGatewayList()
    {
        var currentUrl = _hub?.Settings?.GetEffectiveGatewayUrl();
        // Build display list: discovered gateways + synthesized current gateway if not found
        var displayList = new List<DiscoveredGateway>(_discoveredGateways);

        // Compare by host:port to avoid ws:// vs http:// mismatches
        string? currentHostPort = null;
        Uri? currentUri = null;
        if (!string.IsNullOrEmpty(currentUrl) && Uri.TryCreate(currentUrl, UriKind.Absolute, out var parsedUri))
        {
            currentUri = parsedUri;
            currentHostPort = $"{parsedUri.Host}:{parsedUri.Port}";
        }

        if (_hub?.CurrentStatus == ConnectionStatus.Connected &&
            currentHostPort != null &&
            !displayList.Any(g => $"{g.Host}:{g.Port}".Equals(currentHostPort, StringComparison.OrdinalIgnoreCase)))
        {
            displayList.Insert(0, new DiscoveredGateway
            {
                Id = $"current-{currentHostPort}",
                DisplayName = _hub.LastGatewaySelf?.ServerVersion != null
                    ? $"Current Gateway (v{_hub.LastGatewaySelf.ServerVersion})"
                    : "Current Gateway",
                Host = currentUri!.Host,
                Port = currentUri!.Port,
                TlsEnabled = currentUri!.Scheme is "wss" or "https"
            });
        }

        if (displayList.Count > 0)
        {
            GatewayListPanel.Children.Clear();
            foreach (var gw in displayList)
            {
                var row = new Grid { Padding = new Thickness(4, 8, 4, 8), ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var nameTb = new TextBlock
                {
                    Text = gw.DisplayName,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
                };
                var addrTb = new TextBlock
                {
                    Text = $"{gw.Host}:{gw.Port}",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                info.Children.Add(nameTb);
                info.Children.Add(addrTb);
                Grid.SetColumn(info, 0);
                row.Children.Add(info);

                if (gw.TlsEnabled)
                {
                    var tls = new TextBlock { Text = "🔒", VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(tls, 1);
                    row.Children.Add(tls);
                }

                // Match current gateway by host:port
                var gwHostPort = $"{gw.Host}:{gw.Port}";
                var isCurrentGw = currentHostPort != null &&
                    gwHostPort.Equals(currentHostPort, StringComparison.OrdinalIgnoreCase);
                var connectBtn = new Button
                {
                    Content = isCurrentGw ? "✓" : "→",
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = !isCurrentGw,
                    Tag = gw.Id
                };
                connectBtn.Click += OnConnectToGateway;
                Grid.SetColumn(connectBtn, 2);
                row.Children.Add(connectBtn);

                GatewayListPanel.Children.Add(row);
            }
            GatewayListPanel.Visibility = Visibility.Visible;
            GatewayEmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            GatewayListPanel.Visibility = Visibility.Collapsed;
            GatewayEmptyText.Text = "No gateways found. Click Scan to search.";
            GatewayEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void OnConnectToGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string gatewayId) return;
        var gw = _discoveredGateways.FirstOrDefault(g => g.Id == gatewayId);
        if (gw == null || _hub?.Settings == null) return;

        // When switching to a different gateway, always prompt for token
        // (different gateways may have different tokens)
        var currentUrl = _hub.Settings.GetEffectiveGatewayUrl() ?? "";
        var isSameGateway = !string.IsNullOrEmpty(currentUrl) &&
            Uri.TryCreate(currentUrl, UriKind.Absolute, out var curUri) &&
            $"{curUri.Host}:{curUri.Port}".Equals($"{gw.Host}:{gw.Port}", StringComparison.OrdinalIgnoreCase);

        if (isSameGateway)
            return; // already connected to this one

        _pendingGatewayUrl = gw.ConnectionUrl;
        _pendingGatewayId = gw.Id;
        TokenPromptText.Text = $"Connect to gateway at {gw.Host}:{gw.Port}";
        TokenPromptBox.Text = _hub.Settings.Token ?? "";
        TokenPromptPanel.Visibility = Visibility.Visible;
        TokenPromptBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnConnectWithToken(object sender, RoutedEventArgs e)
    {
        var token = TokenPromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(token) || _hub?.Settings == null || string.IsNullOrEmpty(_pendingGatewayUrl))
            return;

        _hub.Settings.GatewayUrl = _pendingGatewayUrl;
        _hub.Settings.Token = token;
        if (!string.IsNullOrEmpty(_pendingGatewayId))
            _hub.Settings.PreferredGatewayId = _pendingGatewayId;
        _hub.Settings.Save();
        _hub?.RaiseSettingsSaved();

        // Clear auth error from previous attempt
        if (_hub != null) _hub.LastAuthError = null;
        AuthErrorBar.IsOpen = false;

        GatewayUrlTextBox.Text = _pendingGatewayUrl;
        TokenTextBox.Text = token;
        TokenPromptPanel.Visibility = Visibility.Collapsed;
        _pendingGatewayUrl = null;
        _pendingGatewayId = null;

        // Refresh discovery list to show ✓ on newly connected gateway
        PopulateGatewayList();
    }

    private void OnCancelTokenPrompt(object sender, RoutedEventArgs e)
    {
        TokenPromptPanel.Visibility = Visibility.Collapsed;
        _pendingGatewayUrl = null;
        _pendingGatewayId = null;
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

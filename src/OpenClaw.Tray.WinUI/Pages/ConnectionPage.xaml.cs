using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Services.Connection;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Pages;

public sealed partial class ConnectionPage : Page
{
    private HubWindow? _hub;
    private IGatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    private int _connectionAttempts;
    private bool _suppressNodeModeToggle;

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

        Unloaded += OnPageUnloaded;

        // Populate manual connection fields
        GatewayUrlTextBox.Text = settings.GatewayUrl ?? "";
        SshToggle.IsOn = settings.UseSshTunnel;
        SshUserBox.Text = settings.SshTunnelUser ?? "";
        SshHostBox.Text = settings.SshTunnelHost ?? "";
        SshRemotePortBox.Text = settings.SshTunnelRemotePort.ToString();
        SshLocalPortBox.Text = settings.SshTunnelLocalPort.ToString();

        UpdateStatus(hub.CurrentStatus);
        LoadRecentGateways();

        // Initialize node mode toggle
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = settings.EnableNodeMode;
        _suppressNodeModeToggle = false;

        // Default tab: show Setup Code when disconnected, Recent Gateways when connected
        var initialSnapshot = _connectionManager?.CurrentSnapshot;
        var isInitiallyConnected = initialSnapshot?.OverallState is
            OverallConnectionState.Connected or OverallConnectionState.Ready or OverallConnectionState.Degraded;
        ConnectionPivot.SelectedIndex = isInitiallyConnected ? 1 : 0;
    }

    private GatewayConnectionSnapshot? _lastSnapshot;

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_connectionManager != null)
            _connectionManager.StateChanged -= OnManagerStateChanged;
    }

    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _lastSnapshot = snapshot;
            UpdateFromSnapshot(snapshot);
            LoadRecentGateways();
        });
    }

    public void UpdateStatus(ConnectionStatus status)
    {
        // Legacy bridge — convert to snapshot-based update
        var snapshot = _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle;
        UpdateFromSnapshot(snapshot);
    }

    private OverallConnectionState _lastDisplayedOverallState;

    private void UpdateFromSnapshot(GatewayConnectionSnapshot snapshot)
    {
        // Debounce: skip transient flicker if overall state bounces back
        // within the same dispatch cycle (e.g. Connected→Connecting→Connected)
        var overallChanged = snapshot.OverallState != _lastDisplayedOverallState;

        // Overall status — only update text/dot/tint when state actually changes
        if (overallChanged)
        {
            _lastDisplayedOverallState = snapshot.OverallState;

            var (color, text) = snapshot.OverallState switch
            {
                OverallConnectionState.Connected or OverallConnectionState.Ready => (Microsoft.UI.Colors.LimeGreen, "Connected"),
                OverallConnectionState.Degraded => (Microsoft.UI.Colors.Orange, "Degraded"),
                OverallConnectionState.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
                OverallConnectionState.PairingRequired => (Microsoft.UI.Colors.Orange, "Awaiting Approval"),
                OverallConnectionState.Error => (Microsoft.UI.Colors.Red, "Error"),
                _ => (Microsoft.UI.Colors.Gray, "Disconnected")
            };

            StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            StatusText.Text = text;

            // Status card accent tint — only tint for states that need attention
            StatusCard.Background = snapshot.OverallState switch
            {
                OverallConnectionState.Error =>
                    (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"],
                OverallConnectionState.PairingRequired =>
                    (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"],
                _ => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
            };
        }

        var isConnected = snapshot.OverallState is OverallConnectionState.Connected or OverallConnectionState.Ready or OverallConnectionState.Degraded;
        var isPairing = snapshot.OverallState == OverallConnectionState.PairingRequired;
        var isConnecting = snapshot.OverallState == OverallConnectionState.Connecting;
        ReconnectButton.IsEnabled = !isConnecting && !isPairing;
        ReconnectButton.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;

        if (isConnecting)
        {
            _connectionAttempts++;
            ConnectionAttemptsText.Text = $"Connection attempt {_connectionAttempts}…";
            ConnectionAttemptsText.Opacity = 1;
        }
        else
        {
            if (isConnected || isPairing) _connectionAttempts = 0;
            ConnectionAttemptsText.Opacity = 0;
        }

        // Gateway details
        var self = _hub?.LastGatewaySelf;
        var effectiveUrl = _hub?.Settings?.GetEffectiveGatewayUrl() ?? "";
        if (self != null && isConnected)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(self.ServerVersion))
                parts.Add($"v{self.ServerVersion}");
            parts.Add($"Up {self.UptimeText}");
            GatewayDetailText.Text = string.Join(" · ", parts);
            var authLabel = string.IsNullOrWhiteSpace(self.AuthMode) ? "" : $" · {self.AuthMode} auth";
            GatewayUrlDetail.Text = $"{SanitizeUrl(effectiveUrl)}{authLabel}";
        }
        else
        {
            GatewayDetailText.Text = "";
            GatewayUrlDetail.Text = !string.IsNullOrEmpty(effectiveUrl) ? SanitizeUrl(effectiveUrl) : "";
        }

        // Role status rows
        UpdateRoleStatus(snapshot);

        // Pairing guidance
        UpdatePairingGuidance(snapshot);

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

    private void UpdateRoleStatus(GatewayConnectionSnapshot snapshot)
    {
        // Operator
        var (opColor, opText) = snapshot.OperatorState switch
        {
            RoleConnectionState.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            RoleConnectionState.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            RoleConnectionState.PairingRequired => (Microsoft.UI.Colors.Orange, "Awaiting Approval"),
            RoleConnectionState.PairingRejected => (Microsoft.UI.Colors.Red, "Pairing Rejected"),
            RoleConnectionState.Error => (Microsoft.UI.Colors.Red, "Error"),
            RoleConnectionState.RateLimited => (Microsoft.UI.Colors.Red, "Rate Limited"),
            _ => (Microsoft.UI.Colors.Gray, "Off")
        };
        OperatorStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(opColor);
        OperatorStatusLabel.Text = opText;
        OperatorAccentCard.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(opColor);
        OperatorDetailText.Text = snapshot.OperatorState switch
        {
            RoleConnectionState.Connected => snapshot.OperatorDeviceId != null ? $"device={snapshot.OperatorDeviceId}" : "",
            RoleConnectionState.Error => snapshot.OperatorError ?? "",
            _ => ""
        };

        // Node
        var (nodeColor, nodeText) = snapshot.NodeState switch
        {
            RoleConnectionState.Connected => (Microsoft.UI.Colors.LimeGreen, "Connected"),
            RoleConnectionState.Connecting => (Microsoft.UI.Colors.Orange, "Connecting…"),
            RoleConnectionState.PairingRequired => (Microsoft.UI.Colors.Orange, "Awaiting Approval"),
            RoleConnectionState.PairingRejected => (Microsoft.UI.Colors.Red, "Pairing Rejected"),
            RoleConnectionState.Error => (Microsoft.UI.Colors.Red, "Error"),
            RoleConnectionState.RateLimited => (Microsoft.UI.Colors.Red, "Rate Limited"),
            RoleConnectionState.Disabled => (Microsoft.UI.Colors.Gray, "Disabled"),
            _ => (Microsoft.UI.Colors.Gray, "Off")
        };
        NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(nodeColor);
        NodeStatusLabel.Text = nodeText;
        NodeAccentCard.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(nodeColor);
        NodeDetailText.Text = snapshot.NodeState switch
        {
            RoleConnectionState.Connected => snapshot.NodeDeviceId != null ? $"device={snapshot.NodeDeviceId}" : "",
            RoleConnectionState.Error => snapshot.NodeError ?? "",
            _ => ""
        };
    }

    private void UpdatePairingGuidance(GatewayConnectionSnapshot snapshot)
    {
        // Get device ID from snapshot or from the identity file
        var deviceId = snapshot.OperatorDeviceId ?? snapshot.NodeDeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            // Try reading from identity file
            try
            {
                var activeGw = _gatewayRegistry?.GetActive();
                if (activeGw != null && _gatewayRegistry != null)
                {
                    var idDir = _gatewayRegistry.GetIdentityDirectory(activeGw.Id);
                    var keyPath = System.IO.Path.Combine(idDir, "device-key-ed25519.json");
                    if (System.IO.File.Exists(keyPath))
                    {
                        var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(keyPath));
                        if (json.RootElement.TryGetProperty("DeviceId", out var did))
                            deviceId = did.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionPage] Failed to read device ID from identity file: {ex.Message}");
            }
        }

        if (snapshot.OperatorState == RoleConnectionState.PairingRequired)
        {
            PairingGuidanceCard.Visibility = Visibility.Visible;
            PairingGuidanceText.Text = "🔐 Operator: Awaiting approval from gateway";
            PairingApproveCommandText.Text = !string.IsNullOrEmpty(deviceId)
                ? $"openclaw devices approve {deviceId}"
                : "openclaw devices approve <deviceId>";
        }
        else if (snapshot.NodeState == RoleConnectionState.PairingRequired)
        {
            PairingGuidanceCard.Visibility = Visibility.Visible;
            PairingGuidanceText.Text = "🔐 Node: Awaiting approval from gateway";
            PairingApproveCommandText.Text = !string.IsNullOrEmpty(deviceId)
                ? $"openclaw devices approve {deviceId}"
                : "openclaw devices approve <deviceId>";
        }
        else
        {
            PairingGuidanceCard.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCopyApproveCommand(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(PairingApproveCommandText.Text);
    }

    private void OnReconnectAfterApproval(object sender, RoutedEventArgs e)
    {
        _connectionAttempts = 0;
        _ = _connectionManager?.ReconnectAsync();
    }

    /// <summary>
    /// Called by HubWindow when device pairing list updates arrive.
    /// Renders pending pairing request cards with scope-gated Approve/Reject buttons.
    /// </summary>
    public void UpdateDevicePairingRequests(DevicePairingListInfo data)
    {
        DevicePairingListPanel.Children.Clear();
        if (data.Pending.Count == 0)
        {
            DevicePairingCard.Visibility = Visibility.Collapsed;
            return;
        }
        DevicePairingCard.Visibility = Visibility.Visible;

        // Check if operator has scope to approve/reject
        var scopes = _hub?.GatewayClient?.GrantedOperatorScopes ?? (IReadOnlyList<string>)Array.Empty<string>();
        var canPair = scopes.Any(s =>
            s.Equals("operator.admin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("operator.pairing", StringComparison.OrdinalIgnoreCase));

        foreach (var req in data.Pending)
        {
            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (canPair)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text = req.DisplayName ?? req.DeviceId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var detail = $"{req.Platform ?? "unknown"}";
            if (!string.IsNullOrEmpty(req.Role)) detail += $" · {req.Role}";
            info.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            });
            if (req.Scopes is { Length: > 0 })
            {
                info.Children.Add(new TextBlock
                {
                    Text = $"Scopes: {string.Join(", ", req.Scopes)}",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            if (canPair)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                var rejectBtn = new Button { Content = "Reject" };
                var capturedId = req.RequestId;

                approveBtn.Click += async (s, ev) =>
                {
                    approveBtn.IsEnabled = false;
                    rejectBtn.IsEnabled = false;
                    try
                    {
                        var client = _hub?.GatewayClient;
                        if (client != null)
                        {
                            var ok = await client.DevicePairApproveAsync(capturedId);
                            if (ok)
                                await client.RequestDevicePairListAsync();
                            else
                            {
                                approveBtn.IsEnabled = true;
                                rejectBtn.IsEnabled = true;
                            }
                        }
                        else
                        {
                            approveBtn.IsEnabled = true;
                            rejectBtn.IsEnabled = true;
                        }
                    }
                    catch
                    {
                        approveBtn.IsEnabled = true;
                        rejectBtn.IsEnabled = true;
                    }
                };
                rejectBtn.Click += async (s, ev) =>
                {
                    approveBtn.IsEnabled = false;
                    rejectBtn.IsEnabled = false;
                    try
                    {
                        var client = _hub?.GatewayClient;
                        if (client != null)
                        {
                            var ok = await client.DevicePairRejectAsync(capturedId);
                            if (ok)
                                await client.RequestDevicePairListAsync();
                            else
                            {
                                approveBtn.IsEnabled = true;
                                rejectBtn.IsEnabled = true;
                            }
                        }
                        else
                        {
                            approveBtn.IsEnabled = true;
                            rejectBtn.IsEnabled = true;
                        }
                    }
                    catch
                    {
                        approveBtn.IsEnabled = true;
                        rejectBtn.IsEnabled = true;
                    }
                };

                buttons.Children.Add(approveBtn);
                buttons.Children.Add(rejectBtn);
                Grid.SetColumn(buttons, 1);
                grid.Children.Add(buttons);
            }

            card.Child = grid;
            DevicePairingListPanel.Children.Add(card);
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
        // SSH details are now inside the Expander — toggle just saves state
        var settings = _hub?.Settings;
        if (settings != null)
        {
            settings.UseSshTunnel = SshToggle.IsOn;
            settings.Save();
        }
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle) return;
        var settings = _hub?.Settings;
        if (settings == null) return;
        settings.EnableNodeMode = NodeModeToggle.IsOn;
        settings.Save();
        _hub?.RaiseSettingsSaved();
    }

    private async void OnDirectConnect(object sender, RoutedEventArgs e)
    {
        if (_connectionManager == null || _gatewayRegistry == null) return;

        var url = GatewayUrlTextBox.Text?.Trim();
        var token = TokenTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            DirectConnectResultText.Text = "Enter a gateway URL";
            return;
        }

        url = GatewayUrlHelper.NormalizeForWebSocket(url);

        // Validate SSH config upfront before mutating any state
        var useSsh = SshToggle.IsOn;
        SshTunnelConfig? sshConfig = null;
        if (useSsh)
        {
            var sshUser = SshUserBox.Text.Trim();
            var sshHost = SshHostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(sshUser) || string.IsNullOrWhiteSpace(sshHost))
            {
                DirectConnectResultText.Text = "SSH user and host are required";
                return;
            }
            if (!int.TryParse(SshRemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535)
            {
                DirectConnectResultText.Text = "SSH remote port must be 1–65535";
                return;
            }
            if (!int.TryParse(SshLocalPortBox.Text, out var localPort) || localPort is < 1 or > 65535)
            {
                DirectConnectResultText.Text = "SSH local port must be 1–65535";
                return;
            }
            sshConfig = new SshTunnelConfig(sshUser, sshHost, remotePort, localPort);
        }

        DirectConnectResultText.Text = "Connecting…";

        // Snapshot previous state for rollback
        var previousActiveId = _gatewayRegistry.ActiveGatewayId;
        var previousSettings = _hub?.Settings;
        var prevGatewayUrl = previousSettings?.GatewayUrl;
        var prevUseSsh = previousSettings?.UseSshTunnel ?? false;
        var prevSshUser = previousSettings?.SshTunnelUser;
        var prevSshHost = previousSettings?.SshTunnelHost;
        var prevSshRemotePort = previousSettings?.SshTunnelRemotePort ?? 0;
        var prevSshLocalPort = previousSettings?.SshTunnelLocalPort ?? 0;

        var existing = _gatewayRegistry.FindByUrl(url);
        var isNewRecord = existing == null;
        var existingRecordSnapshot = existing;
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();

        try
        {
            await _connectionManager.DisconnectAsync();

            // Create/update gateway record with shared token + SSH config
            var record = new GatewayRecord
            {
                Id = recordId,
                Url = url,
                SharedGatewayToken = string.IsNullOrWhiteSpace(token) ? null : token,
                BootstrapToken = null,
                SshTunnel = sshConfig,
            };
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
            _gatewayRegistry.Save();

            // Clear stored device tokens so the shared token is used
            var identityDir = _gatewayRegistry.GetIdentityDirectory(recordId);
            var keyPath = System.IO.Path.Combine(identityDir, "device-key-ed25519.json");
            if (System.IO.File.Exists(keyPath))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(keyPath));
                    using var ms = new System.IO.MemoryStream();
                    using var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true });
                    writer.WriteStartObject();
                    foreach (var prop in json.RootElement.EnumerateObject())
                    {
                        if (prop.Name is "DeviceToken" or "DeviceTokenScopes" or "NodeDeviceToken" or "NodeDeviceTokenScopes")
                            continue;
                        prop.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    writer.Flush();
                    System.IO.File.WriteAllBytes(keyPath, ms.ToArray());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConnectionPage] Failed to clear device tokens: {ex.Message}");
                }
            }

            // Save settings (SSH config + gateway URL for legacy compat)
            if (previousSettings != null)
            {
                previousSettings.GatewayUrl = url;
                previousSettings.UseSshTunnel = useSsh;
                if (useSsh && sshConfig != null)
                {
                    previousSettings.SshTunnelUser = sshConfig.User;
                    previousSettings.SshTunnelHost = sshConfig.Host;
                    previousSettings.SshTunnelRemotePort = sshConfig.RemotePort;
                    previousSettings.SshTunnelLocalPort = sshConfig.LocalPort;
                }
                previousSettings.Save();
            }

            // Start SSH tunnel if configured
            if (useSsh)
            {
                DirectConnectResultText.Text = "Starting SSH tunnel…";
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                app.EnsureSshTunnelStarted();
            }

            await _connectionManager.ConnectAsync(recordId);

            // Poll connection manager state — ConnectAsync fires connect asynchronously,
            // so we need to wait for a definitive result before reporting success/failure.
            bool connected = false;
            bool failed = false;
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(1000);
                var snapshot = _connectionManager.CurrentSnapshot;
                if (snapshot.OverallState is Services.Connection.OverallConnectionState.Connected
                    or Services.Connection.OverallConnectionState.Ready)
                {
                    connected = true;
                    break;
                }
                if (snapshot.OverallState is Services.Connection.OverallConnectionState.Error)
                {
                    failed = true;
                    break;
                }
                if (snapshot.OverallState is Services.Connection.OverallConnectionState.PairingRequired)
                {
                    DirectConnectResultText.Text = $"⏳ Pairing required — approve on gateway";
                    return; // don't rollback, pairing is in progress
                }
            }

            if (connected)
            {
                DirectConnectResultText.Text = $"✓ Connected to {GatewayUrlHelper.SanitizeForDisplay(url)}";
                return;
            }

            // Connection failed or timed out — rollback
            var reason = failed ? "Connection failed" : "Connection timed out";
            DirectConnectResultText.Text = $"✗ {reason}";
            RollbackDirectConnect(previousActiveId, isNewRecord, recordId, existingRecordSnapshot,
                previousSettings, prevGatewayUrl, prevUseSsh, prevSshUser, prevSshHost, prevSshRemotePort, prevSshLocalPort);
        }
        catch (Exception ex)
        {
            DirectConnectResultText.Text = $"✗ {ex.Message}";
            RollbackDirectConnect(previousActiveId, isNewRecord, recordId, existingRecordSnapshot,
                previousSettings, prevGatewayUrl, prevUseSsh, prevSshUser, prevSshHost, prevSshRemotePort, prevSshLocalPort);
        }
    }

    private void RollbackDirectConnect(
        string? previousActiveId, bool isNewRecord, string recordId,
        GatewayRecord? existingRecordSnapshot, SettingsManager? settings,
        string? prevGatewayUrl, bool prevUseSsh, string? prevSshUser,
        string? prevSshHost, int prevSshRemotePort, int prevSshLocalPort)
    {
        if (_gatewayRegistry == null) return;

        // Restore or remove the gateway record
        if (isNewRecord)
            _gatewayRegistry.Remove(recordId);
        else if (existingRecordSnapshot != null)
            _gatewayRegistry.AddOrUpdate(existingRecordSnapshot);

        // Restore active gateway
        if (previousActiveId != null)
            _gatewayRegistry.SetActive(previousActiveId);
        _gatewayRegistry.Save();

        // Restore legacy settings
        if (settings != null)
        {
            settings.GatewayUrl = prevGatewayUrl;
            settings.UseSshTunnel = prevUseSsh;
            settings.SshTunnelUser = prevSshUser;
            settings.SshTunnelHost = prevSshHost;
            settings.SshTunnelRemotePort = prevSshRemotePort;
            settings.SshTunnelLocalPort = prevSshLocalPort;
            settings.Save();
        }
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
            // Fallback: decode and apply via settings (no connection manager available)
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
            RecentGatewaysEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var gateways = _gatewayRegistry.GetAll();
        if (gateways.Count == 0)
        {
            RecentGatewaysEmptyText.Visibility = Visibility.Visible;
            return;
        }

        RecentGatewaysEmptyText.Visibility = Visibility.Collapsed;
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

}

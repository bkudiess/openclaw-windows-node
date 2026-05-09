using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Lightweight node connector that creates and manages a WindowsNodeClient.
/// Capability setup (canvas, screen capture, etc.) is handled by NodeService,
/// which has WinUI dependencies and remains in App.xaml.cs for now.
/// </summary>
public sealed class NodeConnector : INodeConnector
{
    private readonly IOpenClawLogger _logger;
    private WindowsNodeClient? _client;
    private bool _disposed;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;

    public NodeConnector(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _client?.IsConnected ?? false;
    public PairingStatus PairingStatus => _client switch
    {
        null => PairingStatus.Unknown,
        { IsPaired: true } => PairingStatus.Paired,
        { IsPendingApproval: true } => PairingStatus.Pending,
        _ => PairingStatus.Unknown
    };
    public string? NodeDeviceId => _client?.ShortDeviceId;
    public NodeConnectionMode Mode { get; private set; } = NodeConnectionMode.Disabled;

    /// <summary>The underlying node client, for capability registration by NodeService.</summary>
    public WindowsNodeClient? Client => _client;

    public async Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath)
    {
        if (_disposed) return;

        DisconnectInternal();

        Mode = NodeConnectionMode.Gateway;
        _logger.Info($"[NodeConnector] Connecting to {gatewayUrl}");

        _client = new WindowsNodeClient(
            gatewayUrl,
            credential.Token,
            identityPath,
            _logger);

        _client.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
        _client.PairingStatusChanged += (s, e) => PairingStatusChanged?.Invoke(this, e);

        try
        {
            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"[NodeConnector] Connect failed: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        DisconnectInternal();
        return Task.CompletedTask;
    }

    private void DisconnectInternal()
    {
        var old = _client;
        _client = null;
        if (old != null)
        {
            try { old.Dispose(); }
            catch (Exception ex) { _logger.Warn($"[NodeConnector] Dispose error: {ex.Message}"); }
        }
        Mode = NodeConnectionMode.Disabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectInternal();
    }
}

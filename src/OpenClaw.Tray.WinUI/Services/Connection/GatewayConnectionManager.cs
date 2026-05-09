using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// GatewayConnectionManager — single owner of connection lifecycle.
/// Phase 2.1: Shell with state machine, diagnostics, and stub lifecycle methods.
/// Real client creation is added in Step 2.2a.
/// </summary>
public sealed class GatewayConnectionManager : IGatewayConnectionManager
{
    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly ICredentialResolver _credentialResolver;
    private readonly IGatewayClientFactory _clientFactory;
    private readonly GatewayRegistry _registry;
    private readonly IOpenClawLogger _logger;
    private readonly SemaphoreSlim _transitionSemaphore = new(1, 1);

    private long _generation;
    private CancellationTokenSource? _operationCts;
    private IGatewayClientLifecycle? _activeLifecycle;
    private bool _disposed;

    public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
    public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;

    public GatewayConnectionManager(
        ICredentialResolver credentialResolver,
        IGatewayClientFactory clientFactory,
        GatewayRegistry registry,
        IOpenClawLogger logger,
        IClock? clock = null)
    {
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnostics = new ConnectionDiagnostics(clock: clock);
        _diagnostics.EventRecorded += (_, e) => DiagnosticEvent?.Invoke(this, e);
    }

    // ─── State ───

    public GatewayConnectionSnapshot CurrentSnapshot => _stateMachine.Current;
    public string? ActiveGatewayUrl => _stateMachine.Current.GatewayUrl;
    public OpenClawGatewayClient? OperatorClient => _activeLifecycle?.DataClient;
    public ConnectionDiagnostics Diagnostics => _diagnostics;

    // ─── Lifecycle ───

    public async Task ConnectAsync(string? gatewayId = null)
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            var id = gatewayId ?? _registry.ActiveGatewayId;
            if (id == null)
            {
                _logger.Warn("[ConnMgr] No gateway ID specified and no active gateway");
                return;
            }

            var record = _registry.GetById(id);
            if (record == null)
            {
                _logger.Warn($"[ConnMgr] Gateway {id} not found in registry");
                return;
            }

            // Cancel any in-flight operation
            var gen = Interlocked.Increment(ref _generation);
            var oldCts = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
            oldCts?.Cancel();
            oldCts?.Dispose();

            // Dispose old client
            DisposeActiveClient();

            // Update snapshot with gateway info
            _stateMachine.Current = _stateMachine.Current with
            {
                GatewayId = record.Id,
                GatewayUrl = record.Url,
                GatewayName = record.FriendlyName
            };

            // Resolve credentials
            var identityPath = _registry.GetIdentityDirectory(record.Id);
            var credential = _credentialResolver.ResolveOperator(record, identityPath);
            _diagnostics.RecordCredentialResolution(credential);

            if (credential == null)
            {
                _logger.Warn("[ConnMgr] No credential available for gateway");
                var prev = _stateMachine.Current.OverallState;
                // Must go through Connecting → Error since AuthenticationFailed requires Connecting state
                _stateMachine.TryTransition(ConnectionTrigger.ConnectRequested);
                _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, "No credential available");
                EmitStateChanged(prev);
                return;
            }

            // Transition to Connecting
            var prevState = _stateMachine.Current.OverallState;
            if (!_stateMachine.TryTransition(ConnectionTrigger.ConnectRequested))
            {
                _logger.Warn($"[ConnMgr] Cannot connect from state {_stateMachine.Current.OperatorState}");
                return;
            }
            _diagnostics.RecordStateChange(prevState, _stateMachine.Current.OverallState);
            EmitStateChanged(prevState);

            // Create client via factory
            var lifecycle = _clientFactory.Create(record.Url, credential, identityPath, _logger);
            _activeLifecycle = lifecycle;

            // Subscribe to client events with generation guard
            lifecycle.StatusChanged += (s, status) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleOperatorStatusChangedAsync(status, gen);
            };
            lifecycle.AuthenticationFailed += (s, msg) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleAuthenticationFailedAsync(msg, gen);
            };
            lifecycle.DataClient.HandshakeSucceeded += (s, e) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                _ = HandleHandshakeSucceededAsync(gen);
            };
            lifecycle.DataClient.DeviceTokenReceived += (s, e) =>
            {
                if (Interlocked.Read(ref _generation) != gen) return;
                HandleDeviceTokenReceived(e);
            };

            // Connect (fire and forget — the event handlers will drive state transitions)
            var ct = _operationCts!.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await lifecycle.ConnectAsync(ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"[ConnMgr] Connect failed: {ex.Message}");
                }
            }, ct);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        ThrowIfDisposed();
        await _transitionSemaphore.WaitAsync();
        try
        {
            var prev = _stateMachine.Current.OverallState;
            DisposeActiveClient();
            _stateMachine.TryTransition(ConnectionTrigger.DisconnectRequested);
            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task ReconnectAsync()
    {
        await DisconnectAsync();
        await ConnectAsync();
    }

    public async Task SwitchGatewayAsync(string gatewayId)
    {
        await DisconnectAsync();
        _registry.SetActive(gatewayId);
        await ConnectAsync(gatewayId);
    }

    public Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode)
    {
        // Stub — implemented in Step 3.1
        return Task.FromResult(new SetupCodeResult(SetupCodeOutcome.InvalidCode, "Not yet implemented"));
    }

    // ─── Event Handlers ───

    private async Task HandleOperatorStatusChangedAsync(ConnectionStatus status, long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            switch (status)
            {
                case ConnectionStatus.Connected:
                    _diagnostics.RecordWebSocketEvent("WebSocket connected");
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketConnected);
                    break;
                case ConnectionStatus.Disconnected:
                    _diagnostics.RecordWebSocketEvent("WebSocket disconnected");
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketDisconnected);
                    break;
                case ConnectionStatus.Error:
                    _diagnostics.RecordWebSocketEvent("WebSocket error");
                    _stateMachine.TryTransition(ConnectionTrigger.WebSocketError, "Transport error");
                    break;
                case ConnectionStatus.Connecting:
                    _diagnostics.RecordWebSocketEvent("WebSocket connecting");
                    break;
            }
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task HandleAuthenticationFailedAsync(string message, long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("error", "Authentication failed", message);
            _stateMachine.TryTransition(ConnectionTrigger.AuthenticationFailed, message);
            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async Task HandleHandshakeSucceededAsync(long gen)
    {
        await _transitionSemaphore.WaitAsync();
        try
        {
            if (Interlocked.Read(ref _generation) != gen) return;

            var prev = _stateMachine.Current.OverallState;
            _diagnostics.Record("state", "Handshake succeeded (hello-ok)");
            _stateMachine.TryTransition(ConnectionTrigger.HandshakeSucceeded);
            _diagnostics.RecordStateChange(prev, _stateMachine.Current.OverallState);

            // Update device ID from client
            if (_activeLifecycle?.DataClient is { } client)
            {
                _stateMachine.Current = _stateMachine.Current with
                {
                    OperatorDeviceId = client.OperatorDeviceId
                };
            }

            EmitStateChanged(prev);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private void HandleDeviceTokenReceived(DeviceTokenReceivedEventArgs e)
    {
        _diagnostics.Record("credential", $"Device token received for {e.Role}",
            $"Scopes={string.Join(",", e.Scopes ?? [])}");
    }

    // ─── Helpers ───

    private void EmitStateChanged(OverallConnectionState previousOverall)
    {
        var snapshot = _stateMachine.Current;
        if (snapshot.OverallState != previousOverall || snapshot != _stateMachine.Current)
        {
            StateChanged?.Invoke(this, snapshot);
        }
    }

    private void DisposeActiveClient()
    {
        var old = _activeLifecycle;
        _activeLifecycle = null;
        old?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stateMachine.TryTransition(ConnectionTrigger.Disposed);
        DisposeActiveClient();
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _transitionSemaphore.Dispose();
    }
}

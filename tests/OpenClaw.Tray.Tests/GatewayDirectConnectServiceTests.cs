using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayDirectConnectServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "openclaw-direct-connect-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayRegistry _registry;
    private readonly SettingsManager _settings;
    private readonly FakeConnectionManager _manager = new();
    private int _tunnelReconcileCount;

    public GatewayDirectConnectServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _settings = new SettingsManager(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Connect_NewRecord_CommitsRegistrySettingsAndRuntimeTunnel()
    {
        _manager.NextSnapshot = Connected("gw-new");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://gateway.example",
            SharedToken: null,
            FriendlyName: "Remote",
            SshTunnel: null));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var record = Assert.Single(_registry.GetAll());
        Assert.Equal(record.Id, _registry.ActiveGatewayId);
        Assert.Equal("Remote", record.FriendlyName);
        Assert.Equal("wss://gateway.example", _settings.GatewayUrl);
        Assert.False(_settings.UseSshTunnel);
        Assert.Equal(1, _tunnelReconcileCount);
        Assert.Equal(1, _manager.LeaseCount);
        Assert.Equal(1, _manager.ConnectCount);
    }

    [Fact]
    public async Task Connect_Failure_RestoresRegistrySettingsAndIdentity()
    {
        var previous = AddPreviousGateway();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-old");
        identity.StoreDeviceTokenForRole("node", "node-old");
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: "replacement-token",
            FriendlyName: "Replacement",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.False(result.GatewayCommitted);
        var restored = Assert.IsType<GatewayRecord>(_registry.GetById(previous.Id));
        Assert.Equal(previous.Url, restored.Url);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
        Assert.Equal(previous.Url, _settings.GatewayUrl);
        Assert.Equal(
            "operator-old",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
        Assert.Equal(
            "node-old",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "node"));
        Assert.Equal(2, _tunnelReconcileCount);
    }

    [Fact]
    public async Task Connect_LatePairingWriterWinsOverRollback()
    {
        var previous = AddPreviousGateway();
        var identity = CreateIdentity(previous.Id);
        identity.StoreDeviceTokenForRole("operator", "operator-old");
        _manager.BeforeSnapshot = () =>
        {
            var lateWriter = new DeviceIdentity(_registry.GetIdentityDirectory(previous.Id));
            lateWriter.Initialize();
            lateWriter.StoreDeviceTokenForRole("operator", "operator-new");
        };
        _manager.NextSnapshot = Failed(previous.Id, "rejected");
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: "replacement-token",
            FriendlyName: null,
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(
            "operator-new",
            DeviceIdentity.TryReadStoredDeviceTokenForRole(
                _registry.GetIdentityDirectory(previous.Id),
                "operator"));
    }

    [Fact]
    public async Task Connect_EditingGatewayId_UpdatesInPlaceWithoutDuplicate()
    {
        var previous = AddPreviousGateway();
        _manager.NextSnapshot = Connected(previous.Id);
        var service = CreateService();

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://updated.example",
            SharedToken: null,
            FriendlyName: "Updated",
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Connected, result.Outcome);
        var record = Assert.Single(_registry.GetAll());
        Assert.Equal(previous.Id, record.Id);
        Assert.Equal("wss://updated.example", record.Url);
        Assert.Equal("Updated", record.FriendlyName);
    }

    [Fact]
    public async Task Connect_SettingsSaveFailure_RollsBackBeforeConnecting()
    {
        var previous = AddPreviousGateway();
        var blockedPath = Path.Combine(_tempDir, "blocked-settings");
        File.WriteAllText(blockedPath, "not a directory");
        var blockedSettings = new SettingsManager(blockedPath);
        var service = new GatewayDirectConnectService(
            _manager,
            _registry,
            blockedSettings,
            () => _tunnelReconcileCount++,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100));

        var result = await service.ConnectAsync(new GatewayDirectConnectRequest(
            "wss://replacement.example",
            SharedToken: null,
            FriendlyName: null,
            SshTunnel: null,
            EditingGatewayId: previous.Id));

        Assert.Equal(GatewayDirectConnectOutcome.Failed, result.Outcome);
        Assert.Equal(0, _manager.ConnectCount);
        Assert.Equal(previous.Url, _registry.GetById(previous.Id)?.Url);
        Assert.Equal(previous.Id, _registry.ActiveGatewayId);
    }

    private GatewayDirectConnectService CreateService() =>
        new(
            _manager,
            _registry,
            _settings,
            () => _tunnelReconcileCount++,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(250));

    private GatewayRecord AddPreviousGateway()
    {
        var previous = new GatewayRecord
        {
            Id = "gw-previous",
            Url = "wss://previous.example",
            FriendlyName = "Previous",
        };
        _registry.AddOrUpdate(previous);
        _registry.SetActive(previous.Id);
        _registry.Save();
        _settings.GatewayUrl = previous.Url;
        _settings.UseSshTunnel = false;
        _settings.SaveOrThrow();
        return previous;
    }

    private DeviceIdentity CreateIdentity(string gatewayId)
    {
        var identity = new DeviceIdentity(_registry.GetIdentityDirectory(gatewayId));
        identity.Initialize();
        return identity;
    }

    private static GatewayConnectionSnapshot Connected(string gatewayId) =>
        new()
        {
            GatewayId = gatewayId,
            GatewayUrl = "wss://connected.example",
            OverallState = OverallConnectionState.Ready,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
            NodePairingStatus = PairingStatus.Paired,
        };

    private static GatewayConnectionSnapshot Failed(string gatewayId, string error) =>
        new()
        {
            GatewayId = gatewayId,
            GatewayUrl = "wss://failed.example",
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorError = error,
        };

    private sealed class FakeConnectionManager : IGatewayConnectionManager
    {
        public GatewayConnectionSnapshot CurrentSnapshot { get; private set; } =
            GatewayConnectionSnapshot.Idle;
        public string? ActiveGatewayUrl => CurrentSnapshot.GatewayUrl;
        public IOperatorGatewayClient? OperatorClient => null;
        public ConnectionDiagnostics Diagnostics { get; } = new();
        public bool IsManualGatewayLifecycleInProgress => false;
        public int LeaseCount { get; private set; }
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public GatewayConnectionSnapshot NextSnapshot { get; set; } =
            GatewayConnectionSnapshot.Idle;
        public Action? BeforeSnapshot { get; set; }

        public event EventHandler<GatewayConnectionSnapshot>? StateChanged;
#pragma warning disable CS0067
        public event EventHandler<ConnectionDiagnosticEvent>? DiagnosticEvent;
        public event EventHandler<OperatorClientChangedEventArgs>? OperatorClientChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(string? gatewayId = null)
        {
            ConnectCount++;
            BeforeSnapshot?.Invoke();
            CurrentSnapshot = NextSnapshot with
            {
                GatewayId = gatewayId ?? NextSnapshot.GatewayId
            };
            StateChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            DisconnectCount++;
            CurrentSnapshot = GatewayConnectionSnapshot.Idle;
            StateChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }

        public Task<IDisposable> BeginManualGatewayLifecycleOperationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseCount++;
            return Task.FromResult<IDisposable>(new Scope());
        }

        public Task ConnectNodeOnlyAsync(string? gatewayId = null) => Task.CompletedTask;
        public Task DisconnectByUserAsync() => DisconnectAsync();
        public Task ReconnectAsync() => Task.CompletedTask;
        public Task<bool> ReconnectIfCurrentAsync(
            string gatewayId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RecoverSshTunnelAsync(SshTunnelExit tunnelExit) => Task.FromResult(false);
        public Task SwitchGatewayAsync(string gatewayId) => Task.CompletedTask;
        public void SetGatewayConnectionIntent(string gatewayId, bool shouldBeConnected) { }
        public bool IsAutomaticReconnectAllowed(string gatewayId) => true;
        public Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<SetupCodeResult> ApplySetupCodeAsync(
            string setupCode,
            SshTunnelConfig? sshTunnel = null) =>
            Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        public Task<SetupCodeResult> ConnectWithSharedTokenAsync(
            string gatewayUrl,
            string token,
            SshTunnelConfig? sshTunnel = null) =>
            Task.FromResult(new SetupCodeResult(SetupCodeOutcome.Success));
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}

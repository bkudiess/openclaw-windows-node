using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClawTray.Services.LocalGatewaySetup;
using ConnectionManagerWindowsNodeConnector = OpenClawTray.Services.ConnectionManagerWindowsNodeConnector;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for ConnectionManagerWindowsNodeConnector — the IWindowsNodeConnector
/// implementation used by the easy-button setup engine to drive node pairing
/// via the manager (instead of the legacy NodeServiceWindowsNodeConnector path).
/// </summary>
public sealed class ConnectionManagerWindowsNodeConnectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GatewayRegistry _registry;
    private readonly StubCredentialResolver _resolver = new();
    private readonly StubClientFactory _factory = new();
    private readonly StubNodeConnector _node = new();
    private readonly GatewayConnectionManager _manager;

    public ConnectionManagerWindowsNodeConnectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-cmwnc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _registry = new GatewayRegistry(_tempDir);
        _manager = new GatewayConnectionManager(
            _resolver, _factory, _registry, NullLogger.Instance,
            nodeConnector: _node);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ConnectAsync_RecordExists_PatchesTokensAndDrivesNodeConnect()
    {
        const string url = "ws://localhost:18789";
        var record = new GatewayRecord { Id = "gw-local", Url = url, IsLocal = true };
        _registry.AddOrUpdate(record);
        _registry.SetActive("gw-local");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        // Drive operator to Connected
        await _manager.ConnectAsync("gw-local");
        await InvokeHandshakeSucceededAsync(_manager);

        var connector = new ConnectionManagerWindowsNodeConnector(
            _manager, _registry, NullLogger.Instance);

        await connector.ConnectAsync(url, "shared-token", "boot-token");

        var updated = _registry.FindByUrl(url);
        Assert.NotNull(updated);
        Assert.Equal("shared-token", updated!.SharedGatewayToken);
        Assert.Equal("boot-token", updated.BootstrapToken);
        Assert.Equal(1, _node.ConnectCount);
    }

    [Fact]
    public async Task ConnectAsync_NoExistingRecord_CreatesOne()
    {
        const string url = "ws://localhost:18789";
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");

        // Pre-create gateway record so the manager can connect; the connector's job
        // here is to ensure that even WITHOUT a record matching the URL, it creates one.
        // To exercise that branch we point the manager at a different record.
        var seedRecord = new GatewayRecord { Id = "seed", Url = "wss://other", IsLocal = false };
        _registry.AddOrUpdate(seedRecord);
        _registry.SetActive("seed");
        await _manager.ConnectAsync("seed");
        await InvokeHandshakeSucceededAsync(_manager);

        var connector = new ConnectionManagerWindowsNodeConnector(
            _manager, _registry, NullLogger.Instance);

        // The connector will create a new record for `url` if none exists. Manager's
        // EnsureNodeConnectedAsync uses the currently-active gateway, which is `seed`.
        // The new record creation is independent of the active gateway.
        await connector.ConnectAsync(url, "shared", bootstrapToken: null);

        var created = _registry.FindByUrl(url);
        Assert.NotNull(created);
        Assert.Equal("shared", created!.SharedGatewayToken);
        Assert.True(created.IsLocal);
    }

    [Fact]
    public async Task ConnectAsync_OperatorNotConnected_PropagatesInvalidOperationException()
    {
        const string url = "ws://localhost:18789";
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-local", Url = url, IsLocal = true });
        _registry.SetActive("gw-local");

        var connector = new ConnectionManagerWindowsNodeConnector(
            _manager, _registry, NullLogger.Instance);

        // Operator never reaches Connected; EnsureNodeConnectedAsync throws.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connector.ConnectAsync(url, "tok", null));
    }

    [Fact]
    public async Task ConnectAsync_PreCancelledToken_Throws()
    {
        const string url = "ws://localhost:18789";
        _registry.AddOrUpdate(new GatewayRecord { Id = "gw-local", Url = url, IsLocal = true });
        _registry.SetActive("gw-local");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");

        var connector = new ConnectionManagerWindowsNodeConnector(
            _manager, _registry, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => connector.ConnectAsync(url, "tok", null, cts.Token));
    }

    [Fact]
    public async Task ConnectAsync_PreservesExistingTokens_WhenEmptyValuesPassed()
    {
        const string url = "ws://localhost:18789";
        var record = new GatewayRecord
        {
            Id = "gw-local",
            Url = url,
            IsLocal = true,
            SharedGatewayToken = "preserved-shared",
            BootstrapToken = "preserved-boot",
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive("gw-local");
        _resolver.OperatorCredential = new GatewayCredential("op", false, "test");
        _resolver.NodeCredential = new GatewayCredential("nd", false, "test");
        await _manager.ConnectAsync("gw-local");
        await InvokeHandshakeSucceededAsync(_manager);

        var connector = new ConnectionManagerWindowsNodeConnector(
            _manager, _registry, NullLogger.Instance);

        // Caller supplies empty token values — must NOT overwrite the stored values.
        await connector.ConnectAsync(url, token: "", bootstrapToken: "");

        var after = _registry.FindByUrl(url)!;
        Assert.Equal("preserved-shared", after.SharedGatewayToken);
        Assert.Equal("preserved-boot", after.BootstrapToken);
    }

    // ─── Helpers / mocks ───

    private static async Task InvokeHandshakeSucceededAsync(GatewayConnectionManager manager)
    {
        var method = typeof(GatewayConnectionManager).GetMethod(
            "HandleHandshakeSucceededAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(manager, [1L])!;
        await task;
    }

    private sealed class StubCredentialResolver : ICredentialResolver
    {
        public GatewayCredential? OperatorCredential { get; set; }
        public GatewayCredential? NodeCredential { get; set; }

        public GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath) => OperatorCredential;
        public GatewayCredential? ResolveNode(GatewayRecord record, string identityPath) => NodeCredential;
    }

    private sealed class StubClientFactory : IGatewayClientFactory
    {
        public IGatewayClientLifecycle Create(string gatewayUrl, GatewayCredential credential, string identityPath, IOpenClawLogger logger)
            => new StubLifecycle(gatewayUrl);
    }

    private sealed class StubLifecycle : IGatewayClientLifecycle
    {
        public StubLifecycle(string url)
        {
            DataClient = new StubGatewayClient(url);
        }

        public OpenClawGatewayClient DataClient { get; }
#pragma warning disable CS0067 // Events are required by interface but not exercised
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class StubGatewayClient : OpenClawGatewayClient
    {
        public StubGatewayClient(string url) : base(url, "stub-tok", NullLogger.Instance) { }
    }

    private sealed class StubNodeConnector : INodeConnector
    {
        public int ConnectCount { get; private set; }
        public bool IsConnected { get; private set; }
        public PairingStatus PairingStatus { get; private set; } = PairingStatus.Unknown;
        public string? NodeDeviceId => "stub-node";
        public NodeConnectionMode Mode => IsConnected ? NodeConnectionMode.Gateway : NodeConnectionMode.Disabled;

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
#pragma warning disable CS0067
        public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
#pragma warning restore CS0067

        public Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
        {
            ConnectCount++;
            // Drive synchronous transitions so the manager reaches Connected+Paired and
            // EnsureNodeConnectedAsync returns rather than waiting on the 35s timeout.
            IsConnected = true;
            StatusChanged?.Invoke(this, ConnectionStatus.Connecting);
            PairingStatus = PairingStatus.Paired;
            PairingStatusChanged?.Invoke(this, new PairingStatusEventArgs(PairingStatus.Paired, deviceId: "stub-node"));
            StatusChanged?.Invoke(this, ConnectionStatus.Connected);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}

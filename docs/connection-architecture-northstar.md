# Connection Architecture North Star

> **Status**: Reference Architecture — guides all connection-layer refactoring  
> **Scope**: OpenClaw Windows Node tray application (`OpenClaw.Tray.WinUI`, `OpenClaw.Shared`)  
> **Companion**: `connection-architecture.md` (original findings and recommendations)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current Architecture](#2-current-architecture)
3. [North Star Architecture](#3-north-star-architecture)
4. [Component Specifications](#4-component-specifications)
5. [Data Model](#5-data-model)
6. [State Machine](#6-state-machine)
7. [Event Architecture](#7-event-architecture)
8. [Credential Resolution](#8-credential-resolution)
9. [Setup Code Flow](#9-setup-code-flow)
10. [UI Integration](#10-ui-integration)
11. [Testability](#11-testability)
12. [Migration Plan](#12-migration-plan)
13. [Error Taxonomy & Retry Policy](#13-error-taxonomy--retry-policy)
14. [Event Ownership & Fan-Out](#14-event-ownership--fan-out)
15. [Local Gateway Setup Integration](#15-local-gateway-setup-integration)
16. [What Stays in App.xaml.cs](#16-what-stays-in-appxamlcs)
17. [Appendix: Component Inventory](#17-appendix-component-inventory)

---

## 1. Executive Summary

### The Problem

The OpenClaw Windows Node application has grown organically around a single god class
(`App.xaml.cs` — 4,808 lines, 63+ private fields, ~52 event subscriptions, 12+
responsibilities). Connection lifecycle — the most complex and critical subsystem — is
scattered across this class, with credential resolution duplicated in 5+ places, setup
code application in 4 paths, token writes in 6+ locations, and gateway client
initialization triggered from 8 call sites.

The result:

- **Fragile**: any change to connection logic requires understanding thousands of lines
  of unrelated code.
- **Untestable**: connection behavior is inextricable from WinUI, the filesystem, and
  real WebSocket connections.
- **Unreliable**: race conditions between pairing events, duplicate event subscriptions,
  and inconsistent state writes cause user-visible bugs (toast storms, flicker, stale UI).
- **Unextendable**: multi-gateway support, connection diagnostics, and UX improvements
  all require reaching into the god class.

### The Vision

A clean separation where:

- **One class** (`GatewayConnectionManager`) owns the entire connection lifecycle for a
  single gateway — operator client, node service, credentials, state machine, diagnostics.
- **One registry** (`GatewayRegistry`) catalogs known gateways as pure data, with no
  runtime state.
- **One path** exists for each operation: credential resolution, setup code application,
  token storage, state transition.
- **App.xaml.cs** becomes a thin composition root and window manager (~500-800 lines).
- **Every connection component** is testable in isolation without WinUI, WebSocket
  connections, or filesystem access.

### Key Principles

| Principle | Implication |
|-----------|-------------|
| Single Responsibility | Each class has one reason to change |
| Single Canonical Path | One code path per operation (resolve creds, apply setup code, store token) |
| Pure Data ≠ Runtime State | `GatewayRegistry` has no `ActiveClient`; `SettingsData` has no connection state |
| Immutable Snapshots | Cross-thread state sharing via `GatewayConnectionSnapshot` records |
| Layered Events | Transport → Service → UI; never skip a level |
| Testable by Default | Every component behind an interface; no WinUI/WebSocket/FS dependency in logic |
| No Circular Dependencies | Strict DAG: Transport → Client → Manager → UI |
| Explicit State Machines | Enums + transition tables replace scattered boolean flags |

---

## 2. Current Architecture

### God Class Structure

```
App.xaml.cs (4,808 lines)
 │
 │  63+ private fields including:
 │    _gatewayClient           (OpenClawGatewayClient — operator connection)
 │    _nodeService              (NodeService — wraps WindowsNodeClient)
 │    _settings                 (SettingsManager)
 │    _sshTunnelService         (SshTunnelService)
 │    _trayIcon                 (TrayIcon)
 │    _currentStatus            (ConnectionStatus — written from 6+ locations)
 │    _currentActivity          (string — activity overlay)
 │    _lastChannels/Sessions/Nodes/SessionPreviews/Usage/...  (13+ cached snapshots)
 │    _localSetupEngine         (local gateway setup)
 │    _globalHotkey             (GlobalHotkeyService)
 │
 │  ~52 event subscriptions wired in OnLaunched:
 │    27 gateway client events  (lines 1686-1711)
 │    7 node service events     (lines 1783-1789, duplicated 1829-1834)
 │    Settings, tray, hotkey, deep link, update, etc.
 │
 │  12+ responsibilities:
 │    1. Connection lifecycle (init, connect, reconnect, disconnect, dispose)
 │    2. Credential resolution (5+ paths with different priority orders)
 │    3. Setup code application (4 entry points)
 │    4. Token/credential storage (6+ write paths)
 │    5. Node service management
 │    6. SSH tunnel management
 │    7. Window management (Hub, Chat, Voice, Onboarding, Canvas)
 │    8. Tray icon + context menu
 │    9. Settings persistence + rebuild-on-save
 │   10. Protocol activation / deep links
 │   11. Toast notifications
 │   12. Update checks
 │   13. Audio/voice/TTS
 │   14. Global hotkeys
 │   15. Gateway discovery (mDNS)
 │
 │  8 call sites for InitializeGatewayClient():
 │    Lines: 94, 423, 1014, 2823, 2965, 3020, 3149, 3644
 │
 └── Passes mutable state to HubWindow:
       Settings, GatewayClient, CurrentStatus, VoiceService,
       7 action delegates (connect, disconnect, reconnect, etc.)
       NodeIsConnected, NodeIsPaired, NodeIsPendingApproval,
       LastAuthError, NodeShortDeviceId, NodeFullDeviceId
```

### Credential Resolution Chaos (5+ Paths)

```
Path 1: App.InitializeGatewayClient()
  settings.Token → settings.BootstrapToken → (skip DeviceIdentity)

Path 2: GatewayCredentialResolver.Resolve()
  settings.Token → settings.BootstrapToken → DeviceIdentity.DeviceToken

Path 3: SetupWizardWindow.OnSetupCodeChanged()
  decode → test connect → write settings → trigger rebuild

Path 4: ConnectionPage.OnApplySetupCode()
  decode → write settings → trigger rebuild

Path 5: TryResolveChatCredentials()
  SharedGatewayToken → OperatorDeviceToken → (different priority)
```

### Token Storage Chaos (6+ Write Paths)

```
1. DeviceIdentity.StoreDeviceToken()     — from gateway hello-ok handshake
2. DeviceIdentity.StoreNodeDeviceToken() — from node hello-ok handshake
3. SettingsManager.Save(Token=...)       — from setup code apply
4. SettingsManager.Save(Bootstrap=...)   — from setup code apply
5. App.OnConnectionStatusChanged()       — writes registry + settings
6. App.OnPairingStatusChanged()          — writes registry + settings
```

### Numbers at a Glance

| Metric | Current | Target |
|--------|---------|--------|
| App.xaml.cs lines | 4,808 | ~500-800 |
| Private fields in App | 63+ | ~15-20 |
| Event subs in App | ~52 | ~5-8 |
| InitializeGatewayClient call sites | 8 | 0 (manager owns) |
| Credential resolution paths | 5+ | 1 |
| Setup code entry points | 4 | 1 |
| Token write paths | 6+ | 1 (through manager) |
| Connection status write sites | 6+ | 1 (state machine) |
| Responsibilities in App | 12+ | 3 (compose, window mgmt, tray) |

---

## 3. North Star Architecture

### High-Level Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│  App.xaml.cs  (composition root + window/tray management)          │
│                                                                     │
│  Creates:                                                           │
│    SettingsManager                                                  │
│    GatewayRegistry                                                  │
│    GatewayConnectionManager ◄── subscribes to StateChanged          │
│                                                                     │
│  Owns windows: HubWindow, ChatWindow, OnboardingWindow, etc.       │
│  Owns: TrayIcon, GlobalHotkeyService, UpdateChecker                │
│  Forwards: manager ref to windows that need connection access       │
└───────────────────┬─────────────────────────────────────────────────┘
                    │ creates + holds
                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│  GatewayConnectionManager  (single owner of connection lifecycle)  │
│                                                                     │
│  State Machine: Idle → Connecting → Connected → Error               │
│                        ↕ PairingRequired                            │
│                                                                     │
│  Owns:                                                              │
│    OperatorClient (via IGatewayClientFactory)                       │
│    NodeConnector  (via INodeConnector)                              │
│    ConnectionDiagnostics                                            │
│                                                                     │
│  Uses (injected):                                                   │
│    IGatewayClientFactory   — creates OpenClawGatewayClient          │
│    INodeConnector          — creates/manages WindowsNodeClient      │
│    ICredentialResolver     — single resolution path                 │
│    ISshTunnelManager       — optional tunnel lifecycle              │
│    GatewayRegistry         — read active gateway record             │
│    IClock                  — testable time                          │
│    IOpenClawLogger         — logging                                │
│                                                                     │
│  Exposes:                                                           │
│    Events: StateChanged, DiagnosticEvent                            │
│    Props:  CurrentSnapshot (immutable), ActiveGatewayUrl            │
│    Methods: ConnectAsync, DisconnectAsync, SwitchGatewayAsync,      │
│             ApplySetupCodeAsync, ReconnectAsync                     │
│                                                                     │
│  Does NOT: touch UI, manage windows, show toasts, manage settings   │
└──────┬──────────────────────────┬───────────────────────────────────┘
       │ creates                  │ creates
       ▼                          ▼
┌──────────────────┐    ┌───────────────────────┐
│ OpenClawGateway  │    │ NodeConnector          │
│ Client           │    │ (wraps WindowsNode     │
│                  │    │  Client + NodeService)  │
│ Operator role    │    │                         │
│ 27 events        │    │ Node role               │
│ 35+ methods      │    │ Pairing, invoke,        │
│ Auth handshake   │    │ capabilities            │
└────────┬─────────┘    └───────────┬─────────────┘
         │                          │
         ▼                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│  WebSocketClientBase  (shared transport: connect, reconnect, send) │
│                                                                     │
│  Events: StatusChanged, AuthenticationFailed                        │
│  Reconnect policy: exponential backoff 1s → 60s                    │
│  Buffer management, cancellation, dispose                           │
└─────────────────────────────────────────────────────────────────────┘
```

### Data Layer (No Runtime State)

```
┌─────────────────────────────────────────────────────────────────────┐
│  GatewayRegistry                                                    │
│  Pure data catalog of known gateways                                │
│  Persists: %APPDATA%/OpenClawTray/gateways.json                    │
│  No ActiveClient, No ActiveConnectionStatus                        │
│  Records: Id, Url, FriendlyName, LastConnected, SshTunnel, IsLocal │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  DeviceIdentity (per gateway, in identity subdirectory)             │
│  Manages: Ed25519 keypair, DeviceToken, NodeDeviceToken             │
│  File: <identity-dir>/device-key-ed25519.json                      │
│  Single write API, single read API                                  │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  SettingsData (app preferences only — no connection state)           │
│  Connection config: GatewayUrl, UseSshTunnel, SshTunnel*            │
│  Node toggles: EnableNodeMode, NodeCanvas/Screen/Camera/etc.        │
│  UI prefs: HubNavPaneOpen, GlobalHotkeyEnabled, etc.                │
│  Audio: TTS/STT settings                                            │
│  Notification rules: NotifyHealth, NotifyUrgent, etc.               │
│  NO: Token, BootstrapToken (migrated to GatewayRecord)              │
└─────────────────────────────────────────────────────────────────────┘
```

### Dependency Graph (DAG — No Cycles)

```
                    IClock, IOpenClawLogger
                         │
         ┌───────────────┼───────────────┐
         ▼               ▼               ▼
  GatewayRegistry  SettingsManager  DeviceIdentity
         │               │               │
         ▼               ▼               ▼
  ICredentialResolver ◄──┘               │
         │                               │
         ▼                               │
  IGatewayClientFactory ◄────────────────┘
         │
         ▼
  GatewayConnectionManager ◄── ISshTunnelManager, INodeConnector
         │
         ▼
  App.xaml.cs (composition root)
         │
         ▼
  HubWindow, ConnectionPage, OnboardingWindow (UI consumers)
```

---

## 4. Component Specifications

### 4.1 GatewayConnectionManager

**Responsibility**: Single owner of the complete connection lifecycle for the active
gateway — operator connection, node connection, credential resolution, state transitions,
and diagnostics.

```csharp
public interface IGatewayConnectionManager : IDisposable
{
    // --- State ---
    GatewayConnectionSnapshot CurrentSnapshot { get; }
    string? ActiveGatewayUrl { get; }

    // --- Events ---
    event EventHandler<GatewayConnectionSnapshot> StateChanged;
    event EventHandler<ConnectionDiagnosticEvent> DiagnosticEvent;

    // --- Lifecycle ---
    Task ConnectAsync(string? gatewayId = null);
    Task DisconnectAsync();
    Task ReconnectAsync();
    Task SwitchGatewayAsync(string gatewayId);

    // --- Setup ---
    Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode);

    // --- Operator Client Passthrough ---
    /// <summary>
    /// Provides read-only access to the operator client for pages that need
    /// to make gateway requests. Returns null when disconnected.
    /// </summary>
    IOperatorGatewayClient? OperatorClient { get; }
}
```

**What it owns** (creates, manages lifecycle of):
- `OpenClawGatewayClient` instance (via `IGatewayClientFactory`)
- Node connection (via `INodeConnector`)
- `ConnectionDiagnostics` ring buffer
- `ConnectionStateMachine` (internal)
- Reconnect timer / cancellation tokens
- Generation token for stale event suppression

**What it depends on** (injected):
- `IGatewayClientFactory` — creates operator client instances
- `INodeConnector` — creates/manages node client + capabilities
- `ICredentialResolver` — resolves credentials for a gateway record
- `ISshTunnelManager` — starts/stops SSH tunnel when configured
- `GatewayRegistry` — reads active gateway record
- `IClock` — testable time source
- `IOpenClawLogger` — structured logging

**What it does NOT do**:
- Touch any UI element, dispatch to UI thread, or show notifications
- Manage windows, tray icons, or toasts
- Persist settings (delegates to `GatewayRegistry` and `DeviceIdentity`)
- Own or configure capabilities (that's `INodeConnector`'s job)

**State it manages**:
- `ConnectionStateMachine` — the formal state machine (see §6)
- `_generationToken` (long) — incremented on each connect/disconnect to ignore stale events
- `_transitionSemaphore` (SemaphoreSlim) — serializes state transitions

**Thread safety model**:
- All public methods are async and acquire `_transitionSemaphore` before mutating state
- `CurrentSnapshot` is an immutable record; reads are lock-free
- `StateChanged` fires on the thread where the transition completes; UI consumers must
  marshal to their dispatcher
- `_generationToken` is `volatile long`; events arriving with a stale generation are
  silently dropped

---

### 4.2 ConnectionStateMachine (internal)

**Responsibility**: Enforce valid state transitions and provide the current connection
snapshot as an immutable record.

```csharp
internal sealed class ConnectionStateMachine
{
    // --- State ---
    public GatewayConnectionSnapshot Current { get; private set; }

    // --- Transitions (return false if transition is invalid) ---
    public bool TryTransition(ConnectionTrigger trigger, string? detail = null);

    // --- Query ---
    public bool CanTransition(ConnectionTrigger trigger);
}

public enum ConnectionTrigger
{
    // --- Operator lifecycle ---
    ConnectRequested,         // User/app initiates connection
    ConnectRequestSent,       // WebSocket open, hello sent, awaiting response
    ChallengeReceived,        // Gateway sent auth challenge (nonce)
    WebSocketConnected,       // Transport layer reports open
    HandshakeSucceeded,       // hello-ok received, scopes validated
    PairingPending,           // Gateway says device needs approval
    PairingApproved,          // Pairing approved, device token received
    PairingRejected,          // Pairing explicitly rejected by gateway admin
    AuthenticationFailed,     // Token invalid, scopes insufficient, signature rejected
    RateLimited,              // Gateway returned rate-limit (429 / close code)
    WebSocketDisconnected,    // Clean close or network drop
    WebSocketError,           // Unrecoverable transport error
    DisconnectRequested,      // User/app requests teardown
    ReconnectScheduled,       // Auto-reconnect timer started (backoff)
    ReconnectSuppressed,      // Auto-reconnect blocked (pairing pending, auth failed, etc.)
    Cancelled,                // CancellationToken fired during async operation
    Disposed,                 // Manager.Dispose() called

    // --- Node lifecycle (independent sub-FSM) ---
    NodeConnected,            // Node WebSocket open + hello-ok
    NodeDisconnected,         // Node WebSocket closed
    NodePairingRequired,      // Node needs pairing approval
    NodePaired,               // Node pairing approved
    NodePairingRejected,      // Node pairing explicitly rejected
    NodeError,                // Node transport/auth error
    NodeRateLimited           // Node rate-limited by gateway
}
```

**What it owns**: The current `GatewayConnectionSnapshot` value.

**What it depends on**: Nothing (pure logic, no I/O).

**What it does NOT do**: Subscribe to events, call async methods, or touch I/O.

**Thread safety model**: Not thread-safe — callers (the manager) must serialize access
via `_transitionSemaphore`.

---

### 4.3 GatewayConnectionSnapshot (immutable record)

**Responsibility**: Immutable, cross-thread-safe representation of the entire connection
state at a point in time.

```csharp
public sealed record GatewayConnectionSnapshot
{
    // --- Overall ---
    public OverallConnectionState OverallState { get; init; }

    // --- Operator ---
    public RoleConnectionState OperatorState { get; init; }
    public string? OperatorError { get; init; }
    public bool OperatorPairingRequired { get; init; }
    public string? OperatorDeviceId { get; init; }

    // --- Node ---
    public RoleConnectionState NodeState { get; init; }
    public string? NodeError { get; init; }
    public PairingStatus NodePairingStatus { get; init; }
    public string? NodeDeviceId { get; init; }

    // --- Gateway ---
    public string? GatewayId { get; init; }
    public string? GatewayUrl { get; init; }
    public string? GatewayName { get; init; }

    // --- Derived ---
    public bool IsFullyConnected =>
        OperatorState == RoleConnectionState.Connected &&
        NodeState == RoleConnectionState.Connected;

    public static GatewayConnectionSnapshot Idle { get; } = new()
    {
        OverallState = OverallConnectionState.Idle,
        OperatorState = RoleConnectionState.Idle,
        NodeState = RoleConnectionState.Idle,
        NodePairingStatus = PairingStatus.Unknown
    };
}

public enum OverallConnectionState
{
    Idle,           // No gateway configured or selected
    Connecting,     // At least one role is connecting
    Connected,      // Operator connected (node may still be connecting)
    Ready,          // Both operator and node connected and paired
    Degraded,       // Operator connected but node in error/rejected (functional but impaired)
    PairingRequired,// One or both roles need pairing approval
    Error,          // Unrecoverable error state (operator down)
    Disconnecting   // Teardown in progress
}

public enum RoleConnectionState
{
    Idle,
    Connecting,
    Connected,
    PairingRequired,
    PairingRejected,  // Explicitly rejected — distinct from PairingRequired
    RateLimited,      // Temporarily throttled — will retry after cooldown
    Error,
    Disabled          // Node mode disabled in settings
}
```

---

### 4.4 ICredentialResolver

**Responsibility**: Given a gateway record and identity path, return the single best
credential to use for connecting — or null if none available.

There are two distinct credential concepts:

- **Activation credential**: The token passed to the client constructor to establish the
  WebSocket connection and perform the initial hello handshake. This is what
  `ICredentialResolver` returns.
- **Handshake credential**: The token the client sends inside the hello payload for
  gateway-level authentication (e.g., Ed25519-signed challenge response). This is
  managed internally by the client using `DeviceIdentity` and is NOT part of
  `ICredentialResolver`'s responsibility.

The resolver's job is to pick the best *activation* credential. For already-paired
devices, the stored device token wins — using a bootstrap token for a device that
already has a device token would downgrade the session to bootstrap scopes and
potentially trigger unnecessary re-pairing.

```csharp
public interface ICredentialResolver
{
    GatewayCredential? ResolveOperator(GatewayRecord record, string identityPath);
    GatewayCredential? ResolveNode(GatewayRecord record, string identityPath);
}

public sealed record GatewayCredential(
    string Token,
    bool IsBootstrapToken,
    string Source  // e.g. "identity.DeviceToken", "record.SharedGatewayToken", "record.BootstrapToken"
);
```

**Canonical resolution order** (operator):

```
1. DeviceIdentity.DeviceToken    → Token, IsBootstrap=false, Source="identity.DeviceToken"
   (Paired device — highest priority. Never downgrade a paired device.)
2. record.SharedGatewayToken     → Token, IsBootstrap=false, Source="record.SharedGatewayToken"
   (Shared token — works for any device, full scopes.)
3. record.BootstrapToken         → Token, IsBootstrap=true,  Source="record.BootstrapToken"
   (Bootstrap — one-time setup, limited scopes. Used only for first-time pairing.)
4. (none)                        → null
```

**Canonical resolution order** (node):

```
1. DeviceIdentity.NodeDeviceToken → Token, IsBootstrap=false, Source="identity.NodeDeviceToken"
   (Paired node — highest priority.)
2. record.SharedGatewayToken      → Token, IsBootstrap=false, Source="record.SharedGatewayToken"
3. record.BootstrapToken          → Token, IsBootstrap=true,  Source="record.BootstrapToken"
4. (none)                         → null
```

> **Anti-pattern guard**: The resolver MUST NOT return a bootstrap token if a stored
> device token exists for the same role. Doing so would downgrade the paired session to
> bootstrap scopes, strip `operator.admin`, and potentially trigger a re-pair prompt on
> the gateway.

**What it owns**: Nothing.

**What it depends on**: `IDeviceIdentityReader` (reads stored token from identity file).

**What it does NOT do**: Write tokens, manage files, connect to anything.

**Thread safety model**: Stateless; all methods are pure functions. File reads are
idempotent. Safe to call from any thread.

---

### 4.5 IGatewayClientFactory

**Responsibility**: Create configured gateway client instances that implement both the
read-only `IOperatorGatewayClient` data interface and the `IGatewayClientLifecycle`
lifecycle interface.

```csharp
public interface IGatewayClientFactory
{
    /// <summary>
    /// Creates a gateway client. Returns the lifecycle handle; the manager can
    /// query it for IOperatorGatewayClient to expose to UI consumers.
    /// </summary>
    IGatewayClientLifecycle Create(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        IOpenClawLogger logger);
}

/// <summary>
/// Lifecycle interface owned by the manager. Not exposed to UI.
/// Testable: mock implementations can simulate connect/disconnect/events.
/// </summary>
public interface IGatewayClientLifecycle : IDisposable
{
    IOperatorGatewayClient DataClient { get; }
    event EventHandler<ConnectionStatus> StatusChanged;
    event EventHandler<string> AuthenticationFailed;
    event EventHandler<DeviceTokenReceivedEventArgs> DeviceTokenReceived;
    Task ConnectAsync(CancellationToken ct);
}
```

**What it owns**: Nothing (factory creates; manager owns the instance).

**What it depends on**: Nothing beyond constructor parameters.

**What it does NOT do**: Manage lifecycle, subscribe to events, or store references.

**Thread safety model**: Stateless factory. Safe to call from any thread.

> **Testability note**: The split into `IGatewayClientLifecycle` (for manager) and
> `IOperatorGatewayClient` (for UI) means manager tests can mock the full lifecycle
> without needing `OpenClawGatewayClient`. The concrete `OpenClawGatewayClient`
> implements both interfaces.

---

### 4.6 INodeConnector

**Responsibility**: Create and manage the node-side connection (WindowsNodeClient +
capability registration) for a given gateway.

```csharp
public interface INodeConnector : IDisposable
{
    // --- State ---
    bool IsConnected { get; }
    PairingStatus PairingStatus { get; }
    string? NodeDeviceId { get; }

    // --- Events ---
    event EventHandler<ConnectionStatus> StatusChanged;
    event EventHandler<PairingStatusEventArgs> PairingStatusChanged;

    // --- Lifecycle ---
    Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath);
    Task DisconnectAsync();

    // --- Capability Bridge ---
    /// <summary>
    /// The underlying node client, for capability registration and invoke handling.
    /// Null when disconnected.
    /// </summary>
    WindowsNodeClient? Client { get; }
}
```

**What it owns**: `WindowsNodeClient` instance, capability registrations.

**What it depends on**: `SettingsManager` (for node toggle states), `IOpenClawLogger`.

**What it does NOT do**: Resolve credentials (receives them), manage operator connection,
touch UI, manage windows/canvas/screen capture (that stays in `NodeService`).

**MCP-only mode**: When `SettingsData.McpOnlyMode` is true, the node connector skips
WebSocket connection entirely and only exposes capabilities via the local MCP HTTP
server. The connector reports `RoleConnectionState.Connected` (MCP server is "connected"
locally) and `PairingStatus.Paired` (no pairing needed for local-only operation). The
manager's `DeriveOverall()` sees a connected node and can reach `Ready` state even
without a gateway-side node WebSocket.

```csharp
public enum NodeConnectionMode
{
    Gateway,    // Normal: connect to gateway via WebSocket as node
    McpOnly,    // Local-only: expose capabilities via MCP HTTP, no WS
    Disabled    // Node mode off
}
```

**Thread safety model**: Single-owner (the manager). Not independently thread-safe.

---

### 4.7 ISshTunnelManager

**Responsibility**: Start and stop an SSH tunnel when the active gateway requires one.

```csharp
public interface ISshTunnelManager : IDisposable
{
    bool IsActive { get; }
    Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct);
    Task StopAsync();

    // Returns the local URL to use instead of the remote gateway URL
    string? LocalTunnelUrl { get; }
}

public sealed record SshTunnelConfig(
    string User,
    string Host,
    int RemotePort,
    int LocalPort);
```

**What it owns**: SSH process lifecycle.

**What it depends on**: `SshTunnelCommandLine` (existing), `IOpenClawLogger`.

**What it does NOT do**: Decide whether to use a tunnel (manager reads settings).

---

### 4.8 GatewayRegistry

**Responsibility**: Pure data catalog of known gateway endpoints. Persistence only.

```csharp
public sealed class GatewayRegistry
{
    // --- Query ---
    IReadOnlyList<GatewayRecord> GetAll();
    GatewayRecord? GetById(string id);
    GatewayRecord? GetActive();
    string GetIdentityDirectory(string gatewayId);

    // --- Mutate ---
    GatewayRecord AddOrUpdate(GatewayRecord record);
    void Remove(string id);
    void SetActive(string gatewayId);

    // --- Persistence ---
    void Save();
    void Load();

    // --- Events ---
    event EventHandler<GatewayRegistryChangedEventArgs> Changed;
}

public sealed record GatewayRecord
{
    public string Id { get; init; }              // stable GUID
    public string Url { get; init; }             // gateway WebSocket URL
    public string? FriendlyName { get; init; }   // user-facing label
    public string? SharedGatewayToken { get; init; }
    public string? BootstrapToken { get; init; }
    public DateTime? LastConnected { get; init; }
    public bool IsLocal { get; init; }               // local gateway (localhost/WSL)
    public SshTunnelConfig? SshTunnel { get; init; } // per-gateway SSH config
    public string IdentityDirName => Id;             // derived from Id — always unique
}
```

**What it owns**: `gateways.json` file, identity directory naming.

**What it depends on**: `IFileSystem` (for testability), data directory path.

**What it does NOT do**:
- Hold `ActiveClient` or `ActiveConnectionStatus` (these are RUNTIME state, not DATA)
- Create or manage `OpenClawGatewayClient` instances
- Resolve credentials (that's `ICredentialResolver`)
- Write `DeviceIdentity` files (the client handshake does that via `DeviceIdentity`)

**State it manages**: In-memory list of `GatewayRecord` + which one is "active" (by ID).

**Thread safety model**: Lock-protected internal list. `GatewayRecord` is immutable.
`Changed` event fires **outside** the lock to prevent deadlocks — the pattern is:
acquire lock → copy state → release lock → fire event with copied state.

---

### 4.9 ConnectionDiagnostics

**Responsibility**: Ring buffer of timestamped connection events for the diagnostics
window and troubleshooting.

```csharp
public sealed class ConnectionDiagnostics
{
    public ConnectionDiagnostics(int capacity = 500, IClock? clock = null);

    // --- Recording ---
    void Record(string category, string message, string? detail = null);
    void RecordStateChange(OverallConnectionState from, OverallConnectionState to);
    void RecordCredentialResolution(GatewayCredential? credential);
    void RecordWebSocketEvent(string eventName, string? detail = null);

    // --- Reading ---
    IReadOnlyList<ConnectionDiagnosticEvent> GetRecent(int count = 100);
    IReadOnlyList<ConnectionDiagnosticEvent> GetAll();

    // --- Events ---
    event EventHandler<ConnectionDiagnosticEvent> EventRecorded;

    // --- Lifecycle ---
    void Clear();
}

public sealed record ConnectionDiagnosticEvent(
    DateTime Timestamp,
    string Category,     // "state", "credential", "websocket", "pairing", "node", "error"
    string Message,
    string? Detail);
```

**What it owns**: The ring buffer (fixed-capacity circular array).

**What it depends on**: `IClock` (testable time).

**What it does NOT do**: Format for display, manage UI, persist to disk.

**Thread safety model**: Lock-free ring buffer with `Interlocked` write index. Reads
may see partially-written entries during a write (acceptable for diagnostics). Event
fires synchronously on the recording thread.

---

### 4.10 IOperatorGatewayClient (read-only facade)

**Responsibility**: Expose the subset of `OpenClawGatewayClient` that UI pages need,
without exposing connection lifecycle methods.

```csharp
public interface IOperatorGatewayClient
{
    // --- Events (forwarded from OpenClawGatewayClient) ---
    event EventHandler<NotificationEventArgs> NotificationReceived;
    event EventHandler<ActivityEventArgs> ActivityChanged;
    event EventHandler<ChannelHealthEventArgs> ChannelHealthUpdated;
    event EventHandler<SessionsEventArgs> SessionsUpdated;
    event EventHandler<UsageEventArgs> UsageUpdated;
    event EventHandler<NodesEventArgs> NodesUpdated;
    event EventHandler<GatewaySelfInfo> GatewaySelfUpdated;
    event EventHandler<NodePairListEventArgs> NodePairListUpdated;
    event EventHandler<DevicePairListEventArgs> DevicePairListUpdated;
    event EventHandler<ModelsListEventArgs> ModelsListUpdated;
    event EventHandler<PresenceEventArgs> PresenceUpdated;
    // ... additional data events as needed

    // --- Request Methods ---
    Task<bool> SendChatMessageAsync(string message, string? sessionKey = null);
    Task RequestSessionsAsync();
    Task RequestUsageAsync();
    Task RequestNodesAsync();
    // ... additional request methods as needed

    // --- Query ---
    IReadOnlyDictionary<string, SessionInfo> GetSessionList();
}
```

**Design note**: This interface is extracted from the existing `OpenClawGatewayClient`
public API. The concrete client implements it directly. The manager exposes it via
`OperatorClient` property — null when disconnected, non-null when connected. This
prevents UI code from calling `DisconnectAsync()` or other lifecycle methods on the
client directly.

---

## 5. Data Model

### Clean Separation

```
%APPDATA%/OpenClawTray/
├── settings.json              ← SettingsData (app prefs only)
├── gateways.json              ← GatewayRegistry (gateway catalog)
├── gateways/                  ← per-gateway identity directories
│   ├── <gateway-id-1>/
│   │   └── device-key-ed25519.json  ← DeviceIdentity (Ed25519 keypair + tokens)
│   ├── <gateway-id-2>/
│   │   └── device-key-ed25519.json
│   └── ...
└── logs/                      ← application logs
```

### SettingsData (After Migration)

Connection credentials (`Token`, `BootstrapToken`) are **removed** from `SettingsData`
and migrated to `GatewayRecord`. Settings retains only:

| Category | Fields |
|----------|--------|
| Node toggles | `EnableNodeMode`, `NodeCanvas/Screen/Camera/Location/BrowserProxy/SttEnabled` |
| Consent flags | `ScreenRecordingConsentGiven`, `CameraRecordingConsentGiven` |
| UI preferences | `HubNavPaneOpen`, `GlobalHotkeyEnabled`, `AutoStart`, `ShowNotifications` |
| Audio/Voice | `SttLanguage`, `SttModelName`, `VoiceTtsEnabled`, `TtsProvider`, `Tts*` |
| Notification rules | `NotifyHealth`, `NotifyUrgent`, `NotifyReminder`, etc. |
| MCP | `EnableMcpServer`, `McpOnlyMode` |
| Misc | `A2UIImageHosts`, `UserRules`, `PreferStructuredCategories`, etc. |

> **Removed from SettingsData**: `Token`, `BootstrapToken`, `GatewayUrl` (migrated to
> `GatewayRecord`), `PreferredGatewayId` (replaced by `GatewayRegistry.SetActive()`).
> `UseSshTunnel` and `SshTunnel*` fields move to per-gateway `SshTunnelConfig` inside
> `GatewayRecord` so each gateway can have its own tunnel configuration.
>
> **Single source of truth for active gateway**: `GatewayRegistry.GetActive()` is the
> only way to determine which gateway is active. There is no `PreferredGatewayId` in
> settings and no `ActiveClient` on the registry. This eliminates the conflict where
> `SettingsData.PreferredGatewayId` and `GatewayRegistry` could disagree.

### GatewayRecord (New — in GatewayRegistry)

```csharp
public sealed record GatewayRecord
{
    public string Id { get; init; }                  // stable GUID, primary key
    public string Url { get; init; }                 // wss://gateway.example.com
    public string? FriendlyName { get; init; }       // "Home Gateway", "Work Gateway"
    public string? SharedGatewayToken { get; init; } // long-lived shared token
    public string? BootstrapToken { get; init; }     // one-time bootstrap token
    public DateTime? LastConnected { get; init; }    // for "recent gateways" UI
    public bool IsLocal { get; init; }               // local gateway (localhost/WSL)
    public SshTunnelConfig? SshTunnel { get; init; } // per-gateway SSH tunnel config

    // Identity directory is deterministically derived from Id.
    // Using the gateway Id (a GUID) avoids path-unsafe characters
    // and guarantees uniqueness even if URLs change.
    public string IdentityDirName => Id;
}

public sealed record SshTunnelConfig(
    string User,
    string Host,
    int RemotePort,
    int LocalPort);
```

> **Security note — token storage**: `SharedGatewayToken` and `BootstrapToken` are
> stored in `gateways.json` in the user's `%APPDATA%` directory, which is user-profile
> protected. For environments requiring stronger protection, a future enhancement could
> use DPAPI (`ProtectedData`) to encrypt these values at rest, matching the pattern
> already used for `TtsElevenLabsApiKey` in `SettingsManager`. This is not a launch
> blocker but should be tracked as a follow-up security hardening item.
```

### DeviceIdentity (Per Gateway — Unchanged Core)

```
{
  "PrivateKeyBase64": "...",
  "PublicKeyBase64": "...",
  "DeviceId": "device-xxxx",
  "DeviceToken": "ey...",           // operator device token from gateway
  "DeviceTokenScopes": ["operator.admin", ...],
  "NodeDeviceToken": "ey...",       // node device token from gateway
  "NodeDeviceTokenScopes": ["node.invoke", ...],
  "Algorithm": "Ed25519",
  "CreatedAt": "2024-01-15T..."
}
```

### Migration from SettingsData to GatewayRecord

On first launch after migration:

```
1. If settings.GatewayUrl is set:
   a. Create GatewayRecord with new Id, Url = settings.GatewayUrl
   b. Move settings.Token → record.SharedGatewayToken
   c. Move settings.BootstrapToken → record.BootstrapToken
   d. Move settings.UseSshTunnel / SshTunnel* → record.SshTunnel
   e. Move existing device-key-ed25519.json → gateways/<id>/device-key-ed25519.json
   f. Set record as active in registry
   g. Clear settings.Token, settings.BootstrapToken, settings.PreferredGatewayId
   h. Save both files

2. If settings.GatewayUrl is not set:
   a. No migration needed; registry starts empty
```

**Idempotency & rollback guarantees**:

- **Idempotent**: Migration checks for existing `gateways.json` before running. If it
  already exists and contains a record matching `settings.GatewayUrl`, migration is
  skipped. This makes it safe to run on every startup.
- **Atomic write**: Both `gateways.json` and `settings.json` are written using
  write-to-temp-then-rename to prevent partial writes on crash.
- **Rollback**: If the app is downgraded to a pre-migration version, `settings.json`
  will have cleared `Token`/`BootstrapToken` fields. The old version will see no
  credentials and prompt the user to re-enter a setup code. This is acceptable because:
  (a) the device identity file still exists in the original location (copied, not moved,
  during migration), and (b) the old credential resolver will find it via
  `DeviceIdentity.TryReadStoredDeviceToken()`.
- **Identity file safety**: During migration, the identity file is **copied** to the
  new per-gateway directory, not moved. The original file is left in place until a
  subsequent cleanup step (Phase 4). This ensures rollback compatibility.
- **Concurrent launch**: Use a named mutex (`OpenClawTray_Migration`) to prevent
  two instances from migrating simultaneously.

---

## 6. State Machine

### Overall Connection State Machine

```
                  ┌──────────────────────────────────────────────┐
                  │                                              │
                  ▼                                              │
            ┌──────────┐    ConnectRequested     ┌────────────┐  │
            │          │ ───────────────────────► │            │  │
   ────────►│   Idle   │                         │ Connecting  │  │
            │          │ ◄─── DisconnectReq ──── │            │  │
            └──────────┘                         └─────┬──────┘  │
                  ▲                                    │         │
                  │                          ┌─────────┼─────┐   │
                  │                          │         │     │   │
                  │              HandshakeOk │   Error │     │   │
                  │                          ▼         ▼     │   │
                  │                    ┌───────────┐  ┌──────┴─┐ │
                  │                    │           │  │        │ │
                  │    DisconnectReq   │ Connected │  │ Error  │─┘
                  ├────────────────────│           │  │        │ AutoReconnect
                  │                    └─────┬─────┘  └────────┘
                  │                          │
                  │              PairingReq  │  NodeConnected
                  │                ┌─────────┤──────────┐
                  │                ▼         │          ▼
                  │         ┌──────────────┐ │   ┌──────────┐
                  │         │   Pairing    │ │   │          │
    DisconnectReq │         │  Required    │ │   │  Ready   │
                  ├─────────│              │ │   │          │
                  │         └──────┬───────┘ │   └──────────┘
                  │                │         │        │
                  │   PairingOk    │         │        │ DisconnectReq
                  │                ▼         │        │
                  │          (re-enters      │        │
                  │           Connected)     │        │
                  │                          │        │
                  └──────────────────────────┘────────┘
```

### Transition Table

> **Architecture note**: Operator and node each run their own sub-FSM (using
> `RoleConnectionState`). The `OverallConnectionState` shown below is *derived* from
> both sub-FSMs via `DeriveOverall()` — it is not a state machine itself. The transition
> table below describes the **operator sub-FSM** triggers and their effect on the
> derived overall state. Node sub-FSM transitions follow the same pattern but are
> independent and may occur concurrently.

| From (Operator) | Trigger | To (Operator) | Side Effects |
|-----------------|---------|---------------|-------------|
| Idle | ConnectRequested | Connecting | Resolve credentials, start tunnel if needed, create client |
| Connecting | ConnectRequestSent | Connecting | Hello sent, awaiting server response |
| Connecting | ChallengeReceived | Connecting | Sign challenge nonce, send auth response |
| Connecting | WebSocketConnected | Connecting | Transport open — wait for handshake |
| Connecting | HandshakeSucceeded | Connected | Record diagnostic, reset backoff, start node if enabled |
| Connecting | PairingPending | PairingRequired | Record diagnostic, block node start, suppress auto-reconnect |
| Connecting | PairingRejected | Error | Record diagnostic, do NOT auto-reconnect, surface to UI |
| Connecting | AuthenticationFailed | Error | Record diagnostic, do NOT auto-reconnect |
| Connecting | RateLimited | Error | Record diagnostic, schedule reconnect with extended backoff |
| Connecting | WebSocketError | Error | Record diagnostic, schedule auto-reconnect |
| Connecting | DisconnectRequested | Idle | Cancel connect, dispose client |
| Connecting | Cancelled | Idle | CancellationToken fired, dispose client |
| Connected | NodeConnected | Connected | Update node sub-state; may derive Ready |
| Connected | NodePaired | Connected | Update node sub-state; derives Ready if operator+node both connected |
| Connected | NodeError | Connected | Update node sub-state; derives Degraded |
| Connected | NodePairingRequired | Connected | Update node sub-state; derives PairingRequired |
| Connected | NodePairingRejected | Connected | Update node sub-state; derives Degraded |
| Connected | PairingPending | PairingRequired | Operator pairing requested (rare mid-connection) |
| Connected | WebSocketDisconnected | Connecting | Auto-reconnect with backoff |
| Connected | WebSocketError | Error | Record diagnostic, schedule auto-reconnect |
| Connected | DisconnectRequested | Idle | Dispose client + node, clear state |
| Ready | WebSocketDisconnected | Connecting | Auto-reconnect (node also tears down) |
| Ready | NodeError | Connected | Node sub-state → Error; derives Degraded |
| Ready | NodeDisconnected | Connected | Node sub-state → Idle; derives Connected |
| Ready | DisconnectRequested | Idle | Dispose all |
| PairingRequired | PairingApproved | Connecting | Re-do handshake with new device token |
| PairingRequired | PairingRejected | Error | Terminal — user must re-pair or use different creds |
| PairingRequired | DisconnectRequested | Idle | Dispose all |
| PairingRequired | WebSocketDisconnected | Error | Lost connection during pairing; suppress reconnect |
| PairingRequired | WebSocketError | Error | Record diagnostic |
| Error | ConnectRequested | Connecting | Fresh connect attempt |
| Error | ReconnectScheduled | Connecting | Backoff-delayed reconnect |
| Error | ReconnectSuppressed | Error | No-op; logged to diagnostics |
| Error | DisconnectRequested | Idle | Clear error, dispose |
| Error | Disposed | Idle | Final cleanup |
| * | Disposed | Idle | From any state — emergency teardown |

### Operator vs Node Sub-States

The operator and node each maintain an independent sub-FSM using `RoleConnectionState`.
The `OverallConnectionState` is **derived** — there is no single overall state machine.
This means operator and node can be in any combination of states simultaneously, and
the UI sees a single derived value.

The `GatewayConnectionSnapshot` tracks operator and node states independently using
`RoleConnectionState`. The `OverallConnectionState` is derived:

```csharp
public static OverallConnectionState DeriveOverall(
    RoleConnectionState op, RoleConnectionState node, bool nodeEnabled)
{
    // Error in operator → overall error (operator is primary)
    if (op == RoleConnectionState.Error)
        return OverallConnectionState.Error;

    // Pairing required in operator → overall pairing
    if (op == RoleConnectionState.PairingRequired)
        return OverallConnectionState.PairingRequired;

    // Operator still connecting → overall connecting
    if (op == RoleConnectionState.Connecting)
        return OverallConnectionState.Connecting;

    // From here, operator is Connected.

    // Node error/rejected while operator connected → Degraded
    if (op == RoleConnectionState.Connected && nodeEnabled &&
        (node == RoleConnectionState.Error ||
         node == RoleConnectionState.PairingRejected ||
         node == RoleConnectionState.RateLimited))
        return OverallConnectionState.Degraded;

    // Node pairing required → overall pairing
    if (op == RoleConnectionState.Connected &&
        node == RoleConnectionState.PairingRequired)
        return OverallConnectionState.PairingRequired;

    // Node still connecting → overall connecting
    if (op == RoleConnectionState.Connected &&
        nodeEnabled && node == RoleConnectionState.Connecting)
        return OverallConnectionState.Connecting;

    // Operator connected, node connected (or disabled) → Ready
    if (op == RoleConnectionState.Connected &&
        (node == RoleConnectionState.Connected || !nodeEnabled))
        return OverallConnectionState.Ready;

    // Operator connected but node not yet ready → Connected (partial)
    if (op == RoleConnectionState.Connected)
        return OverallConnectionState.Connected;

    // Both idle
    return OverallConnectionState.Idle;
}
```

### Generation Token for Stale Event Protection

Each connect/switch operation increments a generation counter and captures a per-operation
`CancellationTokenSource`. Events arriving with a stale generation are silently dropped.
The CTS allows the manager to cancel in-flight operations (e.g., a connect that's been
superseded by a disconnect or gateway switch).

> **Threading note**: `_generation` must use `Interlocked.Increment` /
> `Interlocked.Read` / `Interlocked.Exchange` — **not** `volatile long`, which does not
> guarantee atomicity on 32-bit runtimes. The `_operationCts` is exchanged atomically
> via `Interlocked.Exchange` and the previous value is cancelled+disposed.

```csharp
// Inside GatewayConnectionManager:
private long _generation;
private CancellationTokenSource? _operationCts;

private async Task ConnectAsync(...)
{
    var gen = Interlocked.Increment(ref _generation);

    // Cancel any in-flight operation from the previous generation
    var oldCts = Interlocked.Exchange(ref _operationCts, new CancellationTokenSource());
    oldCts?.Cancel();
    oldCts?.Dispose();

    var ct = _operationCts!.Token;

    var client = _clientFactory.Create(...);
    client.StatusChanged += (s, status) =>
    {
        if (Interlocked.Read(ref _generation) != gen) return; // stale
        OnOperatorStatusChanged(status);
    };
    // ...
}
```

This pattern prevents events from a disposed/replaced client from affecting the
current state machine. It's especially important during gateway switching and
reconnect-after-pairing scenarios.

---

## 7. Event Architecture

### Event Hierarchy: Three Layers

```
Layer 1: Transport (WebSocketClientBase)
  ├── StatusChanged(ConnectionStatus)        — raw WS open/close/error
  └── AuthenticationFailed(string)           — auth reject from gateway

Layer 2: Service (GatewayConnectionManager)
  ├── StateChanged(GatewayConnectionSnapshot) — derived, deduplicated, generation-guarded
  └── DiagnosticEvent(ConnectionDiagnosticEvent) — timestamped log entry

Layer 3: UI (HubWindow, ConnectionPage, etc.)
  └── Subscribe to manager.StateChanged → update controls on UI thread
```

### Event Flow: Normal Connect

```
1. User clicks "Connect"
2. App calls manager.ConnectAsync()
3. Manager:
   a. Acquires _transitionSemaphore
   b. Reads active GatewayRecord from registry
   c. Resolves credentials via ICredentialResolver
   d. Starts SSH tunnel if configured (ISshTunnelManager)
   e. Creates OpenClawGatewayClient via IGatewayClientFactory
   f. Subscribes to client events with generation guard
   g. Transitions state: Idle → Connecting
   h. Fires StateChanged(snapshot)
   i. Releases semaphore
4. Client connects WebSocket
5. Client fires StatusChanged(Connected)
6. Manager (generation-guarded handler):
   a. Acquires semaphore
   b. Records diagnostic event
   c. Waits for handshake (hello-ok message)
7. Client processes hello-ok, fires internal handshake event
8. Manager:
   a. Transitions: Connecting → Connected
   b. Fires StateChanged(snapshot)
   c. If node enabled, calls _nodeConnector.ConnectAsync(...)
   d. Releases semaphore
9. Node connects, pairs, fires StatusChanged
10. Manager:
    a. Transitions: Connected → Ready
    b. Fires StateChanged(snapshot)
```

### Event Flow: Setup Code Apply

```
1. User enters setup code in UI
2. UI calls manager.ApplySetupCodeAsync(code)
3. Manager:
   a. Acquires semaphore
   b. Decodes setup code → (gatewayUrl, token, isBootstrap)
   c. Disconnects current connection if any
   d. Creates or updates GatewayRecord in registry
   e. Sets as active gateway
   f. Saves registry
   g. Connects with new credentials
   h. Returns SetupCodeResult
```

### Event Flow: Pairing

```
1. Client receives auth.pairingRequired from gateway
2. Manager (generation-guarded):
   a. Acquires semaphore
   b. Transitions: Connecting → PairingRequired
   c. Records diagnostic
   d. Fires StateChanged(snapshot)
   e. Blocks auto-reconnect
3. User approves pairing on gateway side
4. Client receives hello-ok with device token
5. Manager:
   a. Transitions: PairingRequired → Connected
   b. Stores device token via DeviceIdentity
   c. Updates GatewayRecord (clears bootstrap token if used)
   d. Fires StateChanged(snapshot)
   e. Re-enables auto-reconnect
```

### Subscriber Rules

> **Threading invariant**: All service-layer events (`StateChanged`, `DiagnosticEvent`,
> `OperatorClientChanged`) MUST fire **outside** any lock or semaphore held by the
> manager. This prevents deadlocks when subscribers call back into the manager. The
> pattern is: acquire semaphore → compute new state → release semaphore → fire event.

> **OperatorClient events** (`NotificationReceived`, `SessionsUpdated`, etc.) fire on
> the WebSocket receive thread. Any UI subscriber MUST marshal to `DispatcherQueue`
> before touching UI elements. This applies equally whether the subscriber is App.xaml.cs,
> HubWindow, or any page.

| Consumer | Subscribes To | Must Marshal? |
|----------|--------------|---------------|
| App.xaml.cs | manager.StateChanged | Yes (DispatcherQueue) |
| App.xaml.cs | manager.OperatorClientChanged | Yes (DispatcherQueue) — re-wire data event handlers |
| App.xaml.cs | operatorClient.NotificationReceived | Yes (DispatcherQueue) — toast display |
| HubWindow | manager.StateChanged | Yes (DispatcherQueue) |
| ConnectionPage | manager.StateChanged | Yes (DispatcherQueue) |
| TrayIcon | manager.StateChanged | Yes (DispatcherQueue) |
| DiagnosticsWindow | manager.DiagnosticEvent | Yes (DispatcherQueue) |
| NodeService | operatorClient events | No (already on WS thread, but UI calls within NodeService must marshal) |

### Deduplication

The manager fires `StateChanged` only when the snapshot actually differs:

```csharp
private void EmitIfChanged(GatewayConnectionSnapshot newSnapshot)
{
    var prev = _stateMachine.Current;
    if (prev == newSnapshot) return; // record equality
    _stateMachine.Current = newSnapshot;
    StateChanged?.Invoke(this, newSnapshot);
}
```

---

## 8. Credential Resolution

### Two Credential Concepts

| Concept | Purpose | Managed By | When |
|---------|---------|-----------|------|
| **Activation credential** | Token passed to client constructor; opens WebSocket, authenticates hello | `ICredentialResolver` → `GatewayConnectionManager` | Before `new OpenClawGatewayClient(...)` |
| **Handshake credential** | Ed25519-signed challenge-response inside the hello payload | `DeviceIdentity` (internal to client) | During WebSocket hello exchange |

`ICredentialResolver` is responsible ONLY for activation credentials. The handshake
credential flow (challenge → sign → verify) lives inside `OpenClawGatewayClient` and
`WindowsNodeClient` and is not part of the manager's concern.

### The One Canonical Path (Activation Credentials)

All credential resolution flows through `ICredentialResolver.ResolveOperator()` and
`ICredentialResolver.ResolveNode()`. There are no other resolution paths.

```
┌────────────────────────────────────────────────────────────────┐
│                    ICredentialResolver                          │
│                                                                │
│  ResolveOperator(GatewayRecord record, string identityPath)    │
│                                                                │
│    1. DeviceIdentity.DeviceToken     ──► paired device wins    │
│    2. record.SharedGatewayToken      ──► shared token          │
│    3. record.BootstrapToken          ──► first-time only       │
│    4. return null                                              │
│                                                                │
│  ResolveNode(GatewayRecord record, string identityPath)        │
│                                                                │
│    1. DeviceIdentity.NodeDeviceToken ──► paired node wins      │
│    2. record.SharedGatewayToken      ──► shared token          │
│    3. record.BootstrapToken          ──► first-time only       │
│    4. return null                                              │
│                                                                │
│  Each returns: GatewayCredential(Token, IsBootstrapToken,      │
│                                  Source)                       │
│  Source is diagnostic string for ConnectionDiagnostics          │
└────────────────────────────────────────────────────────────────┘
```

> **Critical invariant — no paired-device downgrade**: If a stored device token exists,
> the resolver MUST return it, even if `SharedGatewayToken` or `BootstrapToken` are
> also present. Returning a bootstrap token for a paired device downgrades scopes,
> strips `operator.admin`, and may trigger an unnecessary re-pair prompt. The stored
> device token proves the device was already approved and carries full scopes.

### Who Calls It

| Caller | Method | When |
|--------|--------|------|
| `GatewayConnectionManager.ConnectAsync` | `ResolveOperator` | Starting operator connection |
| `GatewayConnectionManager.ConnectAsync` | `ResolveNode` | Starting node connection (if enabled) |
| `GatewayConnectionManager.ApplySetupCodeAsync` | (indirect) | After writing record, calls ConnectAsync |

Nobody else calls credential resolution. Not App.xaml.cs. Not HubWindow. Not any page.

### Token Storage: Interim and Target

> **Prerequisite reality**: Today, `OpenClawGatewayClient` and `WindowsNodeClient`
> internally write device tokens to `DeviceIdentity` during the hello-ok handshake.
> The manager cannot own token writes until the clients expose explicit events for
> token receipt (see Step 2.0 in §12). Until then:
>
> - **Interim**: Token storage remains client-owned. The manager observes that a
>   handshake succeeded (via `HandshakeSucceeded` trigger) but does NOT write tokens.
>   The client writes tokens to `DeviceIdentity`. The manager updates `GatewayRecord`
>   (e.g., clearing consumed bootstrap tokens) after observing success.
>
> - **Target**: Clients emit `DeviceTokenReceived(token, scopes, role)` events.
>   The manager handles these events and calls `IDeviceIdentityStore.StoreToken(...)`.
>   Clients no longer write to disk directly.

**Target token write path** (after Step 2.0):

```
1. Client hello-ok handler receives device token from gateway
2. Client fires DeviceTokenReceived(token, scopes, role) event
3. Manager's generation-guarded event handler fires
4. Manager calls IDeviceIdentityStore.StoreToken(identityPath, token, scopes, role)
5. Manager updates GatewayRecord if bootstrap token should be cleared
6. Manager saves registry

That's it. No other code writes tokens.
```

---

## 9. Setup Code Flow

### The One Canonical Path

```
┌────────────────────────────────────────────────────────────────┐
│  Entry points (all call the same method):                      │
│                                                                │
│  • ConnectionPage "Apply" button                               │
│  • SetupWizard flow                                            │
│  • Onboarding advanced setup                                   │
│  • Deep link / protocol activation                             │
│  • Clipboard paste                                             │
│                                                                │
│  All call: manager.ApplySetupCodeAsync(string setupCode)       │
└──────────────────────────┬─────────────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────────────┐
│  GatewayConnectionManager.ApplySetupCodeAsync(setupCode)       │
│                                                                │
│  1. Decode setup code:                                         │
│     GatewayUrlHelper.DecodeCredentials(setupCode)              │
│     → (gatewayUrl, token, isBootstrap)                         │
│                                                                │
│  2. Validate URL:                                              │
│     HttpUrlValidator.IsValid(gatewayUrl)                       │
│                                                                │
│  3. Disconnect current gateway (if any):                       │
│     await DisconnectAsync()                                    │
│                                                                │
│  4. Create or update gateway record:                           │
│     var record = new GatewayRecord {                           │
│         Id = existingOrNewGuid,                                │
│         Url = gatewayUrl,                                      │
│         SharedGatewayToken = isBootstrap ? null : token,       │
│         BootstrapToken = isBootstrap ? token : null,           │
│         // IdentityDirName is derived from Id automatically    │
│     };                                                         │
│     _registry.AddOrUpdate(record);                             │
│     _registry.SetActive(record.Id);                            │
│     _registry.Save();                                          │
│                                                                │
│  5. Connect to new gateway:                                    │
│     await ConnectAsync(record.Id)                              │
│                                                                │
│  6. Return result:                                             │
│     SetupCodeResult.Success / InvalidCode / ConnectionFailed   │
└────────────────────────────────────────────────────────────────┘
```

### SetupCodeResult

```csharp
public sealed record SetupCodeResult(
    SetupCodeOutcome Outcome,
    string? ErrorMessage = null,
    string? GatewayUrl = null);

public enum SetupCodeOutcome
{
    Success,
    InvalidCode,        // couldn't decode
    InvalidUrl,         // URL validation failed
    ConnectionFailed,   // decoded but couldn't connect
    AlreadyConnected    // same gateway, same credentials
}
```

---

## 10. UI Integration

### HubWindow Dependency Shape (Target)

```csharp
// Target: HubWindow receives the manager, not individual fields
public sealed partial class HubWindow : WindowEx
{
    private readonly IGatewayConnectionManager _connectionManager;
    private readonly SettingsManager _settings;

    public HubWindow(
        IGatewayConnectionManager connectionManager,
        SettingsManager settings)
    {
        _connectionManager = connectionManager;
        _settings = settings;

        _connectionManager.StateChanged += OnConnectionStateChanged;
        InitializeComponent();
    }

    private void OnConnectionStateChanged(object? sender, GatewayConnectionSnapshot snap)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Update all pages with the new snapshot
            UpdateStatusBar(snap);
            _homePage?.UpdateConnectionState(snap);
            _connectionPage?.UpdateConnectionState(snap);
        });
    }
}
```

### Page Interaction Pattern

Pages never call the gateway client directly for connection operations. They use the
manager for lifecycle and the `OperatorClient` for data requests:

```
┌─────────────────────────────────────────────────────────────────┐
│  ConnectionPage                                                 │
│                                                                 │
│  Connect button → manager.ConnectAsync()                        │
│  Disconnect button → manager.DisconnectAsync()                  │
│  Setup code apply → manager.ApplySetupCodeAsync(code)           │
│  State display ← manager.StateChanged → snapshot                │
│                                                                 │
│  Does NOT: create clients, resolve credentials, write tokens    │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  HomePage                                                       │
│                                                                 │
│  Session ring ← manager.OperatorClient.SessionsUpdated          │
│  Quick actions → manager.OperatorClient.SendChatMessageAsync()  │
│  Status badge ← manager.StateChanged → snapshot.OverallState    │
│                                                                 │
│  Does NOT: manage connection lifecycle                          │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  OnboardingWindow                                               │
│                                                                 │
│  Setup flow → manager.ApplySetupCodeAsync(code)                 │
│  Local setup → LocalGatewaySetup (independent service)          │
│  State feedback ← manager.StateChanged                          │
│                                                                 │
│  Does NOT: call GatewayUrlHelper directly, write settings       │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  DiagnosticsWindow                                              │
│                                                                 │
│  Live event stream ← manager.DiagnosticEvent                    │
│  History ← manager.Diagnostics.GetAll()                         │
│                                                                 │
│  Read-only view. Does NOT modify connection state.              │
└─────────────────────────────────────────────────────────────────┘
```

### OnSettingsSaved (Target Behavior)

Currently, `OnSettingsSaved` in App.xaml.cs tears down and rebuilds the entire connection
stack. In the target architecture, a settings diff model determines the minimum action:

```csharp
/// <summary>
/// Classifies what changed between two SettingsData snapshots.
/// </summary>
public enum SettingsChangeImpact
{
    NoOp,                  // No meaningful change (e.g., whitespace, same values)
    UiOnly,                // HubNavPaneOpen, ShowNotifications, etc. — no reconnect
    CapabilityReload,      // Node capability toggled — reload capabilities, no reconnect
    NodeReconnectRequired, // EnableNodeMode toggled, or node toggle change requires restart
    OperatorReconnectRequired, // SSH tunnel config changed — full reconnect
    FullReconnectRequired  // Gateway URL changed — tear down and reconnect everything
}

public static SettingsChangeImpact ClassifyChange(SettingsData prev, SettingsData next)
{
    // Gateway URL or SSH tunnel changed → full reconnect
    // EnableNodeMode toggled → node reconnect
    // Node capability toggle → capability reload
    // UI prefs only → UI-only
    // Nothing → no-op
}
```

```csharp
// App.xaml.cs — OnSettingsSaved (target)
private async void OnSettingsSaved(object? sender, SettingsData settings)
{
    var impact = SettingsChangeImpact.ClassifyChange(_previousSettings, settings);
    _previousSettings = settings;

    switch (impact)
    {
        case SettingsChangeImpact.FullReconnectRequired:
        case SettingsChangeImpact.OperatorReconnectRequired:
            await _connectionManager.ReconnectAsync();
            break;
        case SettingsChangeImpact.NodeReconnectRequired:
            await _connectionManager.ReconnectNodeAsync();
            break;
        case SettingsChangeImpact.CapabilityReload:
            _connectionManager.ReloadNodeCapabilities(settings);
            break;
        case SettingsChangeImpact.UiOnly:
        case SettingsChangeImpact.NoOp:
            break;
    }

    // Non-connection settings: update tray, hotkeys, etc. locally
    UpdateTrayIcon(settings);
    UpdateHotkeys(settings);
}
```

The manager's `ReconnectAsync` handles: disconnect → re-read registry → re-resolve
credentials → reconnect. App.xaml.cs never manually disposes clients or resubscribes
events.

---

## 11. Testability

### Testability Matrix

| Component | Test Type | WinUI? | WebSocket? | Filesystem? | Key Interface Seam |
|-----------|-----------|--------|------------|-------------|--------------------|
| ConnectionStateMachine | Unit | No | No | No | Pure logic, no deps |
| GatewayConnectionSnapshot | Unit | No | No | No | Immutable record |
| CredentialResolver | Unit | No | No | Mock | IDeviceIdentityReader |
| GatewayConnectionManager | Unit | No | No | No | IGatewayClientFactory, INodeConnector, ICredentialResolver |
| GatewayRegistry | Unit | No | No | Mock | IFileSystem |
| ConnectionDiagnostics | Unit | No | No | No | IClock |
| SetupCode flow | Integration | No | No | No | Manager + mock factory |
| Reconnect behavior | Integration | No | No | No | Manager + mock factory |
| Pairing flow | Integration | No | No | No | Manager + mock factory |
| Full connection | Integration | No | Mock WS | Temp dir | IWebSocketTransport (see below) |

### Interface Seams for Testing

```csharp
// Time abstraction — replaces DateTime.Now/UtcNow
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

// Filesystem abstraction — for GatewayRegistry and DeviceIdentity tests
public interface IFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
}

// Device identity reader — for CredentialResolver tests
public interface IDeviceIdentityReader
{
    string? TryReadStoredDeviceToken(string dataPath);
    string? TryReadStoredNodeDeviceToken(string dataPath);
}

// WebSocket transport — for full integration tests without real connections
// This is an EXPLICIT prerequisite for mock-WS-based tests. Without it,
// testing reconnect behavior, backoff timing, or concurrent event ordering
// requires a real WebSocket server.
public interface IWebSocketTransport : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<WebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
    WebSocketState State { get; }
}
```

> **Prerequisite note**: `IWebSocketTransport` requires changes to
> `WebSocketClientBase` to accept an injected transport instead of creating
> `ClientWebSocket` directly. This is a prerequisite for full connection integration
> tests but is NOT required for manager-level tests (which mock at the
> `IGatewayClientFactory` / `IGatewayClientLifecycle` level). Prioritize
> factory-level mocking first; add transport-level mocking only when testing
> reconnect/backoff behavior.
```

### Test Scenarios Enabled

**State Machine Tests** (pure unit tests, no mocking):
```csharp
[Fact]
public void Idle_ConnectRequested_TransitionsToConnecting()
{
    var sm = new ConnectionStateMachine();
    Assert.True(sm.TryTransition(ConnectionTrigger.ConnectRequested));
    Assert.Equal(OverallConnectionState.Connecting, sm.Current.OverallState);
}

[Fact]
public void Connected_DisconnectRequested_TransitionsToIdle()
{
    var sm = new ConnectionStateMachine();
    sm.TryTransition(ConnectionTrigger.ConnectRequested);
    sm.TryTransition(ConnectionTrigger.HandshakeSucceeded);
    sm.TryTransition(ConnectionTrigger.DisconnectRequested);
    Assert.Equal(OverallConnectionState.Idle, sm.Current.OverallState);
}

[Fact]
public void InvalidTransition_ReturnsFalse()
{
    var sm = new ConnectionStateMachine();
    Assert.False(sm.TryTransition(ConnectionTrigger.HandshakeSucceeded)); // can't handshake from Idle
}
```

**Credential Resolver Tests** (mock filesystem):
```csharp
[Fact]
public void ResolveOperator_PrefersDeviceToken_OverSharedAndBootstrap()
{
    var record = new GatewayRecord { SharedGatewayToken = "shared", BootstrapToken = "boot" };
    mockIdentityReader.Setup(r => r.TryReadStoredDeviceToken("/id")).Returns("paired-tok");
    var result = resolver.ResolveOperator(record, "/id");
    Assert.Equal("paired-tok", result!.Token);
    Assert.False(result.IsBootstrapToken);
    Assert.Equal("identity.DeviceToken", result.Source);
}

[Fact]
public void ResolveOperator_FallsToSharedToken_WhenNoDeviceToken()
{
    var record = new GatewayRecord { SharedGatewayToken = "shared", BootstrapToken = "boot" };
    mockIdentityReader.Setup(r => r.TryReadStoredDeviceToken("/id")).Returns((string?)null);
    var result = resolver.ResolveOperator(record, "/id");
    Assert.Equal("shared", result!.Token);
    Assert.False(result.IsBootstrapToken);
}

[Fact]
public void ResolveOperator_FallsToBootstrap_WhenNoDeviceOrShared()
{
    var record = new GatewayRecord { BootstrapToken = "boot" };
    mockIdentityReader.Setup(r => r.TryReadStoredDeviceToken("/id")).Returns((string?)null);
    var result = resolver.ResolveOperator(record, "/id");
    Assert.Equal("boot", result!.Token);
    Assert.True(result.IsBootstrapToken);
}

[Fact]
public void ResolveOperator_NeverDowngradesPairedDevice()
{
    // Even if bootstrap token is present, paired device token wins
    var record = new GatewayRecord { BootstrapToken = "boot" };
    mockIdentityReader.Setup(r => r.TryReadStoredDeviceToken("/id")).Returns("paired-tok");
    var result = resolver.ResolveOperator(record, "/id");
    Assert.Equal("paired-tok", result!.Token);
    Assert.False(result.IsBootstrapToken);
}
```

**Manager Integration Tests** (mock factory + connector):
```csharp
[Fact]
public async Task ConnectAsync_CreatesClient_TransitionsToConnecting()
{
    var mockFactory = new MockGatewayClientFactory();
    var manager = CreateManager(factory: mockFactory);

    await manager.ConnectAsync("gw-1");

    Assert.Equal(OverallConnectionState.Connecting, manager.CurrentSnapshot.OverallState);
    Assert.Single(mockFactory.CreatedClients);
}

[Fact]
public async Task SwitchGateway_DisconnectsOld_ConnectsNew()
{
    var manager = CreateConnectedManager(gatewayId: "gw-1");

    await manager.SwitchGatewayAsync("gw-2");

    Assert.Equal("gw-2", manager.CurrentSnapshot.GatewayId);
}

[Fact]
public async Task StaleEvent_FromOldClient_IsIgnored()
{
    var manager = CreateConnectedManager(gatewayId: "gw-1");
    var oldClient = manager.OperatorClient;

    await manager.SwitchGatewayAsync("gw-2");

    // Fire event from old client — should be silently ignored
    oldClient.SimulateStatusChanged(ConnectionStatus.Error);

    Assert.Equal(OverallConnectionState.Connecting, manager.CurrentSnapshot.OverallState);
    // NOT Error
}
```

**Pairing Flow Tests**:
```csharp
[Fact]
public async Task PairingRequired_BlocksAutoReconnect()
{
    var manager = CreateConnectingManager();
    manager.SimulatePairingRequired();

    Assert.Equal(OverallConnectionState.PairingRequired, manager.CurrentSnapshot.OverallState);

    // Simulate WebSocket drop during pairing
    manager.SimulateDisconnect();

    // Should NOT auto-reconnect while pairing is pending
    await Task.Delay(2000);
    Assert.NotEqual(OverallConnectionState.Connecting, manager.CurrentSnapshot.OverallState);
}
```

---

## 12. Migration Plan

Each step produces a shippable build with no regressions. Steps are ordered by
dependency and risk — foundation first, behavioral changes last.

### Phase 1: Foundation (No Behavioral Changes)

#### Step 1.1: Extract ICredentialResolver Interface and Implementation

**What**: Formalize the existing `GatewayCredentialResolver` behind the
`ICredentialResolver` interface. Add `ResolveNode` method.

**Files**:
- Create: `Services/ICredentialResolver.cs`
- Modify: `Services/GatewayCredentialResolver.cs` (implement interface)
- Modify: `App.xaml.cs` (use resolver instead of inline logic)

**Tests**: Existing `GatewayCredentialResolverTests` continue to pass; add node
resolution tests.

**Risk**: Low. Pure consolidation, no behavior change.

**Verify**: All 5 existing credential resolution call sites in App.xaml.cs now delegate
to `GatewayCredentialResolver.Resolve()`. Behavior is identical.

---

#### Step 1.2: Extract ConnectionStateMachine and GatewayConnectionSnapshot

**What**: Create the pure-logic state machine and immutable snapshot record.

**Files**:
- Create: `Services/Connection/ConnectionStateMachine.cs`
- Create: `Services/Connection/GatewayConnectionSnapshot.cs`
- Create: `Services/Connection/ConnectionTrigger.cs`
- Create: `Services/Connection/OverallConnectionState.cs`
- Create: `Services/Connection/RoleConnectionState.cs`

**Tests**: Comprehensive state machine unit tests — every valid transition, every invalid
transition, DeriveOverall logic.

**Risk**: None. New code, not wired to anything yet.

---

#### Step 1.3: Extract IGatewayClientFactory

**What**: Create factory interface and implementation that wraps the existing
`new OpenClawGatewayClient(...)` constructor call.

**Files**:
- Create: `Services/Connection/IGatewayClientFactory.cs`
- Create: `Services/Connection/GatewayClientFactory.cs`

**Tests**: Factory creates client with correct parameters.

**Risk**: None. New code, not wired yet.

---

#### Step 1.4: Create GatewayRegistry (Pure Data Store)

**What**: Create the gateway catalog with persistence but NO runtime state.

**Files**:
- Create: `Services/Connection/GatewayRegistry.cs`
- Create: `Services/Connection/GatewayRecord.cs`

**Tests**: Add/remove/update/persist/load round-trip tests.

**Risk**: Low. New code, persists to new file (`gateways.json`).

---

#### Step 1.5: Create ConnectionDiagnostics

**What**: Ring buffer for diagnostic events.

**Files**:
- Create: `Services/Connection/ConnectionDiagnostics.cs`
- Create: `Services/Connection/ConnectionDiagnosticEvent.cs`
- Create: `Services/Connection/IClock.cs`

**Tests**: Ring buffer capacity, event recording, overflow behavior.

**Risk**: None. New code, standalone.

---

### Phase 2: Manager (Gradual Ownership Transfer)

#### Step 2.0: Client Events for Token Receipt (Prerequisite)

**What**: Add `DeviceTokenReceived` and `HandshakeSucceeded` events to
`OpenClawGatewayClient` and `WindowsNodeClient`. Optionally accept an
`IDeviceIdentityStore` to decouple token persistence from the clients.

**Why this is a prerequisite**: Today, both clients internally write device tokens to
`DeviceIdentity` during the hello-ok handshake. The manager cannot own the token write
path until clients emit events instead of writing directly. Without this step, the
"single token write path" goal is impossible.

**Files**:
- Modify: `OpenClawGatewayClient.cs` — add `DeviceTokenReceived` event, fire it from
  `ProcessHelloOk()` after receiving `auth.deviceToken`. Optionally inject
  `IDeviceIdentityStore` to replace direct `DeviceIdentity` writes.
- Modify: `WindowsNodeClient.cs` — same pattern for node device token
- Create: `IDeviceIdentityStore.cs` — interface wrapping `DeviceIdentity.StoreDeviceToken`
  and `StoreNodeDeviceToken`
- Modify: `WebSocketClientBase.cs` — no changes needed (events are subclass-level)

**Interim behavior**: Until the manager subscribes, the clients continue to write tokens
internally (backward compatible). The events are additive.

```csharp
public class DeviceTokenReceivedEventArgs : EventArgs
{
    public string Token { get; }
    public string[] Scopes { get; }
    public string Role { get; }  // "operator" or "node"
}

// In OpenClawGatewayClient:
public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
```

**Tests**: Verify event fires with correct token/scopes/role during hello-ok processing.
Existing `OpenClawGatewayClientTests` and `WindowsNodeClientTests` still pass.

**Risk**: Low. Additive events, no behavior change. Existing token writes continue.

**Verify**: Existing tests pass. New event fires in test harness.

---

#### Step 2.1: Create GatewayConnectionManager Shell

**What**: Create the manager with its interface, wired to the state machine, but not yet
owning any clients. App still owns `_gatewayClient` during this step.

**Files**:
- Create: `Services/Connection/GatewayConnectionManager.cs`
- Create: `Services/Connection/IGatewayConnectionManager.cs`
- Modify: `App.xaml.cs` — create manager instance, subscribe to StateChanged,
  forward state to tray icon

**Tests**: Manager starts in Idle, exposes correct snapshot.

**Risk**: Low. Manager is passive observer initially.

**Verify**: App creates manager at startup. Tray icon updates from manager state.
Existing client code unchanged.

---

#### Step 2.2: Manager Owns Operator Client (3 Sub-Steps)

> **Do NOT attempt this as a single PR.** The old Step 2.2 combined client creation,
> event re-wiring, and InitializeGatewayClient deletion into one step. This is too
> large and too risky. Split into three sub-steps, each independently shippable.

##### Step 2.2a: Manager Creates Client, Exposes OperatorClient Property

**What**: Manager creates `OpenClawGatewayClient` via factory. Manager exposes
`OperatorClient` property (typed as `IOperatorGatewayClient?`). Manager fires
`OperatorClientChanged(old, new)` event when the client instance changes (connect,
disconnect, reconnect, gateway switch). App still ALSO creates its own client via
`InitializeGatewayClient()` — both run in parallel temporarily.

**Files**:
- Modify: `Services/Connection/GatewayConnectionManager.cs` — implement ConnectAsync
  and DisconnectAsync, create client via factory, manage generation guards
- Modify: `Services/Connection/IGatewayConnectionManager.cs` — add
  `event EventHandler<OperatorClientChangedEventArgs> OperatorClientChanged`

```csharp
public sealed class OperatorClientChangedEventArgs : EventArgs
{
    public IOperatorGatewayClient? OldClient { get; init; }
    public IOperatorGatewayClient? NewClient { get; init; }
}
```

**Tests**: Manager creates client on connect. `OperatorClient` is non-null after
connect. `OperatorClientChanged` fires with old=null, new=client. Disconnect sets
`OperatorClient` to null and fires with old=client, new=null.

**Risk**: Low. Manager runs alongside existing App code. No deletion yet.

**Verify**: Both App's client and manager's client connect successfully. Manager's
`StateChanged` fires. App's existing behavior unchanged.

---

##### Step 2.2b: App Subscribes to OperatorClientChanged and Re-Wires Handlers

**What**: App subscribes to `manager.OperatorClientChanged`. When it fires, App
unsubscribes its 27 event handlers from the old client and subscribes them to the new
client. App STOPS creating its own client via `InitializeGatewayClient()` and instead
uses the manager's client exclusively.

**Files**:
- Modify: `App.xaml.cs` — add `OnOperatorClientChanged(old, new)` handler that
  re-wires all 27 data event handlers (NotificationReceived, SessionsUpdated, etc.)
  from old client to new client. Remove the call to `InitializeGatewayClient()` from
  the 8 call sites, replacing with `manager.ConnectAsync()` calls.

**Implementation pattern**:
```csharp
private void OnOperatorClientChanged(object? s, OperatorClientChangedEventArgs e)
{
    DispatcherQueue.TryEnqueue(() =>
    {
        if (e.OldClient is { } old)
        {
            old.NotificationReceived -= OnNotificationReceived;
            old.SessionsUpdated -= OnSessionsUpdated;
            // ... all 27 handlers
        }
        if (e.NewClient is { } client)
        {
            client.NotificationReceived += OnNotificationReceived;
            client.SessionsUpdated += OnSessionsUpdated;
            // ... all 27 handlers
        }
    });
}
```

**Tests**: Verify that after `OperatorClientChanged` fires, App's handlers receive
events from the new client. Verify old client events are ignored.

**Risk**: **Medium-High**. This is the critical switchover point. The 27 handlers are
moved from direct subscription to indirect subscription via `OperatorClientChanged`.
Careful testing of each handler is required.

**Verify**: All 27 event handlers fire correctly. Notifications, sessions, usage,
models, presence — all update in the UI. Toast notifications appear. No duplicate
events.

---

##### Step 2.2c: Gradual Handler Migration (Optional, Per-Handler)

**What**: Over subsequent PRs, move individual event handlers from App.xaml.cs into
dedicated services or directly into the pages that consume them. This is not a single
step — each handler (or group of related handlers) is a separate small PR.

**Example moves**:
- `OnNotificationReceived` → extract to `NotificationDisplayService`
- `OnSessionsUpdated` / `OnUsageUpdated` → cache management moves to HubWindow or
  a dedicated `GatewayDataCache` class
- `OnGatewaySelfUpdated` → directly consumed by HomePage

**Risk**: Low per handler. Each is a small, testable change.

**Verify**: Each handler continues to function after moving. No regressions in the
specific feature it serves.

---

#### Step 2.3: Manager Owns Node Connection

**What**: Create `INodeConnector` implementation. Move node lifecycle from App to manager.

**Files**:
- Create: `Services/Connection/INodeConnector.cs`
- Create: `Services/Connection/NodeConnector.cs` (wraps existing NodeService init logic)
- Modify: `Services/Connection/GatewayConnectionManager.cs` — start/stop node on
  operator connected/disconnected
- Modify: `App.xaml.cs` — remove `InitializeNodeService()`, remove node event subscriptions,
  remove `EnsureNodeServiceForLocalGatewaySetup()`

**Tests**: Node starts when operator connects and node is enabled. Node stops on
disconnect. Node pairing events flow through manager state.

**Risk**: Medium. Node lifecycle is coupled to capabilities (canvas, screen capture, etc.)
which remain in `NodeService`. The `NodeConnector` delegates capability setup to
`NodeService` but owns the connection decision.

**Verify**: Node connects after operator. Capabilities work. Pairing approval works.

---

#### Step 2.4: Migrate Settings Credentials to GatewayRegistry

**What**: Implement the migration logic (§5) to move `Token` and `BootstrapToken` from
`SettingsData` to `GatewayRecord`.

**Files**:
- Modify: `GatewayRegistry.cs` — add `MigrateFromSettings()` method
- Modify: `App.xaml.cs` — call migration at startup before connecting
- Modify: `SettingsData.cs` — mark `Token` and `BootstrapToken` as `[Obsolete]`

**Tests**: Migration creates correct record. Migration is idempotent. Post-migration
settings have empty token fields.

**Risk**: Medium. Must handle all edge cases: no gateway URL, empty tokens, existing
identity files. Migration must be idempotent (safe to run twice).

**Verify**: Cold start with existing settings.json → gateway record created → connection
works. Second launch → no duplicate records.

---

### Phase 3: Unification (Single Path)

#### Step 3.1: Unify Setup Code Path

**What**: All 4 setup code entry points call `manager.ApplySetupCodeAsync()`.

**Files**:
- Modify: `Windows/HubWindow.xaml.cs` — connection page calls manager
- Modify: `Onboarding/` — wizard calls manager
- Modify: `Services/Connection/GatewayConnectionManager.cs` — implement ApplySetupCodeAsync

**Tests**: Setup code from each entry point produces same result.

**Risk**: Low. Behavioral consolidation; each path was doing roughly the same thing.

**Verify**: Apply setup code from connection page, onboarding, deep link, clipboard.
All succeed and connect.

---

#### Step 3.2: Unify Token Write Path

**What**: Only the manager writes tokens. Client handshake events go through manager
to DeviceIdentity.

**Files**:
- Modify: `GatewayConnectionManager.cs` — handle device token from hello-ok
- Modify: `App.xaml.cs` — remove `OnConnectionStatusChanged` token writes,
  remove `OnPairingStatusChanged` token writes

**Tests**: Token stored after successful handshake. Token not written from any other path.

**Risk**: Low-Medium. Must ensure the handshake callback still reaches DeviceIdentity.

---

#### Step 3.3: SSH Tunnel Integration

**What**: Extract `ISshTunnelManager`, wire into manager.

**Files**:
- Create: `Services/Connection/ISshTunnelManager.cs`
- Create: `Services/Connection/SshTunnelManager.cs` (wraps existing `SshTunnelService`)
- Modify: `GatewayConnectionManager.cs` — start tunnel before connect, stop on disconnect

**Tests**: Tunnel starts when settings require it. Gateway connects through tunnel URL.

**Risk**: Low. Wrapping existing service with interface.

---

### Phase 4: Cleanup

#### Step 4.1: Remove Dead Code from App.xaml.cs

**What**: Remove all connection-related code that's been moved to the manager.

**Files**:
- Modify: `App.xaml.cs` — remove InitializeGatewayClient, InitializeNodeService,
  all connection event handlers, credential resolution code, reconnect logic,
  connection status field

**Expected result**: App.xaml.cs drops from ~4800 lines to ~500-800 lines.

**Risk**: Low if previous steps are validated. This is pure deletion.

---

#### Step 4.2: Extract IOperatorGatewayClient Interface

**What**: Create the read-only facade interface. Modify `OpenClawGatewayClient` to
implement it.

**Files**:
- Create: `IOperatorGatewayClient.cs`
- Modify: `OpenClawGatewayClient.cs` — add interface
- Modify: `GatewayConnectionManager.cs` — expose via property

**Risk**: None. Additive interface extraction.

---

#### Step 4.3: OnSettingsSaved Simplification

**What**: Replace the "rebuild everything" OnSettingsSaved with targeted reconnect.

**Files**:
- Modify: `App.xaml.cs` — simplify OnSettingsSaved to check what changed and call
  manager.ReconnectAsync() only when connection settings changed

**Risk**: Low. Manager handles the complexity internally.

---

### Migration Timeline Estimate

| Phase | Steps | Estimated Effort | Risk |
|-------|-------|-----------------|------|
| Phase 1: Foundation | 1.1 – 1.5 | 1-2 weeks | Low |
| Phase 2: Manager | 2.0 – 2.4 (with 2.2a/b/c) | 3-5 weeks | Medium-High |
| Phase 3: Unification | 3.1 – 3.3 | 1-2 weeks | Low-Medium |
| Phase 4: Cleanup | 4.1 – 4.3 | 1 week | Low |
| **Total** | **~18 steps** | **6-10 weeks** | |

---

## 13. Error Taxonomy & Retry Policy

Every connection error maps to a defined category with explicit retry behavior and
user-visible state. This taxonomy prevents inconsistent handling where some errors
trigger reconnect storms and others silently swallow failures.

### Error Categories

| Error Category | Example | Retry? | Backoff | User-Visible State | Auto-Reconnect? |
|---------------|---------|--------|---------|-------------------|-----------------|
| **AuthFailure** | Invalid token, expired token, signature mismatch | No | N/A | `Error` + "Authentication failed" | No — requires user action (re-pair or new setup code) |
| **PairingPending** | Device not yet approved on gateway | No | N/A | `PairingRequired` + "Awaiting approval" | No — suppress reconnect until approval or rejection |
| **PairingRejected** | Gateway admin rejected device | No | N/A | `Error` + "Pairing rejected" | No — terminal for this identity |
| **RateLimited** | Gateway returned 429 or rate-limit close code | Yes | Extended (30s-5min) | `Error` + "Rate limited" | Yes, with extended backoff |
| **NetworkUnreachable** | DNS failure, TCP connect refused, no internet | Yes | Standard (1s-60s) | `Error` + "Network error" | Yes |
| **ServerClose** | Gateway sent clean WebSocket close (going away, restart) | Yes | Short (1s-4s) | `Connecting` + "Reconnecting" | Yes, immediate first retry |
| **ProtocolMismatch** | Unsupported hello version, unknown message type | No | N/A | `Error` + "Incompatible gateway" | No — requires app update |
| **MalformedMessage** | JSON parse failure, missing required fields | Yes | Standard | `Error` + "Protocol error" | Yes — may be transient |
| **InternalError** | Unhandled exception in handler, OOM | Yes | Standard | `Error` + "Internal error" | Yes, with logging |
| **SshTunnelFailure** | SSH process crashed, port binding failed | Yes | Standard | `Error` + "Tunnel failed" | Yes — tunnel restart, then WS reconnect |
| **Cancelled** | CancellationToken fired (user disconnect, gateway switch) | No | N/A | `Idle` or `Connecting` (to new) | N/A — intentional |
| **Disposed** | Manager.Dispose() called | No | N/A | `Idle` | No |

### Retry Policy

```csharp
public sealed class RetryPolicy
{
    // Standard backoff: 1s, 2s, 4s, 8s, 15s, 30s, 60s (cap)
    public static readonly int[] StandardBackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };

    // Extended backoff for rate limiting: 30s, 60s, 120s, 300s (cap)
    public static readonly int[] RateLimitBackoffMs = { 30000, 60000, 120000, 300000 };

    // Short backoff for clean server close: 1s, 2s, 4s (then standard)
    public static readonly int[] ServerCloseBackoffMs = { 1000, 2000, 4000 };

    public static bool ShouldRetry(ConnectionErrorCategory category) => category switch
    {
        ConnectionErrorCategory.AuthFailure => false,
        ConnectionErrorCategory.PairingPending => false,
        ConnectionErrorCategory.PairingRejected => false,
        ConnectionErrorCategory.ProtocolMismatch => false,
        ConnectionErrorCategory.Cancelled => false,
        ConnectionErrorCategory.Disposed => false,
        _ => true
    };
}
```

### Diagnostics Integration

Every error is recorded to `ConnectionDiagnostics` with:
- Category (from taxonomy above)
- Raw error message / exception type
- Whether auto-reconnect was scheduled or suppressed
- Backoff delay if scheduled
- Stack trace for `InternalError` category only

---

## 14. Event Ownership & Fan-Out

This table defines who **owns** each piece of event-derived state and who consumes it.
In the current architecture, App.xaml.cs owns everything. In the target architecture,
each state has exactly one owner.

### Ownership Table

| State / Data | Owner (Target) | Source Event | Consumers | Notes |
|-------------|---------------|-------------|-----------|-------|
| Connection state (snapshot) | `GatewayConnectionManager` | Internal state machine | App (tray), HubWindow, ConnectionPage | Immutable snapshot, UI dispatches |
| Toast notifications | `App.xaml.cs` (retained) | `operatorClient.NotificationReceived` | System toast API | App subscribes via `OperatorClientChanged` |
| Hub cached sessions | `HubWindow` or `GatewayDataCache` | `operatorClient.SessionsUpdated` | HomePage, session list | Move from App `_lastSessions` field |
| Hub cached usage | `HubWindow` or `GatewayDataCache` | `operatorClient.UsageUpdated` | HomePage | Move from App `_lastUsage` field |
| Hub cached nodes | `HubWindow` or `GatewayDataCache` | `operatorClient.NodesUpdated` | HomePage | Move from App `_lastNodes` field |
| Session previews | `HubWindow` or `GatewayDataCache` | `operatorClient.SessionPreviewUpdated` | Session detail page | Move from App `_sessionPreviews` field |
| Gateway self info | `HubWindow` | `operatorClient.GatewaySelfUpdated` | HomePage, ConnectionPage | Move from App `_lastGatewaySelf` |
| Chat credentials | `GatewayConnectionManager` | Derived from snapshot | ChatWindow | Manager exposes via `OperatorClient` |
| Tray icon state | `App.xaml.cs` (retained) | `manager.StateChanged` | System tray | App maps snapshot → icon/tooltip |
| Device pair list | `HubWindow` or dedicated page | `operatorClient.DevicePairListUpdated` | Device management page | Move from App `_lastDevicePairList` |
| Node pair list | `HubWindow` or dedicated page | `operatorClient.NodePairListUpdated` | Node management page | Move from App `_lastNodePairList` |
| Models list | `HubWindow` | `operatorClient.ModelsListUpdated` | Settings, chat model picker | Move from App `_lastModelsList` |
| Presence | `HubWindow` | `operatorClient.PresenceUpdated` | HomePage | Move from App `_lastPresence` |
| Activity stream | `HubWindow` | `operatorClient.ActivityChanged` | Activity page | Move from App `_currentActivity` |
| Agent events | `HubWindow` | `operatorClient.AgentEventReceived` | Agent page | Move from App `_agentEventsCache` |
| Diagnostics events | `ConnectionDiagnostics` | `manager.DiagnosticEvent` | DiagnosticsWindow | Ring buffer, not in App |
| Channel health | `HubWindow` | `operatorClient.ChannelHealthUpdated` | HomePage | Move from App `_lastChannels` |

### Fan-Out Pattern (Target)

```
operatorClient event fires (on WS thread)
    │
    ├──► App.xaml.cs handler (toast only — retained in App)
    │      └── DispatcherQueue.TryEnqueue → show toast
    │
    └──► HubWindow handler (data cache — moved from App)
           └── DispatcherQueue.TryEnqueue → update page controls
```

> **Guiding rule**: If state is consumed only by HubWindow pages, it should be owned
> by HubWindow (or a `GatewayDataCache` helper class created for this purpose). If
> state must be visible app-wide (tray icon, toasts), it stays in App.xaml.cs. If
> state is connection-layer internal, it stays in the manager.

---

## 15. Local Gateway Setup Integration

The `LocalGatewaySetup.cs` file (2,668 lines, 31 classes, 10+ single-implementation
interfaces) manages provisioning a local OpenClaw gateway in WSL. It has deep coupling
to the connection layer that must be addressed.

### Current Coupling Points

```
LocalGatewaySetup.cs interacts with:
  1. App._nodeService — creates/manages NodeService for local gateway
  2. App.InitializeGatewayClient() — called after local gateway is provisioned
  3. App.EnsureNodeServiceForLocalGatewaySetup() — duplicates node event wiring
  4. SettingsManager — writes GatewayUrl, BootstrapToken
  5. DeviceIdentity — provisions bootstrap token, pairs operator
  6. NodeService — auto-pairs node after operator pairing
```

### Target Integration

```
┌─────────────────────────────────────────────────────────────────┐
│  LocalGatewaySetup (unchanged internally for Phase 1-3)        │
│                                                                 │
│  After provisioning completes, calls:                           │
│    manager.ApplySetupCodeAsync(bootstrapSetupCode)              │
│                                                                 │
│  For auto-pair suppression:                                     │
│    manager.ConnectAsync(gatewayId,                              │
│        options: new ConnectOptions { SuppressAutoPair = true }) │
│                                                                 │
│  For node connector during local setup:                         │
│    Uses manager's INodeConnector — does NOT create its own      │
│    NodeService. The manager owns node lifecycle.                 │
└─────────────────────────────────────────────────────────────────┘
```

### Key Design Questions (Resolved)

**Q: Who owns NodeService during local setup?**
A: The manager owns node lifecycle via `INodeConnector`. Local setup calls
`manager.ConnectAsync()` which internally starts the node connector. Local setup
does NOT create its own `NodeService` — it uses the one managed by the connector.
`EnsureNodeServiceForLocalGatewaySetup()` is deleted.

**Q: How does auto-pair suppression work after manager owns node?**
A: The manager's `ConnectAsync` accepts an optional `ConnectOptions` parameter with
`SuppressAutoPair = true`. When set, the node connector skips sending the initial
pairing request and waits for explicit approval via the local setup flow. This
replaces the current pattern where `App.EnsureNodeServiceForLocalGatewaySetup()` sets
a flag on the node client.

**Q: Does local setup use the manager-managed node connector?**
A: Yes. Local setup provisions the gateway, writes credentials, then calls
`manager.ApplySetupCodeAsync()`. The manager creates the gateway record, resolves
credentials, and connects — including starting the node connector. Local setup can
observe progress via `manager.StateChanged`.

**Q: How are IsLocal / WSL / loopback URLs represented?**
A: `GatewayRecord.IsLocal = true` for gateways provisioned by local setup. The
`LocalGatewayUrlClassifier` (existing shared class) determines if a URL is local.
WSL-specific logic (port forwarding, health probes) remains in `LocalGatewaySetup`.

**Q: How is bootstrap provisioning represented?**
A: Local setup provisions a bootstrap token via its `IBootstrapTokenProvider` /
`IBootstrapTokenProvisioner` interfaces (existing). After provisioning, it constructs
a setup code string and calls `manager.ApplySetupCodeAsync()` — the same path as
manual setup code entry. The bootstrap token flows through `GatewayRecord.BootstrapToken`.

### Phase Timing

Local gateway setup integration is deferred to **Phase 3** or later. During Phase 2,
local setup continues to interact with App.xaml.cs directly. The migration path:

1. **Phase 2**: Local setup still calls `App.InitializeGatewayClient()` and
   `App.EnsureNodeServiceForLocalGatewaySetup()`. These methods are not yet deleted.
2. **Phase 3 (Step 3.4)**: Add `manager.ApplySetupCodeAsync()` call path for local
   setup. Remove `EnsureNodeServiceForLocalGatewaySetup()`. Local setup observes
   `manager.StateChanged` instead of wiring node events directly.
3. **Phase 4**: Delete remaining local setup coupling from App.xaml.cs.

---

## 16. What Stays in App.xaml.cs

After migration, App.xaml.cs retains only composition-root and window/tray duties:

```
App.xaml.cs (~500-800 lines)
  │
  │  Composition Root:
  │    Create SettingsManager
  │    Create GatewayRegistry (+ run migration if needed)
  │    Create GatewayConnectionManager (inject deps)
  │    Subscribe to manager.StateChanged → update tray
  │
  │  Window Management:
  │    ShowHubWindow(manager, settings)
  │    ShowChatWindow(manager.OperatorClient)
  │    ShowOnboardingWindow(manager)
  │    ShowDiagnosticsWindow(manager.Diagnostics)
  │    ShowVoiceWindow(voiceService)
  │
  │  Tray Icon:
  │    Build context menu
  │    Update icon/tooltip from connection state
  │    Handle tray click → show/hide hub
  │
  │  App Lifecycle:
  │    Single-instance mutex
  │    Protocol activation (deep links → manager.ApplySetupCodeAsync)
  │    OnSettingsSaved (targeted: reconnect if connection settings changed)
  │    App exit / cleanup
  │
  │  Standalone Services (not connection-related):
  │    GlobalHotkeyService
  │    UpdateChecker / AppUpdater
  │    Toast notification display (subscribes to manager.OperatorClient events)
  │    VoiceService management
  │
  │  NOT in App.xaml.cs:
  │    ✗ _gatewayClient field
  │    ✗ _nodeService field
  │    ✗ _currentStatus field
  │    ✗ InitializeGatewayClient()
  │    ✗ InitializeNodeService()
  │    ✗ 27 gateway client event subscriptions
  │    ✗ 7 node service event subscriptions
  │    ✗ Credential resolution
  │    ✗ Setup code decode/apply
  │    ✗ Token write logic
  │    ✗ Reconnect timer
  │    ✗ SSH tunnel management
  │    ✗ Connection state tracking
```

---

## 17. Appendix: Component Inventory

### Before → After File Map

```
BEFORE (current):
  src/OpenClaw.Tray.WinUI/
    App.xaml.cs                          (4808 lines — god class)
    Services/
      SettingsManager.cs                 (settings + connection data)
      NodeService.cs                     (node + capabilities)
      SshTunnelService.cs               (SSH tunnel)
      GatewayCredentialResolver.cs       (partial resolver)
      GatewayDiscoveryService.cs         (mDNS discovery)
      LocalGatewaySetup/
        LocalGatewaySetup.cs             (2668 lines, 31 classes)
    Windows/
      HubWindow.xaml.cs                  (receives mutable refs from App)
    Pages/
      ConnectionPage.xaml.cs             (setup code logic inline)
      HomePage.xaml.cs                   (status display)
    Onboarding/
      (setup code logic duplicated)

  src/OpenClaw.Shared/
    OpenClawGatewayClient.cs             (~1800 lines)
    WindowsNodeClient.cs                 (node WebSocket client)
    WebSocketClientBase.cs               (shared transport)
    DeviceIdentity.cs                    (462 lines, keypair + tokens)
    Models.cs                            (ConnectionStatus, PairingStatus, etc.)
    SettingsData.cs                      (50+ fields including Token, BootstrapToken)
    GatewayUrlHelper.cs                  (URL decode/sanitize)


AFTER (target):
  src/OpenClaw.Tray.WinUI/
    App.xaml.cs                          (~500-800 lines — composition root only)
    Services/
      SettingsManager.cs                 (app prefs only, no Token/BootstrapToken)
      NodeService.cs                     (capabilities only, not connection)
      GatewayDiscoveryService.cs         (unchanged)
      LocalGatewaySetup/
        LocalGatewaySetup.cs             (unchanged for now)
      Connection/                        ★ NEW DIRECTORY
        IGatewayConnectionManager.cs     ★ manager interface
        GatewayConnectionManager.cs      ★ connection lifecycle owner
        ConnectionStateMachine.cs        ★ formal state machine
        GatewayConnectionSnapshot.cs     ★ immutable state record
        ConnectionTrigger.cs             ★ transition triggers enum
        OverallConnectionState.cs        ★ overall state enum
        RoleConnectionState.cs           ★ per-role state enum
        IGatewayClientFactory.cs         ★ client factory interface
        IGatewayClientLifecycle.cs       ★ lifecycle interface (manager-side)
        GatewayClientFactory.cs          ★ client factory impl
        INodeConnector.cs                ★ node connector interface
        NodeConnector.cs                 ★ node connector impl
        NodeConnectionMode.cs            ★ Gateway/McpOnly/Disabled enum
        ICredentialResolver.cs           ★ resolver interface
        CredentialResolver.cs            ★ canonical resolver
        ISshTunnelManager.cs             ★ tunnel manager interface
        SshTunnelManager.cs              ★ tunnel manager impl
        GatewayRegistry.cs              ★ pure data catalog
        GatewayRecord.cs                 ★ per-gateway record (incl SshTunnelConfig)
        ConnectionDiagnostics.cs         ★ ring buffer
        ConnectionDiagnosticEvent.cs     ★ event record
        ConnectionErrorCategory.cs       ★ error taxonomy enum
        RetryPolicy.cs                   ★ retry/backoff rules
        SettingsChangeImpact.cs          ★ settings diff classifier
        IClock.cs                        ★ time abstraction
        SetupCodeResult.cs               ★ setup result type
        IOperatorGatewayClient.cs        ★ read-only client facade
        IDeviceIdentityStore.cs          ★ token write abstraction
    Windows/
      HubWindow.xaml.cs                  (receives IGatewayConnectionManager)
    Pages/
      ConnectionPage.xaml.cs             (calls manager.ApplySetupCodeAsync)
      HomePage.xaml.cs                   (reads manager.CurrentSnapshot)
    Onboarding/
      (calls manager.ApplySetupCodeAsync)

  src/OpenClaw.Shared/
    OpenClawGatewayClient.cs             (implements IOperatorGatewayClient + IGatewayClientLifecycle)
    WindowsNodeClient.cs                 (adds DeviceTokenReceived event)
    WebSocketClientBase.cs               (unchanged; future: accept IWebSocketTransport)
    DeviceIdentity.cs                    (unchanged)
    Models.cs                            (ConnectionStatus preserved for compat)
    SettingsData.cs                      (Token/BootstrapToken/PreferredGatewayId marked [Obsolete])
    GatewayUrlHelper.cs                  (unchanged)
    IFileSystem.cs                       ★ filesystem abstraction
    IDeviceIdentityReader.cs             ★ identity reader interface
    IWebSocketTransport.cs               ★ transport abstraction (future, for integration tests)

  tests/
    OpenClaw.Tray.Tests/
      Connection/                        ★ NEW DIRECTORY
        ConnectionStateMachineTests.cs   ★ exhaustive transition tests
        CredentialResolverTests.cs        ★ resolution priority tests (device token > shared > bootstrap)
        GatewayRegistryTests.cs          ★ CRUD + persistence + migration idempotency
        ConnectionDiagnosticsTests.cs    ★ ring buffer tests
        GatewayConnectionManagerTests.cs ★ lifecycle integration tests
        SetupCodeFlowTests.cs            ★ end-to-end setup tests
        PairingFlowTests.cs              ★ pairing state machine tests
        StaleEventGuardTests.cs          ★ generation token tests
        RetryPolicyTests.cs              ★ error category → retry mapping
        SettingsChangeImpactTests.cs     ★ settings diff classification
        NodeConnectorTests.cs            ★ MCP-only mode, capability registration
```

### Lines of Code Estimate (Target)

| Component | Estimated Lines | Complexity |
|-----------|----------------|------------|
| GatewayConnectionManager | 400-600 | High |
| ConnectionStateMachine | 150-200 | Medium |
| GatewayConnectionSnapshot | 60-80 | Low |
| CredentialResolver | 80-100 | Low |
| GatewayClientFactory + Lifecycle | 60-80 | Low |
| NodeConnector (incl MCP-only) | 250-350 | Medium |
| SshTunnelManager | 80-120 | Low |
| GatewayRegistry | 150-200 | Medium |
| ConnectionDiagnostics | 100-150 | Low |
| ErrorTaxonomy + RetryPolicy | 80-120 | Low |
| SettingsChangeImpact | 60-80 | Low |
| IDeviceIdentityStore | 30-50 | Low |
| Enums + records | 100-130 | Low |
| Interfaces | 120-160 | Low |
| **Total new code** | **~1,720-2,420** | |
| **Code removed from App.xaml.cs** | **~3,500-4,000** | |
| **Net change** | **~-1,600 to -2,300** | |

---

## Design Decisions Log

| Decision | Rationale | Alternatives Considered |
|----------|-----------|------------------------|
| Manager exposes `OperatorClient` property instead of forwarding all 27 events | Avoids 27 forwarding methods; UI code naturally null-checks | Forward all events (too much boilerplate) |
| Immutable snapshot record for state | Thread-safe without locks; enables structural equality | Mutable state + locks (error-prone) |
| Generation token paired with per-operation CTS | Handles stale events + enables cancellation of in-flight ops | Volatile long alone (not atomic on x86), weak refs (GC-dependent) |
| Semaphore for transition serialization; events fire outside semaphore | Async-compatible; prevents deadlocks when subscribers call back into manager | Lock (blocks thread, can't fire events outside), Channel (over-engineered) |
| GatewayRecord stores tokens, not SettingsData | Clean separation: settings = prefs, registry = connection catalog | Keep tokens in settings (status quo, causes confusion) |
| ICredentialResolver as interface, not static class | Testable without filesystem; mockable for manager tests | Static class (existing pattern, not testable) |
| Device token wins over shared/bootstrap in resolution order | Prevents paired-device downgrade; bootstrap re-auth would strip admin scopes | Shared token first (original design, breaks paired devices) |
| Split Step 2.2 into 2.2a/b/c | Atomic switchover is too risky as single PR; gradual re-wiring is safer | Single PR (too large, 27 handlers + 8 call sites in one diff) |
| Add Step 2.0 for client token events | Manager cannot own token writes until clients expose the data | Skip and let clients keep writing (blocks "single write path" goal) |
| NodeConnector separate from NodeService | NodeService has WinUI deps (canvas, screen capture); connector is pure connection | Merge into one (untestable) |
| Degraded state for operator-ok + node-error | Operator still functional; don't show Error when half the system works | Error (too alarming), Connected (hides node problem) |
| Factory returns IGatewayClientLifecycle + IOperatorGatewayClient | Full mockability without needing OpenClawGatewayClient in tests | Return concrete type (not mockable), single interface (leaks lifecycle to UI) |
| Per-gateway SSH tunnel config in GatewayRecord | Each gateway may need different tunnel settings; avoids global SSH config conflicts | Keep in SettingsData (shared config, can't differ per gateway) |
| Gateway ID as identity directory name | GUIDs are path-safe and unique; URL-derived names have encoding issues | URL-derived name (path-unsafe chars), hash (opaque, debugging hard) |
| Active gateway is single source of truth in GatewayRegistry | Eliminates SettingsData.PreferredGatewayId vs Registry disagreement | Keep both (conflicts between two sources) |
| Settings diff model for OnSettingsSaved | Only reconnect when connection settings change; avoid full teardown for UI pref changes | Always reconnect (current behavior, wasteful and disruptive) |
| MCP-only mode as first-class NodeConnectionMode | MCP-only nodes don't need WebSocket; connector reports Connected locally | Always require WS (breaks MCP-only scenarios) |
| Local setup uses manager.ApplySetupCodeAsync | Same path as manual setup; no special-cased code paths | Keep separate path (duplicated logic, harder to maintain) |

---

*This document is the authoritative reference for all connection-layer refactoring.
Changes to the architecture should be reflected here before implementation begins.
Each migration step should reference the relevant section of this document in its
PR description.*

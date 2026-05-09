# Connection Architecture — Implementation Audit & Handoff

> **Date**: 2026-05-09  
> **Branch**: `user/ranjeshj/connection2`  
> **Commits**: 22 commits since `master`  
> **Tests**: 829 tray + 1442 shared = 2,271 total (some shared tests need fixing — see below)

---

## What Was Done

### Phase 1: Foundation (Steps 1.1–1.5) ✅ Fully Implemented
All new types, no behavioral changes to existing code.

- `ICredentialResolver` + `CredentialResolver` — northstar resolution order: DeviceToken > SharedGatewayToken > BootstrapToken
- `ConnectionStateMachine` — operator/node sub-FSMs, `DeriveOverall()`, full transition table
- `GatewayConnectionSnapshot` — immutable record with `OverallConnectionState`, `RoleConnectionState`
- `ConnectionTrigger` enum — all operator + node triggers
- `IGatewayClientFactory` + `GatewayClientFactory` — wraps `OpenClawGatewayClient` construction
- `IGatewayClientLifecycle` — separates lifecycle (manager) from data (UI)
- `GatewayRegistry` + `GatewayRecord` — pure data catalog with `gateways.json` persistence
- `IFileSystem` / `RealFileSystem` — filesystem abstraction for testability
- `IDeviceIdentityReader` / `DeviceIdentityFileReader` — testable identity reads
- `ConnectionDiagnostics` — ring buffer with `IClock`/`SystemClock`
- `ConnectionDiagnosticEvent` — timestamped event record

### Phase 2: Manager Ownership Transfer (Steps 2.0–2.4) ✅ Operator Only
- `DeviceTokenReceived` + `HandshakeSucceeded` + `PairingRequired` + `V2SignatureFallback` events added to `OpenClawGatewayClient`
- `DeviceTokenReceived` + `HandshakeSucceeded` events added to `WindowsNodeClient`
- `GatewayConnectionManager` — owns operator connection lifecycle with:
  - State machine integration
  - Generation-guarded event handlers (stale event protection)
  - `_transitionSemaphore` for serialized transitions
  - `OperatorClientChanged` event for App handler re-wiring
  - `ApplySetupCodeAsync` — single canonical setup code path
  - `DiagnosticTeeLogger` — pipes client logs to diagnostics timeline
  - v3→v2 signature fallback with per-gateway persistence
  - Device token clearing on setup code apply
  - Root-to-per-gateway identity file sync
- `INodeConnector` + `NodeConnector` + `NodeConnectionMode` — **created but NOT wired**
- `ISshTunnelManager` + `SshTunnelManager` — **created but NOT wired**
- `DeviceIdentityStore` — **created but NOT wired**
- Settings→Registry migration (`MigrateFromSettings`) with idempotency + identity file copy

### Phase 3: Unification (Steps 3.1–3.3) ⚠️ Partially Wired
- `ApplySetupCodeAsync` — implemented and wired in ConnectionPage + ConnectionStatusWindow ✅
- `DeviceIdentityStore` — created but token writes still happen inside clients directly ⚠️
- `ISshTunnelManager` — created but SSH tunnel still managed by App's `_sshTunnelService` ⚠️

### Phase 4: Cleanup (Steps 4.1–4.3) ⚠️ Partially Done
- `UnsubscribeGatewayEvents()` removed from App ✅
- Disconnect paths use `_connectionManager.DisconnectAsync()` ✅
- `IOperatorGatewayClient` interface extracted, `OpenClawGatewayClient` implements ✅
- `SettingsChangeImpact` classifier created with tests ✅ — **but NOT wired into `OnSettingsSaved`** ⚠️
- **App.xaml.cs is still 4,726 lines** (target: 500-800) ❌

### Protocol Fixes (from connection-analysis-report.md)
- Node client uses `SignConnectPayloadV3` (was legacy `SignPayload`) ✅
- Operator: v3→v2 fallback (try v3 first, flag persists per session) ✅
- `TryGetErrorDetailCode` for structured error parsing ✅
- `IsTerminalAuthDetailCode` for `AUTH_BOOTSTRAP_TOKEN_INVALID`, `AUTH_DEVICE_TOKEN_MISMATCH`, etc. ✅
- `BuildDebugPayload`/`SignPayload` marked `[Obsolete]` ✅

### UI
- `ConnectionStatusWindow` — two-column layout with state machine visual, gateway catalog, credentials, setup code section, event timeline with direction arrows and word wrap ✅
- `ConnectionPage` upgraded — disconnect button, setup code decode preview, recent gateways from registry, live state via `IGatewayConnectionManager.StateChanged` ✅
- Gateway discovery (mDNS scan) section removed from ConnectionPage ✅

### Live Testing Bugs Found & Fixed (11 total)
1. Bootstrap token misclassification in `ApplySetupCodeAsync`
2. Settings bridge overwrites registry on startup
3. Stale bootstrap token auto-connect on startup
4. Identity file not synced to per-gateway directory
5. v3 signature rejected by this gateway version
6. v2 retry on same socket wastes bootstrap token
7. Bootstrap tokens can't request `operator.admin` scope
8. `PairingRequired` shown as `Error` state
9. WebSocket disconnect during pairing transitions to Error
10. Terminal auth errors (`AUTH_BOOTSTRAP_TOKEN_INVALID`) cause reconnect loops
11. `SendConnectMessageAsync` exceptions silently swallowed

---

## What Is NOT Done

### Critical Gaps (Northstar promises not delivered)

| Gap | Impact | Effort |
|-----|--------|--------|
| **Node connection NOT managed by manager** | `InitializeNodeService()` still in App.xaml.cs (15+ call sites). `INodeConnector`/`NodeConnector` exist but are unused. | High |
| **SSH tunnel NOT managed by manager** | `_sshTunnelService` still in App.xaml.cs. `ISshTunnelManager` exists but unused. | Medium |
| **App.xaml.cs still 4,726 lines** | Target was 500-800. Only `UnsubscribeGatewayEvents` was removed (~30 lines). | High |
| **`OnSettingsSaved` still does full reconnect** | `SettingsChangeImpact` classifier exists with tests but is NOT wired. | Low |
| **Settings `Token`/`BootstrapToken`/`GatewayUrl` still exist** | Legacy fields with a bridge in `InitializeGatewayClient`. Source of bugs. | Medium |
| **Token writes still in clients** | `IDeviceIdentityStore` exists but `OpenClawGatewayClient` and `WindowsNodeClient` still write tokens to `DeviceIdentity` directly. | Medium |
| **Local gateway setup still uses App directly** | `LocalGatewaySetup` calls `App.InitializeGatewayClient()` and `EnsureNodeServiceForLocalGatewaySetup()`. | Medium |
| **Step 2.2c handler migration not done** | 27 event handlers still in App's `OnOperatorClientChanged`. Should move to dedicated services/pages. | Low-Medium |
| **Auto-reconnect after pairing approval** | User must click Connect manually after gateway approves. Should reconnect automatically. | Low |

### Test Failures to Fix
- 3 shared tests still failing (scope assertions + pairing event assertions need GatewayClientTestHelper.Client property)
- The test helper needs a `Client` property exposing the underlying `OpenClawGatewayClient` for event subscription

### Files Created But Not Wired
```
src/OpenClaw.Tray.WinUI/Services/Connection/
  NodeConnector.cs          — created, not used by App
  INodeConnector.cs         — created, not used
  NodeConnectionMode.cs     — created, not used
  ISshTunnelManager.cs      — created, not used
  SshTunnelManager.cs       — created, not used (wraps SshTunnelService)
  DeviceIdentityStore.cs    — created, not used
  SettingsChangeImpact.cs   — created + tested, not wired into OnSettingsSaved
```

### Identity File Architecture Issue
- `OpenClawGatewayClient` always creates `DeviceIdentity` from `%APPDATA%/OpenClawTray` (root)
- Per-gateway identity directories exist but client doesn't use them
- Current workaround: sync root → per-gateway on connect
- Long-term: client constructor should accept identity path parameter

---

## Branch State

```
22 commits on user/ranjeshj/connection2 ahead of master

Key commits:
- Steps 1.1-1.5: Foundation types
- Steps 2.0-2.4: Manager + client events + registry migration
- Steps 3.1-3.3: Setup code unification + token/SSH interfaces
- Steps 4.1-4.3: Dead code removal + IOperatorGatewayClient + SettingsChangeImpact
- Protocol fixes: v3 signatures, cascade removal, structured error codes
- ConnectionStatusWindow + ConnectionPage UX upgrade
- QR connectivity: 11 bugs found and fixed via live testing
```

---

## How to Test

### QR Code Flow (clean state)
```powershell
# 1. Clear state
$appdata = Join-Path $env:APPDATA "OpenClawTray"
Remove-Item "$appdata\gateways.json", "$appdata\gateways", "$appdata\device-key-ed25519.json" -Recurse -Force -EA 0
# Also clear settings tokens:
$s = Get-Content "$appdata\settings.json" | ConvertFrom-Json; $s.Token=""; $s.BootstrapToken=""; $s.GatewayUrl=""; $s | ConvertTo-Json | Set-Content "$appdata\settings.json"

# 2. Build and run
./build.ps1
Start-Process "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-arm64\OpenClaw.Tray.WinUI.exe"

# 3. On gateway host: openclaw qr --url ws://localhost:18790
# 4. Paste setup code in Connection Status window → Connect
# 5. Approve on gateway host
# 6. Click Connect again → should get hello-ok + Connected
```

### Reconnect Flow (has stored device token)
```powershell
# Just restart the app — it should auto-connect from registry + stored device token
# Note: if gateway was restarted, device token will be stale → AUTH_DEVICE_TOKEN_MISMATCH
# Fix: paste a fresh QR code (clears old tokens automatically)
```

using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Read-only facade for the operator gateway client.
/// Exposes data events and request methods needed by UI consumers
/// without exposing connection lifecycle methods.
/// </summary>
public interface IOperatorGatewayClient
{
    // ─── Data Events ───
    event EventHandler<OpenClawNotification>? NotificationReceived;
    event EventHandler<AgentActivity>? ActivityChanged;
    event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<GatewayUsageInfo>? UsageUpdated;
    event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
    event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
    event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    event EventHandler<JsonElement>? CronListUpdated;
    event EventHandler<JsonElement>? CronStatusUpdated;
    event EventHandler<JsonElement>? SkillsStatusUpdated;
    event EventHandler<JsonElement>? ConfigUpdated;
    event EventHandler<JsonElement>? ConfigSchemaUpdated;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<PairingListInfo>? NodePairListUpdated;
    event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
    event EventHandler<PresenceEntry[]>? PresenceUpdated;
    event EventHandler<JsonElement>? AgentsListUpdated;
    event EventHandler<JsonElement>? AgentFilesListUpdated;
    event EventHandler<JsonElement>? AgentFileContentUpdated;

    // ─── Query ───
    string? OperatorDeviceId { get; }
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }

    // ─── Connection events (from WebSocketClientBase) ───
    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    event EventHandler? HandshakeSucceeded;
}

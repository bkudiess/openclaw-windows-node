using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>
/// Subset of <see cref="OpenClawGatewayClient"/> needed by
/// <see cref="OpenClawChatDataProvider"/>. Exposed as an interface so the
/// provider can be unit-tested without a live WebSocket connection.
/// </summary>
public interface IChatGatewayBridge : IDisposable
{
    bool IsConnected { get; }
    ConnectionStatus CurrentStatus { get; }
    SessionInfo[] GetSessionList();
    ModelsListInfo? GetCurrentModelsList();

    Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId);
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey);
    Task SendChatAbortAsync(string runId);

    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
}

/// <summary>
/// Production bridge wrapping a real <see cref="OpenClawGatewayClient"/>.
/// </summary>
public sealed class GatewayClientChatBridge : IChatGatewayBridge
{
    private readonly OpenClawGatewayClient _client;
    private readonly EventHandler<ConnectionStatus> _statusChangedHandler;
    private readonly EventHandler<SessionInfo[]> _sessionsUpdatedHandler;
    private readonly EventHandler<ChatMessageInfo> _chatMessageReceivedHandler;
    private readonly EventHandler<AgentEventInfo> _agentEventReceivedHandler;
    private readonly EventHandler<ModelsListInfo> _modelsListUpdatedHandler;
    private ConnectionStatus _currentStatus = ConnectionStatus.Disconnected;
    private ModelsListInfo? _currentModels;
    private bool _disposed;

    public GatewayClientChatBridge(OpenClawGatewayClient client)
    {
        _client = client;
        _statusChangedHandler = (s, e) =>
        {
            _currentStatus = e;
            StatusChanged?.Invoke(s, e);
        };
        _sessionsUpdatedHandler = (s, e) => SessionsUpdated?.Invoke(s, e);
        _chatMessageReceivedHandler = (s, e) => ChatMessageReceived?.Invoke(s, e);
        _agentEventReceivedHandler = (s, e) => AgentEventReceived?.Invoke(s, e);
        _modelsListUpdatedHandler = (s, e) =>
        {
            _currentModels = e;
            ModelsListUpdated?.Invoke(s, e);
        };

        _client.StatusChanged += _statusChangedHandler;
        _client.SessionsUpdated += _sessionsUpdatedHandler;
        _client.ChatMessageReceived += _chatMessageReceivedHandler;
        _client.AgentEventReceived += _agentEventReceivedHandler;
        _client.ModelsListUpdated += _modelsListUpdatedHandler;
    }

    public bool IsConnected => _client.IsConnectedToGateway;
    public ConnectionStatus CurrentStatus => _currentStatus;
    public SessionInfo[] GetSessionList() => _client.GetSessionList();
    public ModelsListInfo? GetCurrentModelsList() => _currentModels;

    public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId) =>
        _client.SendChatMessageAsync(message, sessionKey, sessionId);

    public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) =>
        _client.RequestChatHistoryAsync(sessionKey);

    public Task SendChatAbortAsync(string runId) => _client.SendChatAbortAsync(runId);

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.StatusChanged -= _statusChangedHandler;
        _client.SessionsUpdated -= _sessionsUpdatedHandler;
        _client.ChatMessageReceived -= _chatMessageReceivedHandler;
        _client.AgentEventReceived -= _agentEventReceivedHandler;
        _client.ModelsListUpdated -= _modelsListUpdatedHandler;

        StatusChanged = null;
        SessionsUpdated = null;
        ChatMessageReceived = null;
        AgentEventReceived = null;
        ModelsListUpdated = null;
    }
}

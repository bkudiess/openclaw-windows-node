using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClawTray.Chat;

internal static class AccessibilityHistoryCollisionFixture
{
    internal const string FixtureName = "history-collision";
    internal const string ThreadId = "accessibility-main";

    internal static OpenClawChatDataProvider Create(
        string isolatedDataDirectory,
        Action<Action>? post = null)
    {
        ValidateIsolationGate(isolatedDataDirectory);
        return CreateCore(isolatedDataDirectory, post).Provider;
    }

    internal static (OpenClawChatDataProvider Provider, Bridge GatewayBridge) CreateWithBridge(
        string isolatedDataDirectory,
        Action<Action>? post = null)
    {
        ValidateIsolationGate(isolatedDataDirectory);
        return CreateCore(isolatedDataDirectory, post);
    }

    internal static OpenClawChatDataProvider CreateForTesting(
        string isolatedDataDirectory,
        Func<string, string?> environmentLookup)
    {
        ValidateIsolationGate(isolatedDataDirectory, environmentLookup);
        return CreateCore(isolatedDataDirectory, post: null).Provider;
    }

    internal static (OpenClawChatDataProvider Provider, Bridge GatewayBridge) CreateWithBridgeForTesting(
        string isolatedDataDirectory,
        Func<string, string?> environmentLookup)
    {
        ValidateIsolationGate(isolatedDataDirectory, environmentLookup);
        return CreateCore(isolatedDataDirectory, post: null);
    }

    private static (OpenClawChatDataProvider Provider, Bridge GatewayBridge) CreateCore(
        string isolatedDataDirectory,
        Action<Action>? post)
    {
        Directory.CreateDirectory(isolatedDataDirectory);

        var bridge = new Bridge();
        var provider = new OpenClawChatDataProvider(
            bridge,
            post,
            toolMetaCacheFilePath: Path.Combine(
                isolatedDataDirectory,
                "accessibility-history-collision-tool-metadata.json"),
            attachmentMetaCacheFilePath: Path.Combine(
                isolatedDataDirectory,
                "accessibility-history-collision-attachment-metadata.json"),
            lastChatStateFilePath: Path.Combine(
                isolatedDataDirectory,
                "accessibility-history-collision-last-chat-state.json"));

        provider.CacheToolMeta(
            ThreadId,
            tsMs: 100,
            toolName: "Exec",
            label: "Verified structured history call",
            toolCallId: "history-tool-0",
            toolArgs: new JsonObject
            {
                ["command"] = "verified structured id: history-tool-0",
            },
            identityStrength: ChatToolIdentityStrength.Specific);
        provider.CacheToolMeta(
            ThreadId,
            tsMs: 200,
            toolName: "Bash",
            label: "Flattened history output",
            toolCallId: "unverified-cached-flat-id",
            toolArgs: new JsonObject
            {
                ["command"] = "synthetic flattened id: history-tool-1",
            },
            identityStrength: ChatToolIdentityStrength.Specific,
            runId: "unverified-cached-run");

        return (provider, bridge);
    }

    private static void ValidateIsolationGate(string isolatedDataDirectory)
        => ValidateIsolationGate(
            isolatedDataDirectory,
            Environment.GetEnvironmentVariable);

    private static void ValidateIsolationGate(
        string isolatedDataDirectory,
        Func<string, string?> environmentLookup)
    {
        var configuredDataDirectory =
            environmentLookup("OPENCLAW_TRAY_DATA_DIR");
        if (!string.Equals(
                environmentLookup("OPENCLAW_ACCESSIBILITY_TEST_CHAT"),
                "1",
                StringComparison.Ordinal)
            || !string.Equals(
                environmentLookup("OPENCLAW_ACCESSIBILITY_TEST_CHAT_FIXTURE"),
                FixtureName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configuredDataDirectory)
            || !string.Equals(
                Path.GetFullPath(isolatedDataDirectory),
                Path.GetFullPath(configuredDataDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The history collision fixture requires the isolated accessibility test gate.");
        }
    }

    internal sealed class Bridge : IChatGatewayBridge
    {
        private static readonly SessionInfo[] Sessions =
        [
            new()
            {
                Key = ThreadId,
                IsMain = true,
                DisplayName = "Accessibility session",
                Status = "active",
                Model = "test-model",
            },
        ];

        public bool IsConnected => true;
        public ConnectionStatus CurrentStatus => ConnectionStatus.Connected;
        public string MainSessionKey => ThreadId;
        public bool HasHandshakeSnapshot => true;
        public int HistoryRequestCount { get; private set; }
        public List<string?> RequestedHistoryKeys { get; } = [];

        public SessionInfo[] GetSessionList() => Sessions;
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }

        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey)
        {
            HistoryRequestCount++;
            RequestedHistoryKeys.Add(sessionKey);
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionId = "accessibility-history-collision-session",
                SessionKey = ThreadId,
                Messages =
                [
                    new ChatMessageInfo
                    {
                        Role = "assistant",
                        Ts = 100,
                        ToolContent =
                        [
                            new ChatToolContentInfo
                            {
                                Kind = ChatToolContentKind.Call,
                                CallId = "history-tool-0",
                                ToolName = "Exec",
                                Args = ParseArgs(
                                    """{"command":"verified structured id: history-tool-0"}"""),
                            },
                        ],
                    },
                    new ChatMessageInfo
                    {
                        Role = "toolresult",
                        Text = "flattened output owned by history-tool-1",
                        Ts = 200,
                    },
                ],
            });
        }

        public Task SendChatMessageAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null)
            => Task.CompletedTask;

        public Task<ChatSendResult> SendChatMessageForRunAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null,
            string? idempotencyKey = null)
            => Task.FromResult(new ChatSendResult());

        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null)
            => Task.FromResult(new CommandCatalog { IsSupported = false });

        public Task PatchSessionModelAsync(string sessionKey, string model) =>
            Task.CompletedTask;

        public Task ClearSessionModelAsync(string sessionKey) =>
            Task.CompletedTask;

        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) =>
            Task.CompletedTask;

        public Task SendChatAbortAsync(string runId, string? sessionKey = null) =>
            Task.CompletedTask;

        public Task ResolveExecApprovalAsync(string approvalId, string decision) =>
            Task.CompletedTask;

        public void Dispose() { }

        public event EventHandler<ConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionInfo[]>? SessionsUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionCommandResult>? SessionCommandCompleted
        {
            add { }
            remove { }
        }

        public event EventHandler<ChatMessageInfo>? ChatMessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AgentEventInfo>? AgentEventReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<ModelsListInfo>? ModelsListUpdated
        {
            add { }
            remove { }
        }

        private static JsonElement ParseArgs(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}

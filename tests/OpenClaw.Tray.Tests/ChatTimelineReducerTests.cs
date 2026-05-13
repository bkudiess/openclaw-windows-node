using OpenClaw.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatTimelineReducerTests
{
    [Fact]
    public void ToolStart_BeginsTurnWhenLifecycleStartWasMissed()
    {
        var state = ChatTimelineState.Initial();

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatToolStartEvent("powershell", "powershell"));

        Assert.True(updated.TurnActive);
        Assert.Single(updated.Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, updated.Entries[0].Kind);
    }

    [Fact]
    public void Error_EndsActiveTurn()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatThinkingEvent(string.Empty));

        var updated = ChatTimelineReducer.Apply(
            state,
            new ChatErrorEvent("Agent error"));

        Assert.False(updated.TurnActive);
        Assert.Null(updated.ActiveAssistantId);
        Assert.Null(updated.ActiveReasoningId);
        Assert.Null(updated.ActiveToolCallId);
        Assert.Null(updated.PendingPermission);
        Assert.Single(updated.Entries);
        Assert.Equal(ChatTimelineItemKind.Status, updated.Entries[0].Kind);
        Assert.Equal(ChatTone.Error, updated.Entries[0].Tone);
    }

    [Fact]
    public void FinalAssistant_UpdatesStreamingAssistantAfterTurnEnd()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageDeltaEvent("partial"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("final", ReconcilePrevious: true));

        Assert.Single(updated.Entries);
        Assert.Equal("final", updated.Entries[0].Text);
        Assert.False(updated.Entries[0].IsStreaming);
    }

    [Fact]
    public void DuplicateFinalAssistant_DoesNotCreateSecondEntry()
    {
        var state = ChatTimelineReducer.Apply(
            ChatTimelineState.Initial(),
            new ChatMessageEvent("final"));
        state = ChatTimelineReducer.Apply(state, new ChatTurnEndEvent());

        var updated = ChatTimelineReducer.Apply(state, new ChatMessageEvent("final", ReconcilePrevious: true));

        Assert.Single(updated.Entries);
        Assert.Equal("final", updated.Entries[0].Text);
    }

    [Fact]
    public void AddLocalUser_CapsTrackedNonces()
    {
        var state = ChatTimelineState.Initial();
        for (var i = 0; i < 300; i++)
        {
            state = ChatTimelineReducer.AddLocalUser(state, $"message {i}", $"nonce-{i}");
        }

        Assert.Equal(256, state.LocalNonces.Count);
        Assert.Contains("nonce-299", state.LocalNonces);
    }
}

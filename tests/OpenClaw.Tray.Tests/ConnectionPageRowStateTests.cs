using OpenClaw.Connection;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Behavior tests for the saved-gateway row state predicates. These guard
/// the regression where the overflow menu offered Disconnect during
/// Disconnecting (re-entering teardown), and the related cleanup that
/// gave the active row a proper Error badge instead of dropping back to
/// [Connect] indistinguishably from inactive rows.
/// </summary>
public sealed class ConnectionPageRowStateTests
{
    [Theory]
    [InlineData(OverallConnectionState.Connected)]
    [InlineData(OverallConnectionState.Ready)]
    [InlineData(OverallConnectionState.Degraded)]
    [InlineData(OverallConnectionState.Connecting)]
    [InlineData(OverallConnectionState.PairingRequired)]
    [InlineData(OverallConnectionState.Error)]
    public void CanDisconnectFromBadge_TrueForLiveAndPendingStates(OverallConnectionState state)
    {
        Assert.True(ConnectionPageRowState.CanDisconnectFromBadge(state),
            $"{state} should expose Disconnect (state has a live/pending connection to tear down).");
    }

    [Fact]
    public void CanDisconnectFromBadge_FalseWhileDisconnecting()
    {
        // Disconnecting is the exact state that USED to receive Disconnect
        // and race the connection manager — this is the regression guard.
        Assert.False(ConnectionPageRowState.CanDisconnectFromBadge(OverallConnectionState.Disconnecting),
            "Disconnecting must NOT offer Disconnect — teardown is already in flight.");
    }

    [Fact]
    public void CanDisconnectFromBadge_FalseWhenIdle()
    {
        // Idle rows render [Connect], not a badge — there's nothing to
        // disconnect from.
        Assert.False(ConnectionPageRowState.CanDisconnectFromBadge(OverallConnectionState.Idle),
            "Idle rows render [Connect]; Disconnect is meaningless.");
    }

    [Theory]
    [InlineData(OverallConnectionState.Connected)]
    [InlineData(OverallConnectionState.Ready)]
    [InlineData(OverallConnectionState.Degraded)]
    [InlineData(OverallConnectionState.Connecting)]
    [InlineData(OverallConnectionState.PairingRequired)]
    [InlineData(OverallConnectionState.Disconnecting)]
    [InlineData(OverallConnectionState.Error)]
    public void HasActiveRowBadge_TrueForNonIdleStates(OverallConnectionState state)
    {
        // The Error case is the regression added in this commit pass: an
        // active row in Error used to render [Connect] indistinguishably
        // from an inactive row, hiding the failure.
        Assert.True(ConnectionPageRowState.HasActiveRowBadge(state),
            $"{state} should render a status badge so the user can see the live state.");
    }

    [Fact]
    public void HasActiveRowBadge_FalseWhenIdle()
    {
        Assert.False(ConnectionPageRowState.HasActiveRowBadge(OverallConnectionState.Idle),
            "Idle is the only state without a badge — the row falls back to [Connect].");
    }
}

using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the gateway-vs-local divergence detector. We don't paper over the
/// gateway's stale data anywhere in UI — the gateway is the source of
/// truth — but we DO log a clear warning when its self-entry has drifted
/// from what we just registered, so the gateway-side bug stays visible.
/// </summary>
public sealed class GatewayDivergenceCheckerTests
{
    private static GatewayNodeInfo MakeNode(string id, bool online, IEnumerable<string> caps, IEnumerable<string> cmds)
    {
        var capsList = caps.ToList();
        var cmdsList = cmds.ToList();
        return new GatewayNodeInfo
        {
            NodeId = id,
            DisplayName = $"node-{id}",
            IsOnline = online,
            Capabilities = capsList,
            Commands = cmdsList,
            CapabilityCount = capsList.Count,
            CommandCount = cmdsList.Count,
        };
    }

    [Fact]
    public void NoLiveLocal_ReturnsNull()
    {
        // Without a live local snapshot we can't compare; caller skips logging.
        var report = GatewayDivergenceChecker.Compare(System.Array.Empty<GatewayNodeInfo>(), "local-id", null, false);
        Assert.Null(report);
    }

    [Fact]
    public void NoLocalId_ReturnsNull()
    {
        var live = MakeNode("local-id", true, ["system"], ["system.run"]);
        var report = GatewayDivergenceChecker.Compare(System.Array.Empty<GatewayNodeInfo>(), null, live, true);
        Assert.Null(report);
    }

    [Fact]
    public void InSync_NoDrift()
    {
        var live = MakeNode("local-id", true,
            caps: ["app", "system", "browser"],
            cmds: ["app.status", "system.run", "browser.proxy"]);
        var gw = MakeNode("local-id", true,
            caps: ["app", "system", "browser"],
            cmds: ["app.status", "system.run", "browser.proxy"]);

        var report = GatewayDivergenceChecker.Compare([gw], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.False(report!.HasAnyDrift);
        Assert.Equal(1, report.DuplicateSelfEntries);
        Assert.False(report.MissingFromGateway);
        Assert.False(report.ConnectedFlagMismatch);
    }

    [Fact]
    public void StaleCommands_DetectedAsDrift()
    {
        // Gateway has the first-pair snapshot (no system.run); we just toggled it on.
        var live = MakeNode("local-id", true,
            caps: ["app", "system"],
            cmds: ["app.status", "system.notify", "system.run"]);
        var gw = MakeNode("local-id", true,
            caps: ["app", "system"],
            cmds: ["app.status", "system.notify"]);

        var report = GatewayDivergenceChecker.Compare([gw], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.True(report!.HasAnyDrift);
        Assert.Contains("system.run", report.CommandsOnlyInLocal);
        Assert.Empty(report.CommandsOnlyInGateway);
    }

    [Fact]
    public void DuplicateSelfEntries_Flagged()
    {
        // Re-pair leaves multiple paired-registry rows for the same NodeId.
        var live = MakeNode("local-id", true, ["system"], ["system.run"]);
        var stale1 = MakeNode("local-id", false, ["system"], ["system.run"]);
        var stale2 = MakeNode("local-id", false, ["system"], ["system.run"]);

        var report = GatewayDivergenceChecker.Compare([stale1, stale2], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.Equal(2, report!.DuplicateSelfEntries);
        Assert.True(report.HasAnyDrift);
    }

    [Fact]
    public void MissingFromGateway_Flagged()
    {
        var live = MakeNode("local-id", true, ["system"], ["system.run"]);
        var someoneElse = MakeNode("remote-pc", true, ["app"], ["app.status"]);

        var report = GatewayDivergenceChecker.Compare([someoneElse], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.True(report!.MissingFromGateway);
        Assert.Equal(0, report.DuplicateSelfEntries);
        Assert.True(report.HasAnyDrift);
    }

    [Fact]
    public void ConnectedFlagMismatch_Flagged()
    {
        // Live websocket is up, but gateway still says connected=false.
        var live = MakeNode("local-id", true, ["system"], ["system.run"]);
        var gw = MakeNode("local-id", online: false, caps: ["system"], cmds: ["system.run"]);

        var report = GatewayDivergenceChecker.Compare([gw], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.True(report!.ConnectedFlagMismatch);
        Assert.False(report.GatewayConnected!.Value);
        Assert.True(report.LiveConnected);
        Assert.True(report.HasAnyDrift);
    }

    [Fact]
    public void DuplicateEntries_FreshestRecordChosenForComparison()
    {
        // When duplicates exist, comparison should use the entry with the
        // larger command set (most recent handshake "wins" for the diff).
        var live = MakeNode("local-id", true, ["system"], ["system.run", "system.notify"]);
        var oldStale = MakeNode("local-id", false, ["system"], ["system.notify"]);
        var freshish = MakeNode("local-id", false, ["system"], ["system.run", "system.notify"]);

        var report = GatewayDivergenceChecker.Compare([oldStale, freshish], "local-id", live, liveConnected: true);

        Assert.NotNull(report);
        Assert.Empty(report!.CommandsOnlyInLocal); // freshish matches live cmds
        Assert.Equal(2, report.GatewayCommandCount);
    }

    [Fact]
    public void FormatLogLine_IncludesAllDriftSignals()
    {
        var report = new GatewayDivergenceChecker.DivergenceReport(
            DuplicateSelfEntries: 2,
            MissingFromGateway: false,
            ConnectedFlagMismatch: true,
            GatewayConnected: false,
            LiveConnected: true,
            CapsOnlyInLocal: ["browser"],
            CapsOnlyInGateway: [],
            CommandsOnlyInLocal: ["system.run"],
            CommandsOnlyInGateway: [],
            GatewayCommandCount: 2,
            LiveCommandCount: 3);

        var line = GatewayDivergenceChecker.FormatLogLine(report);

        Assert.Contains("duplicate_self_entries=2", line);
        Assert.Contains("connected_mismatch", line);
        Assert.Contains("caps_only_in_local=[browser]", line);
        Assert.Contains("cmds_only_in_local(count=1)=[system.run]", line);
        Assert.Contains("totals(gateway_cmds=2, live_cmds=3)", line);
    }
}

using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the rules used by <c>ConnectionPage</c> and <c>InstancesPage</c> to
/// decide which capability categories to display. The single source of truth
/// is the live registered set the node is advertising; when that's missing
/// we fall back to settings toggles so we still show what *will* be exposed
/// on the next reconnect.
///
/// These rules are what guarantee the two pages always show the same chip
/// count for the same node — a regression here makes the UI feel inconsistent
/// ("Connection says 7, Instances says 9, why?").
/// </summary>
public sealed class NodeCapabilityDisplayTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private SettingsManager NewSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-display-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new SettingsManager(dir);
    }

    [Fact]
    public void LiveCategories_AreUsedVerbatim_WhenProvided()
    {
        // When the node is connected, the live registration is what we're
        // actually forwarding — settings toggles are stale until the next
        // RegisterCapabilities runs, so prefer the live snapshot.
        var live = new[] { "app", "system", "browser", "device" };
        var s = NewSettings();
        s.NodeSystemRunEnabled = false; // would normally hide system, but live wins

        var result = NodeCapabilityDisplay.BuildExposedCategories(live, s);

        Assert.Equal(new[] { "system", "browser", "app", "device" }, result);
    }

    [Fact]
    public void NoLiveCategories_FallsBackToSettings()
    {
        var s = NewSettings();
        s.NodeSystemRunEnabled = true;
        s.NodeBrowserProxyEnabled = true;
        s.NodeCameraEnabled = false;
        s.NodeCanvasEnabled = false;
        s.NodeScreenEnabled = false;
        s.NodeLocationEnabled = false;
        s.NodeTtsEnabled = false;
        s.NodeSttEnabled = false;

        var result = NodeCapabilityDisplay.BuildExposedCategories(null, s);

        // Always-on app + device + the two enabled toggles, system-tools first.
        Assert.Equal(new[] { "system", "browser", "app", "device" }, result);
    }

    [Fact]
    public void EmptyLiveCategories_FallsBackToSettings()
    {
        var s = NewSettings();
        s.NodeSystemRunEnabled = false;
        s.NodeBrowserProxyEnabled = false;
        s.NodeCameraEnabled = false;
        s.NodeCanvasEnabled = false;
        s.NodeScreenEnabled = false;
        s.NodeLocationEnabled = false;
        s.NodeTtsEnabled = false;
        s.NodeSttEnabled = false;

        var result = NodeCapabilityDisplay.BuildExposedCategories(Array.Empty<string>(), s);

        // Always-on app + device still appear (they're registered unconditionally).
        Assert.Equal(new[] { "app", "device" }, result);
    }

    [Fact]
    public void OffToggle_DoesNotAppear_InSettingsFallback()
    {
        // User requirement: "if it's off, don't show anywhere".
        var s = NewSettings();
        s.NodeSystemRunEnabled = false;
        s.NodeCameraEnabled = false;

        var result = NodeCapabilityDisplay.BuildExposedCategories(null, s);

        Assert.DoesNotContain("system", result);
        Assert.DoesNotContain("camera", result);
    }

    [Fact]
    public void CanonicalOrder_PutsSystemFirst()
    {
        var live = new[] { "tts", "system", "stt", "browser", "canvas" };
        var result = NodeCapabilityDisplay.BuildExposedCategories(live, NewSettings());

        Assert.Equal("system", result[0]);
        Assert.Equal("browser", result[1]);
        Assert.Equal("canvas", result[2]);
    }

    [Fact]
    public void DuplicateLiveCategories_AreCollapsed()
    {
        // Defensive: a buggy registration with duplicates shouldn't double-count chips.
        var live = new[] { "system", "system", "app", "app" };
        var result = NodeCapabilityDisplay.BuildExposedCategories(live, NewSettings());

        Assert.Equal(new[] { "system", "app" }, result);
    }

    [Fact]
    public void UnknownCategory_AppearsLast_AlphabeticallySorted()
    {
        // Forward-compatible: a new category not in our canonical order
        // shouldn't crash, it should just sort after the known ones.
        var live = new[] { "system", "zeta", "alpha" };
        var result = NodeCapabilityDisplay.BuildExposedCategories(live, NewSettings());

        Assert.Equal(new[] { "system", "alpha", "zeta" }, result);
    }

    [Fact]
    public void GetChipLabel_MapsKnownCategories()
    {
        Assert.Equal("System tools", NodeCapabilityDisplay.GetChipLabel("system"));
        Assert.Equal("Browser",      NodeCapabilityDisplay.GetChipLabel("browser"));
        Assert.Equal("TTS",          NodeCapabilityDisplay.GetChipLabel("tts"));
        Assert.Equal("STT",          NodeCapabilityDisplay.GetChipLabel("stt"));
        Assert.Equal("App",          NodeCapabilityDisplay.GetChipLabel("app"));
        Assert.Equal("Device",       NodeCapabilityDisplay.GetChipLabel("device"));
    }

    [Fact]
    public void GetChipLabel_CapitalizesUnknownCategory()
    {
        Assert.Equal("Foo", NodeCapabilityDisplay.GetChipLabel("foo"));
    }

    [Fact]
    public void GetSlug_MapsSystemToSystemTools()
    {
        // Display in "Providing N capabilities: …" line keeps the marketing
        // name "system-tools" so users see the same label as the toggle.
        Assert.Equal("system-tools", NodeCapabilityDisplay.GetSlug("system"));
        Assert.Equal("browser",      NodeCapabilityDisplay.GetSlug("browser"));
        Assert.Equal("camera",       NodeCapabilityDisplay.GetSlug("camera"));
    }

    [Fact]
    public void ConnectionAndInstances_Agree_OnTheSameLiveSnapshot()
    {
        // Both pages source from BuildExposedCategories with the same live
        // snapshot, so given the same input they MUST produce identical lists.
        // This is the regression test for the original "9 vs 7" inconsistency.
        var live = new[] { "app", "system", "canvas", "screen", "camera", "location", "stt", "device", "browser" };
        var s = NewSettings();

        var connectionPageList = NodeCapabilityDisplay.BuildExposedCategories(live, s);
        var instancesPageList = NodeCapabilityDisplay.BuildExposedCategories(live, s);

        Assert.Equal(connectionPageList, instancesPageList);
        Assert.Equal(9, connectionPageList.Count);
    }

    // ── BuildGatewayViewCategories (ConnectionPage = gateway view) ─────────

    private static OpenClaw.Shared.GatewayNodeInfo MakeGwNode(string id, IEnumerable<string> caps)
    {
        var capsList = caps.ToList();
        return new OpenClaw.Shared.GatewayNodeInfo
        {
            NodeId = id,
            DisplayName = $"node-{id}",
            IsOnline = true,
            Capabilities = capsList,
            Commands = new List<string>(),
            CapabilityCount = capsList.Count,
            CommandCount = 0,
        };
    }

    [Fact]
    public void GatewayView_FallsBackToSettings_WhenGatewayListEmpty()
    {
        // Before the first node.list arrives we don't want a blank chip
        // strip — show the settings-derived list (what we'll register).
        var s = NewSettings();
        s.NodeSystemRunEnabled = true;
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            gatewayNodes: System.Array.Empty<OpenClaw.Shared.GatewayNodeInfo>(),
            localNodeId: "local-id",
            settingsFallback: s);
        Assert.Contains("system", result);
        Assert.Contains("app", result);
    }

    [Fact]
    public void GatewayView_UsesGatewaySelfEntry_WhenAvailable()
    {
        // The whole point: ignore live registration entirely, mirror what
        // the gateway returned for our node.
        var self = MakeGwNode("local-id", ["app", "device", "system"]);
        var other = MakeGwNode("remote-pc", ["app", "browser"]);
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            new[] { other, self }, "local-id", settingsFallback: null);
        Assert.Equal(new[] { "system", "app", "device" }, result);
    }

    [Fact]
    public void GatewayView_StaleGatewayCaps_ShownAsIs_NoFallbackToSettings()
    {
        // Even if the user toggled system on in settings, if gateway doesn't
        // have it we mirror gateway (not settings). This is the new
        // contract — ConnectionPage shows the gateway's stale truth.
        var s = NewSettings();
        s.NodeSystemRunEnabled = true; // user wants system on
        var self = MakeGwNode("local-id", ["app", "device"]); // gateway is stale
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            new[] { self }, "local-id", settingsFallback: s);
        Assert.DoesNotContain("system", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GatewayView_NoSelfEntry_ReturnsEmpty_NotSettingsFallback()
    {
        // Gateway has a list but doesn't know about us — matches the
        // tray menu's behaviour of hiding the device. Returning settings
        // here would re-introduce the inconsistency we're trying to remove.
        var s = NewSettings();
        s.NodeSystemRunEnabled = true;
        var other = MakeGwNode("remote-pc", ["app"]);
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            new[] { other }, "local-id", settingsFallback: s);
        Assert.Empty(result);
    }

    [Fact]
    public void GatewayView_DuplicateSelfEntries_FreshestWins()
    {
        // Re-pair leaves multiple paired-registry rows; we use the one
        // with the most capabilities so we don't show a regressing chip
        // count when the gateway hasn't deduped yet.
        var older = MakeGwNode("local-id", ["app"]);
        var newer = MakeGwNode("local-id", ["app", "system", "browser"]);
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            new[] { older, newer }, "local-id", settingsFallback: null);
        Assert.Equal(3, result.Count);
        Assert.Contains("system", result);
    }

    [Fact]
    public void GatewayView_NoLocalId_ReturnsEmpty()
    {
        // Without knowing our own NodeId we can't pick the right entry —
        // show nothing rather than guessing.
        var self = MakeGwNode("local-id", ["app", "system"]);
        var result = NodeCapabilityDisplay.BuildGatewayViewCategories(
            new[] { self }, localNodeId: null, settingsFallback: null);
        Assert.Empty(result);
    }
}

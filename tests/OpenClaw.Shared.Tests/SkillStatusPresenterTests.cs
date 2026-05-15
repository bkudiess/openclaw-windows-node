using System.Linq;
using System.Text.Json;
using OpenClawTray.Windows;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class SkillStatusPresenterTests
{
    private static JsonElement Json(string text) =>
        JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Parse_ReturnsEmpty_ForUnrecognizedShape()
    {
        var rows = SkillStatusPresenter.Parse(Json("\"not-an-object\""));
        Assert.Empty(rows);
    }

    [Fact]
    public void Parse_ReadsTopLevelSkillsArray()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ { "name": "a", "description": "desc", "emoji": "🅰️" } ] }
            """));
        var row = Assert.Single(rows);
        Assert.Equal("a", row.Name);
        Assert.Equal("desc", row.Description);
        Assert.Equal("🅰️", row.Emoji);
        Assert.Equal("🅰️ a", row.DisplayName);
    }

    [Fact]
    public void Parse_TolleratesPayloadWrapping()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "payload": { "skills": [ { "name": "wrapped" } ] } }
            """));
        var row = Assert.Single(rows);
        Assert.Equal("wrapped", row.Name);
    }

    [Fact]
    public void Parse_TolleratesBarePayloadArray()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "payload": [ { "name": "bare-array" } ] }
            """));
        Assert.Equal("bare-array", Assert.Single(rows).Name);
    }

    [Fact]
    public void Parse_MapsSourceLabelsLikeMac()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "b", "source": "openclaw-bundled" },
                { "name": "p", "source": "openclaw-plugin" },
                { "name": "w", "source": "openclaw-workspace" },
                { "name": "m", "source": "openclaw-managed" },
                { "name": "x", "source": "openclaw-extra" },
                { "name": "node", "source": "windows-node" },
                { "name": "custom", "source": "totally-custom" }
            ] }
            """));
        Assert.Equal("Bundled",   rows.Single(r => r.Name == "b").SourceLabel);
        Assert.Equal("Plugin",    rows.Single(r => r.Name == "p").SourceLabel);
        Assert.Equal("Workspace", rows.Single(r => r.Name == "w").SourceLabel);
        Assert.Equal("Managed",   rows.Single(r => r.Name == "m").SourceLabel);
        Assert.Equal("Extra",     rows.Single(r => r.Name == "x").SourceLabel);
        Assert.Equal("Node",      rows.Single(r => r.Name == "node").SourceLabel);
        // Unknown sources pass through verbatim.
        Assert.Equal("totally-custom", rows.Single(r => r.Name == "custom").SourceLabel);
    }

    [Fact]
    public void State_Ready_WhenEligibleAndNothingMissing()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ { "name": "ok", "eligible": true } ] }
            """));
        Assert.Equal(SkillRowState.Ready, Assert.Single(rows).State);
    }

    [Fact]
    public void State_Disabled_BeatsEverything()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "off",
                "disabled": true,
                "eligible": true,
                "missing": { "bins": ["pwsh"], "env": ["KEY"], "config": [] }
            } ] }
            """));
        Assert.Equal(SkillRowState.Disabled, Assert.Single(rows).State);
    }

    [Fact]
    public void State_NeedsInstall_WhenMissingBinsHaveInstallOption()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "graph",
                "eligible": false,
                "missing": { "bins": ["mgc"], "env": [], "config": [] },
                "install": [
                    { "id": "winget", "kind": "winget", "label": "Install via winget", "bins": ["mgc"] }
                ]
            } ] }
            """));
        var row = Assert.Single(rows);
        Assert.Equal(SkillRowState.NeedsInstall, row.State);
        var opts = row.InstallOptionsForMissingBins;
        Assert.Single(opts);
        Assert.Equal("winget", opts[0].Id);
    }

    [Fact]
    public void State_NeedsInstall_IncludesInstallOptionsWithNoDeclaredBins()
    {
        // An install option with no `bins` is treated as relevant (Mac matches this behavior in
        // SkillsSettings.swift: `if option.bins.isEmpty { return true }`).
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "bundle",
                "eligible": false,
                "missing": { "bins": ["xyz"], "env": [], "config": [] },
                "install": [ { "id": "any", "kind": "brew", "label": "Install", "bins": [] } ]
            } ] }
            """));
        Assert.Single(Assert.Single(rows).InstallOptionsForMissingBins);
    }

    [Fact]
    public void State_NeedsEnv_WhenMissingBinsEmptyButEnvMissing()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "secret",
                "eligible": false,
                "primaryEnv": "OPENAI_API_KEY",
                "missing": { "bins": [], "env": ["OPENAI_API_KEY"], "config": [] }
            } ] }
            """));
        Assert.Equal(SkillRowState.NeedsEnv, Assert.Single(rows).State);
    }

    [Fact]
    public void State_NeedsSetup_WhenMissingBinsButNoInstallOptionMatches()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "stuck",
                "eligible": false,
                "missing": { "bins": ["weird-bin"], "env": [], "config": [] },
                "install": [
                    { "id": "x", "kind": "brew", "label": "irrelevant", "bins": ["other-bin"] }
                ]
            } ] }
            """));
        // Bins missing but no install option covers them — falls through to NeedsSetup, not NeedsInstall.
        Assert.Equal(SkillRowState.NeedsSetup, Assert.Single(rows).State);
    }

    [Fact]
    public void Parse_ReadsConfigChecks()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ {
                "name": "with-config",
                "configChecks": [
                    { "path": "skills.entries.foo.enabled", "satisfied": true,  "value": true },
                    { "path": "skills.entries.foo.token",   "satisfied": false, "value": "" }
                ]
            } ] }
            """));
        var checks = Assert.Single(rows).ConfigChecks;
        Assert.Equal(2, checks.Count);
        Assert.True(checks[0].Satisfied);
        Assert.Equal("true", checks[0].ValueDisplay);
        Assert.False(checks[1].Satisfied);
    }

    [Fact]
    public void Filter_All_ReturnsEveryRowSortedAlphabetically()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "Beta",  "eligible": true },
                { "name": "alpha", "eligible": true },
                { "name": "Gamma", "eligible": true }
            ] }
            """));
        var filtered = SkillStatusPresenter.Filter(rows, SkillsFilter.All);
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, filtered.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Filter_Ready_DropsAnythingNotReady()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "ready",  "eligible": true },
                { "name": "missing-bin", "eligible": false, "missing": { "bins": ["mgc"], "env": [], "config": [] },
                  "install": [{ "id": "w", "kind": "winget", "label": "Install", "bins": ["mgc"] }] },
                { "name": "off", "disabled": true, "eligible": true }
            ] }
            """));
        var filtered = SkillStatusPresenter.Filter(rows, SkillsFilter.Ready);
        var only = Assert.Single(filtered);
        Assert.Equal("ready", only.Name);
    }

    [Fact]
    public void Filter_NeedsSetup_IncludesInstallEnvAndOtherUnreadyStates()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "needs-install", "eligible": false,
                  "missing": { "bins": ["mgc"], "env": [], "config": [] },
                  "install": [{ "id": "w", "kind": "winget", "label": "Install", "bins": ["mgc"] }] },
                { "name": "needs-env", "eligible": false,
                  "missing": { "bins": [], "env": ["KEY"], "config": [] } },
                { "name": "needs-setup", "eligible": false,
                  "missing": { "bins": ["unknown"], "env": [], "config": [] } },
                { "name": "ready",       "eligible": true },
                { "name": "disabled",    "disabled": true }
            ] }
            """));
        var filtered = SkillStatusPresenter.Filter(rows, SkillsFilter.NeedsSetup);
        Assert.Equal(
            new[] { "needs-env", "needs-install", "needs-setup" },
            filtered.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Filter_Disabled_OnlyReturnsAdminDisabledSkills()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "off",   "disabled": true },
                { "name": "on",    "eligible": true }
            ] }
            """));
        var only = Assert.Single(SkillStatusPresenter.Filter(rows, SkillsFilter.Disabled));
        Assert.Equal("off", only.Name);
    }

    [Fact]
    public void Parse_HandlesEnabledFlagAsInverseOfDisabled()
    {
        // Newer gateways send `enabled` instead of `disabled`. enabled=false means admin-disabled.
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [
                { "name": "explicit-on",  "enabled": true,  "eligible": true },
                { "name": "explicit-off", "enabled": false, "eligible": true }
            ] }
            """));
        Assert.Equal(SkillRowState.Ready,    rows.Single(r => r.Name == "explicit-on").State);
        Assert.Equal(SkillRowState.Disabled, rows.Single(r => r.Name == "explicit-off").State);
    }

    [Fact]
    public void Parse_TreatsMissingFieldsAsEmpty()
    {
        // Minimum-shape skill: only name. Everything else must default to empty/false.
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ { "name": "tiny" } ] }
            """));
        var row = Assert.Single(rows);
        Assert.Empty(row.MissingBins);
        Assert.Empty(row.MissingEnv);
        Assert.Empty(row.MissingConfig);
        Assert.Empty(row.Install);
        Assert.Empty(row.ConfigChecks);
        Assert.False(row.Eligible);
        Assert.False(row.Disabled);
        Assert.False(row.Bundled);
    }

    [Fact]
    public void DisplayName_IsNameAloneWhenEmojiMissing()
    {
        var rows = SkillStatusPresenter.Parse(Json("""
            { "skills": [ { "name": "plain" } ] }
            """));
        Assert.Equal("plain", Assert.Single(rows).DisplayName);
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelConfigPatchBuilderTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static List<(string, object)> Updates(params (string, object)[] vs) => vs.ToList();

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public void BuildPatch_FreshChannel_CreatesChannelBlock_AndSetsEnabled()
    {
        var existing = Json("""{ "agents": [] }""");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "12345:abc")));

        Assert.Null(result.BlockedReason);
        Assert.NotNull(result.Patch);
        var patch = result.Patch!.Value;
        Assert.Equal("12345:abc", patch.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
        Assert.True(patch.GetProperty("channels").GetProperty("telegram").GetProperty("enabled").GetBoolean());
        // Existing top-level keys are preserved verbatim.
        Assert.Equal(JsonValueKind.Array, patch.GetProperty("agents").ValueKind);
    }

    [Fact]
    public void BuildPatch_UpdatesTargetChannel_LeavesOtherChannelsAlone()
    {
        var existing = Json("""
            {
              "channels": {
                "discord":  { "webhookUrl": "https://discord.com/api/webhooks/123/abc", "enabled": true },
                "telegram": { "botToken": "old-token", "enabled": false }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "new-token")));

        Assert.Null(result.BlockedReason);
        var patch = result.Patch!.Value;
        var channels = patch.GetProperty("channels");
        Assert.Equal("new-token", channels.GetProperty("telegram").GetProperty("botToken").GetString());
        Assert.True(channels.GetProperty("telegram").GetProperty("enabled").GetBoolean()); // forced to true
        Assert.Equal("https://discord.com/api/webhooks/123/abc",
            channels.GetProperty("discord").GetProperty("webhookUrl").GetString());
        Assert.True(channels.GetProperty("discord").GetProperty("enabled").GetBoolean()); // untouched
    }

    [Fact]
    public void BuildPatch_MultilinePath_IsSplitIntoTrimmedArray()
    {
        var existing = Json("""{ "channels": {} }""");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "nostr",
            Updates(
                ("channels.nostr.nsec", "nsec1abc"),
                ("channels.nostr.relays", "  wss://relay.damus.io \n\n wss://relay.nostr.band ")),
            multilineDotPaths: new HashSet<string> { "channels.nostr.relays" });

        Assert.Null(result.BlockedReason);
        var relays = result.Patch!.Value.GetProperty("channels").GetProperty("nostr").GetProperty("relays");
        Assert.Equal(JsonValueKind.Array, relays.ValueKind);
        var arr = relays.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "wss://relay.damus.io", "wss://relay.nostr.band" }, arr);
    }

    [Fact]
    public void BuildPatch_EnabledField_IsBoolNotString()
    {
        var existing = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "12345:abc")));

        var enabled = result.Patch!.Value.GetProperty("channels").GetProperty("telegram").GetProperty("enabled");
        Assert.Equal(JsonValueKind.True, enabled.ValueKind);
    }

    [Fact]
    public void BuildPatch_EmptyCachedConfig_StartsFresh()
    {
        var empty = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            empty, "discord",
            Updates(("channels.discord.webhookUrl", "https://discord.com/api/webhooks/123/abc")));

        Assert.Null(result.BlockedReason);
        var channels = result.Patch!.Value.GetProperty("channels");
        Assert.Equal("https://discord.com/api/webhooks/123/abc",
            channels.GetProperty("discord").GetProperty("webhookUrl").GetString());
    }

    [Fact]
    public void BuildPatch_NonObjectCachedConfig_StartsFresh()
    {
        // Null root, array root, etc. — be tolerant.
        var notObject = Json("null");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            notObject, "telegram",
            Updates(("channels.telegram.botToken", "abc")));

        Assert.NotNull(result.Patch);
        Assert.Equal("abc", result.Patch!.Value.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Fact]
    public void BuildPatch_DeepPath_CreatesIntermediateObjects()
    {
        var existing = Json("{}");

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.advanced.proxy.url", "http://localhost:8080")));

        Assert.Equal("http://localhost:8080",
            result.Patch!.Value.GetProperty("channels").GetProperty("telegram")
                .GetProperty("advanced").GetProperty("proxy").GetProperty("url").GetString());
    }

    // ─── Redaction-sentinel safety rail ──────────────────────────────────

    [Fact]
    public void BuildPatch_BlocksWhenOtherChannelHasRedactionSentinel()
    {
        var existing = Json("""
            {
              "channels": {
                "slack":    { "signingSecret": "[REDACTED]", "enabled": true },
                "telegram": { "botToken": "old", "enabled": false }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "new")));

        Assert.Null(result.Patch);
        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.slack.signingSecret", result.BlockedPath);
        Assert.Contains("redacted", result.BlockedReason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPatch_DoesNotBlockOnSentinelInsideTargetChannel()
    {
        // The sentinel is in the channel we're about to overwrite — that's
        // the whole point of this save, so don't refuse.
        var existing = Json("""
            {
              "channels": {
                "telegram": { "botToken": "<redacted>", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "real-new-token")));

        Assert.Null(result.BlockedReason);
        Assert.Equal("real-new-token",
            result.Patch!.Value.GetProperty("channels").GetProperty("telegram").GetProperty("botToken").GetString());
    }

    [Theory]
    [InlineData("***")]
    [InlineData("<redacted>")]
    [InlineData("[redacted]")]
    [InlineData("  [REDACTED]  ")] // trimmed before matching
    public void BuildPatch_DetectsCommonRedactionSentinels(string sentinel)
    {
        var existing = JsonDocument.Parse($$"""
            {
              "channels": {
                "slack": { "signingSecret": "{{sentinel.Replace("\\", "\\\\")}}", "enabled": true }
              }
            }
            """).RootElement;

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "abc")));

        Assert.NotNull(result.BlockedReason);
        Assert.Equal("channels.slack.signingSecret", result.BlockedPath);
    }

    [Fact]
    public void BuildPatch_IgnoresPartialSentinelMatches()
    {
        // A value that *contains* the word "redacted" as part of legit
        // text should NOT be treated as a sentinel.
        var existing = Json("""
            {
              "channels": {
                "slack": { "label": "no redacted secrets here", "enabled": true }
              }
            }
            """);

        var result = ChannelConfigPatchBuilder.BuildPatch(
            existing, "telegram",
            Updates(("channels.telegram.botToken", "abc")));

        Assert.Null(result.BlockedReason);
        Assert.NotNull(result.Patch);
    }

    // ─── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void BuildPatch_RejectsEmptyDotPathSegments()
    {
        var existing = Json("{}");
        Assert.Throws<System.ArgumentException>(() =>
            ChannelConfigPatchBuilder.BuildPatch(
                existing, "telegram",
                Updates(("channels..botToken", "abc"))));
    }

    [Fact]
    public void BuildPatch_RequiresChannelId()
    {
        Assert.Throws<System.ArgumentException>(() =>
            ChannelConfigPatchBuilder.BuildPatch(
                Json("{}"), "",
                Updates(("channels.telegram.botToken", "abc"))));
    }
}

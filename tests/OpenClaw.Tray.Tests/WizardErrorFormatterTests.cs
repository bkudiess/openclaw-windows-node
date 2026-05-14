using System.Net.WebSockets;
using OpenClawTray.Onboarding.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Regression coverage for the channel-pairing wizard reset bug
/// (PR #274 follow-up). Before <see cref="WizardErrorFormatter"/> existed, every
/// failure on wizard.next was masked behind "An error occurred processing this step",
/// so when the gateway returned an error after the user picked
/// "None. I'll connect a channel later" on the channel-pairing step, the wizard just
/// showed an error icon with no actionable text. These tests document the contract
/// the wizard UI now relies on so future regressions surface as test failures.
/// </summary>
public class WizardErrorFormatterTests
{
    [Fact]
    public void FormatStepError_NullException_ReturnsFallback()
    {
        var msg = WizardErrorFormatter.FormatStepError(null!, "channel.select", "fallback");
        Assert.Equal("fallback", msg);
    }

    [Fact]
    public void FormatStepError_NullExceptionAndFallback_ReturnsGenericMessage()
    {
        var msg = WizardErrorFormatter.FormatStepError(null!, "channel.select", null);
        Assert.Equal(WizardErrorFormatter.GenericFallbackMessage, msg);
    }

    [Fact]
    public void FormatStepError_TimeoutException_ReturnsHelpfulMessage()
    {
        var msg = WizardErrorFormatter.FormatStepError(new TimeoutException("any text"), "channel.select");
        Assert.Contains("did not respond", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("any text", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatStepError_InvalidOperationWithMessage_PreservesMessage()
    {
        var ex = new InvalidOperationException("Channel handler 'whatsapp' missing required configuration");
        var msg = WizardErrorFormatter.FormatStepError(ex, "channel.select");
        Assert.Contains("Channel handler", msg);
        Assert.Contains("whatsapp", msg);
    }

    /// <summary>
    /// This is the actual repro of the channel-pairing wizard error: the gateway
    /// returns a JSON-RPC error like "rpc error: invalid channel selection: none" and
    /// the tray previously hid it. The user-visible message must now contain the
    /// gateway's message so the user can react.
    /// </summary>
    [Fact]
    public void FormatStepError_GatewayRpcError_PreservesActionableText()
    {
        var ex = new InvalidOperationException("rpc error -32602: invalid channel selection 'none'; expected one of [whatsapp, telegram, slack, ''] or skip");
        var msg = WizardErrorFormatter.FormatStepError(ex, "channel.select");
        Assert.Contains("invalid channel selection", msg);
        Assert.Contains("'none'", msg);
    }

    [Fact]
    public void FormatStepError_RedactsBearerToken()
    {
        var ex = new InvalidOperationException("Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.Contains("<redacted>", msg);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", msg);
    }

    [Fact]
    public void FormatStepError_RedactsKeyValueSecret()
    {
        var ex = new InvalidOperationException("config error: token=abcd1234567890abcd1234567890abcd1234 was rejected");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.Contains("<redacted>", msg);
        Assert.DoesNotContain("abcd1234567890abcd1234567890abcd1234", msg);
        Assert.Contains("rejected", msg);
    }

    [Fact]
    public void FormatStepError_RedactsHexBlobs()
    {
        var ex = new InvalidOperationException("session_id=" + new string('a', 40) + " is invalid");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.Contains("<redacted>", msg);
        Assert.DoesNotContain(new string('a', 40), msg);
        Assert.Contains("invalid", msg);
    }

    /// <summary>
    /// PR-review fix: prefixed credential names (gateway_token, device_token, setup_code,
    /// bootstrap_token, api_key, …) must be redacted. Previously SecretLikeRegex used
    /// `\b(token|key|…)\b` which doesn't match the inner "token" inside "gateway_token"
    /// because `_` is a word character (no word boundary), so the value leaked.
    /// </summary>
    [Theory]
    [InlineData("gateway_token=abc123secretvalue")]
    [InlineData("gateway-token=abc123secretvalue")]
    [InlineData("device_token=abc123secretvalue")]
    [InlineData("bootstrap_token=abc123secretvalue")]
    [InlineData("setup_code=abc123secretvalue")]
    [InlineData("setup-code=abc123secretvalue")]
    [InlineData("auth_token=abc123secretvalue")]
    [InlineData("api_key=abc123secretvalue")]
    [InlineData("access_token=abc123secretvalue")]
    [InlineData("refresh_token=abc123secretvalue")]
    [InlineData("private_key=abc123secretvalue")]
    [InlineData("client_secret=abc123secretvalue")]
    public void FormatStepError_RedactsPrefixedCredentialNames(string pattern)
    {
        var ex = new InvalidOperationException($"upstream rejected: {pattern} was malformed");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.DoesNotContain("abc123secretvalue", msg);
        Assert.Contains("malformed", msg);
    }

    /// <summary>
    /// PR-review fix: the bare word "key" used to be in the credential regex, which
    /// over-redacted common English error text. Verify those phrases now pass through.
    /// </summary>
    [Theory]
    [InlineData("primary key violation on table users")]
    [InlineData("key not found in the index")]
    [InlineData("key constraint check failed during commit")]
    public void FormatStepError_DoesNotOverRedactWordKey(string sentence)
    {
        var ex = new InvalidOperationException(sentence);
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.DoesNotContain("<redacted>", msg);
        Assert.Contains(sentence, msg);
    }

    [Fact]
    public void FormatStepError_TruncatesVeryLongMessages()
    {
        // Use a long sentence-like payload that doesn't trigger any redaction regex —
        // the test is about truncation behavior, not redaction interaction.
        var sentence = string.Join(' ', Enumerable.Repeat("the gateway returned a long descriptive narrative about the failure", 20));
        var ex = new InvalidOperationException(sentence);
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.True(msg.Length <= WizardErrorFormatter.MaxUserMessageLength + 1, $"message length was {msg.Length}");
        Assert.EndsWith("…", msg);
    }

    [Fact]
    public void FormatStepError_TruncationDoesNotSplitSurrogatePair()
    {
        // Hanselman-review fix: emoji (U+1F44B = 0xD83D 0xDC4B as UTF-16) at the
        // truncation boundary must not produce an orphan high-surrogate code unit.
        // Pad with leading text so that the emoji's high surrogate lands at index
        // MaxUserMessageLength - 1, where naive slicing would split it.
        var prefix = new string('a', WizardErrorFormatter.MaxUserMessageLength - 1);
        var ex = new InvalidOperationException(prefix + "\uD83D\uDC4B trailing context that pushes us past the limit");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        // Last code unit before the ellipsis must be a valid (non-surrogate) char.
        var beforeEllipsis = msg[^2];
        Assert.False(char.IsHighSurrogate(beforeEllipsis), $"truncated string ended with orphan high surrogate U+{(int)beforeEllipsis:X4}");
        Assert.False(char.IsLowSurrogate(beforeEllipsis), $"truncated string ended with orphan low surrogate U+{(int)beforeEllipsis:X4}");
    }

    [Fact]
    public void FormatStepError_RedactsBase64WithEqualsPadding()
    {
        // Hanselman-review fix: Base64BlobRegex used to leave the '=' / '==' padding
        // un-redacted because the trailing \b couldn't match between '=' and EOL.
        var ex = new InvalidOperationException("payload=" + new string('A', 60) + "== was malformed");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.Contains("<redacted>", msg);
        Assert.DoesNotContain("AAAAAAAAAAAAAA==", msg, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 60), msg);
    }

    [Fact]
    public void FormatStepError_ComposesWithSharedTokenSanitizer_64CharHex()
    {
        // Hanselman-review fix: TokenSanitizer (shared across the codebase) catches a
        // 64-char hex blob that isn't preceded by a key=. WizardErrorFormatter must
        // delegate to it so the wizard pane gets the same redaction guarantees as
        // the rest of the app.
        var hex = new string('a', 64);
        var ex = new InvalidOperationException("upstream rejected handshake with id " + hex);
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.DoesNotContain(hex, msg);
        Assert.Contains("upstream rejected", msg);
    }

    [Fact]
    public void FormatStepError_ComposesWithSharedTokenSanitizer_JsonTokenField()
    {
        // Hanselman-review fix: the wizard pane could have surfaced a JSON field like
        // {"token":"actual-secret"} verbatim before; TokenSanitizer must scrub it.
        var ex = new InvalidOperationException("config rejected: {\"token\":\"super-secret-value-1234\",\"reason\":\"expired\"}");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.DoesNotContain("super-secret-value-1234", msg);
        Assert.Contains("expired", msg);
    }

    [Fact]
    public void FormatStepError_BlankMessage_FallsBackToGeneric()
    {
        var ex = new InvalidOperationException("   ");
        var msg = WizardErrorFormatter.FormatStepError(ex, genericFallback: "fallback");
        Assert.Equal("fallback", msg);
    }

    [Fact]
    public void FormatStepError_WebSocketException_PrependsTypeWhenMessageIsTerse()
    {
        var ex = new WebSocketException("Closed");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.Contains("WebSocketException", msg);
        Assert.Contains("Closed", msg);
    }

    [Fact]
    public void FormatStepError_CollapsesMultilineWhitespace()
    {
        var ex = new InvalidOperationException("first line\n\n   second line\twith\tabs");
        var msg = WizardErrorFormatter.FormatStepError(ex);
        Assert.DoesNotContain("\n", msg);
        Assert.DoesNotContain("\t", msg);
        Assert.Contains("first line second line with abs", msg);
    }
}

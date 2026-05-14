using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Formats exceptions raised by wizard.start / wizard.next / wizard.status RPCs into
/// user-facing strings for the wizard error pane. Previously the wizard masked all
/// failures with a generic "An error occurred processing this step" message which made
/// bugs like the channel-pairing reset (PR #274) extremely hard to diagnose without
/// log files. This helper preserves enough detail for the user to act on the failure
/// while redacting obvious secret-looking material.
/// </summary>
public static class WizardErrorFormatter
{
    /// <summary>Maximum length of the user-visible message before truncation.</summary>
    public const int MaxUserMessageLength = 240;

    /// <summary>Fallback when a localization key is unavailable.</summary>
    public const string GenericFallbackMessage = "An error occurred processing this step";

    /// <summary>
    /// Returns a sanitized, user-facing message describing why a wizard step failed.
    /// </summary>
    /// <param name="exception">The exception thrown by the gateway client.</param>
    /// <param name="stepId">The wizard step id (logged but not displayed).</param>
    /// <param name="genericFallback">
    /// Localized generic fallback to use when the exception carries no actionable message.
    /// Pass null/empty to use <see cref="GenericFallbackMessage"/>.
    /// </param>
    public static string FormatStepError(Exception exception, string? stepId = null, string? genericFallback = null)
    {
        var fallback = string.IsNullOrWhiteSpace(genericFallback) ? GenericFallbackMessage : genericFallback;

        if (exception is null)
            return fallback;

        var raw = exception switch
        {
            TimeoutException => "The gateway did not respond in time. Wait a moment and retry.",
            _ => exception.Message,
        };

        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var sanitized = Sanitize(raw);

        if (string.IsNullOrWhiteSpace(sanitized))
            return fallback;

        // Prepend exception type for non-Timeout cases when it adds context the bare
        // message lacks (e.g. ConnectionClosedException without text).
        if (exception is not TimeoutException && sanitized.Length < 12)
        {
            sanitized = $"{exception.GetType().Name}: {sanitized}";
        }

        return TruncateWithEllipsis(sanitized, MaxUserMessageLength);
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16
    /// code units, appending an ellipsis. Avoids splitting a UTF-16 surrogate pair —
    /// gateway errors can embed user-supplied data (channel names, device names) that
    /// may contain emoji or other non-BMP characters.
    /// </summary>
    private static string TruncateWithEllipsis(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut -= 1;
        }
        return value[..cut] + "…";
    }

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._\-+/=]+",
        RegexOptions.Compiled);

    // Catches prefixed key/token/secret/code names that the upstream gateway commonly uses
    // (gateway_token, device_token, bootstrap_token, setup_code, api_key, private_key, etc.)
    // Modelled on the SecretRedactor regex in LocalGatewaySetup.cs. We deliberately do NOT
    // include the bare word "key" here because it produces too many false positives on
    // legitimate error text ("primary key", "key constraint violated", "key not found").
    // Bare credential keywords are handled by SecretLikeRegex below.
    private static readonly Regex PrefixedSecretLikeRegex = new(
        @"(?i)\b(setup[_-]?code|bootstrap[_-]?token|device[_-]?token|gateway[_-]?token|auth[_-]?token|access[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|access[_-]?key|secret[_-]?key|private[_-]?key|public[_-]?key|client[_-]?secret|signing[_-]?key)\b[\s:=]+[^\s,;""'}]+",
        RegexOptions.Compiled);

    // Bare credential keywords (no underscore/dash prefix). "key" intentionally omitted —
    // see PrefixedSecretLikeRegex comment above.
    private static readonly Regex SecretLikeRegex = new(
        @"(?i)\b(token|secret|password|authorization|credential|passphrase)\b[\s:=]+[^\s,;""'}]+",
        RegexOptions.Compiled);

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+",
        RegexOptions.Compiled);

    private static readonly Regex HexBlobRegex = new(
        @"\b[0-9a-fA-F]{32,}\b",
        RegexOptions.Compiled);

    // Match 40+ chars of base64 alphabet, optionally followed by 1-2 '=' padding chars.
    // The trailing lookahead avoids the historical bug where a `\b` after `={0,2}` could
    // not match between '=' (non-word) and end-of-string / non-word, leaving the
    // padding chars un-redacted.
    private static readonly Regex Base64BlobRegex = new(
        @"\b[A-Za-z0-9+/]{40,}={0,2}(?![A-Za-z0-9+/=])",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var collapsed = WhitespaceRegex.Replace(value, " ").Trim();
        // Defense-in-depth: run the shared TokenSanitizer first so the well-known
        // patterns (Authorization: Bearer, JSON "token":"...", 64-char hex, 43-char
        // base64url) are caught with the same redaction tokens used everywhere else
        // in the codebase. Then apply the wizard-specific passes for the patterns
        // that TokenSanitizer doesn't cover (key=value tuples, JWTs, generic hex /
        // base64 blobs).
        var step0 = TokenSanitizer.Sanitize(collapsed);
        // Strip JWTs and Bearer tokens first so the bare credential is removed even when
        // it sits next to a "Bearer "/"Authorization:" header that the key=value sweep
        // alone wouldn't reach.
        var step0a = JwtRegex.Replace(step0, "<redacted>");
        var step0b = BearerTokenRegex.Replace(step0a, "Bearer <redacted>");
        // Prefixed credential keywords (gateway_token, setup_code, api_key, …) BEFORE
        // the bare-keyword sweep so the longest match wins.
        var step1a = PrefixedSecretLikeRegex.Replace(step0b, m =>
        {
            var key = m.Value.Split(new[] { ' ', ':', '=' }, 2)[0];
            return $"{key}=<redacted>";
        });
        var step1b = SecretLikeRegex.Replace(step1a, m =>
        {
            var key = m.Value.Split(new[] { ' ', ':', '=' }, 2)[0];
            return $"{key}=<redacted>";
        });
        var step2 = HexBlobRegex.Replace(step1b, "<redacted>");
        var step3 = Base64BlobRegex.Replace(step2, "<redacted>");
        return step3;
    }
}

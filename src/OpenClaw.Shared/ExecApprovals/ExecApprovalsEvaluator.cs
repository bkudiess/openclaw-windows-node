using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Phase 3 of the V2 exec approval pipeline: policy evaluation (rail 18, step 3).
/// Stateless — safe to call concurrently.
/// </summary>
/// <remarks>
/// Evaluation cascade:
/// <list type="number">
///   <item>
///     <see cref="ExecSecurity.Deny"/> → <see cref="ExecApprovalV2EvaluationKind.Denied"/>
///     with <see cref="ExecApprovalV2Code.SecurityDeny"/>.
///   </item>
///   <item>
///     <see cref="ExecSecurity.Full"/> → <see cref="ExecApprovalV2EvaluationKind.Allowed"/>.
///   </item>
///   <item>
///     <see cref="ExecSecurity.Allowlist"/> → match each <see cref="ExecAllowlistEntry"/>
///     pattern against the command's <see cref="CanonicalCommandIdentity.AllowlistResolutions"/>.
///     On hit → <see cref="ExecApprovalV2EvaluationKind.Allowed"/>.
///     On miss → apply <see cref="ExecAsk"/> fallback:
///     <list type="bullet">
///       <item><see cref="ExecAsk.Off"/> → <see cref="ExecApprovalV2Code.AllowlistMiss"/>.</item>
///       <item><see cref="ExecAsk.Deny"/> → <see cref="ExecApprovalV2Code.SecurityDeny"/>.</item>
///       <item><see cref="ExecAsk.OnMiss"/> → <see cref="ExecApprovalV2EvaluationKind.NeedsPrompt"/> with <see cref="ExecApprovalV2PromptReason.AllowlistMiss"/>.</item>
///       <item><see cref="ExecAsk.Always"/> → <see cref="ExecApprovalV2EvaluationKind.NeedsPrompt"/> with <see cref="ExecApprovalV2PromptReason.Always"/>.</item>
///     </list>
///   </item>
/// </list>
/// When <c>ask=always</c> is set with <see cref="ExecSecurity.Allowlist"/>, an allowlist hit
/// still proceeds to a prompt — the allowlist only pre-populates the "Allow Always" suggestion in the UI.
/// </remarks>
public static class ExecApprovalsEvaluator
{
    /// <summary>
    /// Evaluates whether <paramref name="identity"/> should be allowed, denied, or shown to the user.
    /// </summary>
    /// <param name="identity">Canonical command produced by the normalizer.</param>
    /// <param name="resolved">Fully-resolved policy from the store.</param>
    public static ExecApprovalV2EvaluationOutcome Evaluate(
        CanonicalCommandIdentity identity,
        ExecApprovalsResolved resolved)
    {
        var defaults = resolved.Defaults;

        switch (defaults.Security)
        {
            case ExecSecurity.Deny:
                return ExecApprovalV2EvaluationOutcome.Denied(
                    ExecApprovalV2Result.SecurityDeny("security-deny"));

            case ExecSecurity.Full:
                return EvaluateAskAlways(defaults);

            case ExecSecurity.Allowlist:
                return EvaluateAllowlist(identity.AllowlistResolutions, resolved.Allowlist, defaults);

            default:
                // Unknown security level — fail closed (research doc 04 R2).
                return ExecApprovalV2EvaluationOutcome.Denied(
                    ExecApprovalV2Result.SecurityDeny("unknown-security-level"));
        }
    }

    private static ExecApprovalV2EvaluationOutcome EvaluateAskAlways(
        ExecApprovalsResolvedDefaults defaults)
    {
        // Even under Full security, ask=always forces a prompt.
        return defaults.Ask == ExecAsk.Always
            ? ExecApprovalV2EvaluationOutcome.NeedsPrompt(ExecApprovalV2PromptReason.Always)
            : ExecApprovalV2EvaluationOutcome.Allowed();
    }

    private static ExecApprovalV2EvaluationOutcome EvaluateAllowlist(
        IReadOnlyList<ExecCommandResolution> allowlistResolutions,
        IReadOnlyList<ExecAllowlistEntry> allowlist,
        ExecApprovalsResolvedDefaults defaults)
    {
        // ask=always forces a prompt regardless of allowlist status.
        if (defaults.Ask == ExecAsk.Always)
            return ExecApprovalV2EvaluationOutcome.NeedsPrompt(ExecApprovalV2PromptReason.Always);

        // Fail-closed: if there are no resolutions, allowlist cannot be satisfied.
        if (allowlistResolutions.Count == 0)
            return DenyAllowlistMiss(defaults);

        // Check each allowlist entry against the resolved segments.
        foreach (var entry in allowlist)
        {
            if (ExecApprovalsAllowlistMatcher.MatchesAny(entry.Pattern, allowlistResolutions))
                return ExecApprovalV2EvaluationOutcome.Allowed();
        }

        return DenyAllowlistMiss(defaults);
    }

    private static ExecApprovalV2EvaluationOutcome DenyAllowlistMiss(
        ExecApprovalsResolvedDefaults defaults)
    {
        return defaults.Ask switch
        {
            ExecAsk.Off => ExecApprovalV2EvaluationOutcome.Denied(
                ExecApprovalV2Result.AllowlistMiss("allowlist-miss")),

            ExecAsk.Deny => ExecApprovalV2EvaluationOutcome.Denied(
                ExecApprovalV2Result.SecurityDeny("ask-deny")),

            ExecAsk.OnMiss => ExecApprovalV2EvaluationOutcome.NeedsPrompt(
                ExecApprovalV2PromptReason.AllowlistMiss),

            // ask=always is handled before this point, but fall through to NeedsPrompt for safety.
            _ => ExecApprovalV2EvaluationOutcome.NeedsPrompt(
                ExecApprovalV2PromptReason.AllowlistMiss),
        };
    }
}

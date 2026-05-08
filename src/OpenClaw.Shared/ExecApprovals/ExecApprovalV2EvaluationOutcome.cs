namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Discriminated outcome from <see cref="ExecApprovalsEvaluator"/>.
/// The coordinator consumes this and either proceeds, rejects, or shows a prompt.
/// </summary>
public sealed class ExecApprovalV2EvaluationOutcome
{
    public ExecApprovalV2EvaluationKind Kind { get; }

    /// <summary>Non-null when <see cref="Kind"/> is <see cref="ExecApprovalV2EvaluationKind.Denied"/>.</summary>
    public ExecApprovalV2Result? Denial { get; }

    /// <summary>Non-null when <see cref="Kind"/> is <see cref="ExecApprovalV2EvaluationKind.NeedsPrompt"/>.</summary>
    public ExecApprovalV2PromptReason? PromptReason { get; }

    private ExecApprovalV2EvaluationOutcome(
        ExecApprovalV2EvaluationKind kind,
        ExecApprovalV2Result? denial = null,
        ExecApprovalV2PromptReason? promptReason = null)
    {
        Kind = kind;
        Denial = denial;
        PromptReason = promptReason;
    }

    public static ExecApprovalV2EvaluationOutcome Allowed()
        => new(ExecApprovalV2EvaluationKind.Allowed);

    public static ExecApprovalV2EvaluationOutcome Denied(ExecApprovalV2Result denial)
        => new(ExecApprovalV2EvaluationKind.Denied, denial: denial);

    public static ExecApprovalV2EvaluationOutcome NeedsPrompt(ExecApprovalV2PromptReason reason)
        => new(ExecApprovalV2EvaluationKind.NeedsPrompt, promptReason: reason);

    public override string ToString()
        => Kind switch
        {
            ExecApprovalV2EvaluationKind.Denied => $"Denied: {Denial}",
            ExecApprovalV2EvaluationKind.NeedsPrompt => $"NeedsPrompt({PromptReason})",
            _ => "Allowed",
        };
}

public enum ExecApprovalV2EvaluationKind
{
    Allowed,
    Denied,
    NeedsPrompt,
}

/// <summary>
/// Reason the evaluator requires a user prompt.
/// The coordinator uses this to set appropriate UI context.
/// </summary>
public enum ExecApprovalV2PromptReason
{
    /// <summary>Command is not on the allowlist and ask=on-miss.</summary>
    AllowlistMiss,

    /// <summary>Ask policy is set to always ask regardless of allowlist status.</summary>
    Always,
}

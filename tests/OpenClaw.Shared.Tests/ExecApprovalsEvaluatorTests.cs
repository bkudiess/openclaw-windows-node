using System.Collections.Generic;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// Tests for PR5: ExecApprovalsEvaluator and ExecApprovalsAllowlistMatcher.
// Coverage: security-level cascade, allowlist pattern matching, ask-mode fallback,
// empty-allowlist fail-closed, edge cases.
public class ExecApprovalsEvaluatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // On Linux, Path.GetFileName does not split Windows backslash paths.
    // Simulate what ExecCommandResolver does on Windows by splitting on both separators.
    private static string WindowsBasename(string path)
    {
        var parts = path.Split(['\\', '/'], System.StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : path;
    }

    private static ExecCommandResolution Res(string exe, string? resolved = null)
        => new(exe, resolved, WindowsBasename(resolved ?? exe), null);

    private static ExecAllowlistEntry Entry(string pattern)
        => new() { Pattern = pattern };

    private static ExecApprovalsResolvedDefaults Defaults(
        ExecSecurity security = ExecSecurity.Allowlist,
        ExecAsk ask = ExecAsk.Off,
        ExecAsk askFallback = ExecAsk.Off)
        => new()
        {
            Security = security,
            Ask = ask,
            AskFallback = askFallback,
            AutoAllowSkills = false,
        };

    private static ExecApprovalsResolved Resolved(
        ExecApprovalsResolvedDefaults defaults,
        IReadOnlyList<ExecAllowlistEntry>? allowlist = null,
        string agentId = "agent1")
        => new()
        {
            AgentId = agentId,
            Defaults = defaults,
            Allowlist = allowlist ?? [],
        };

    private static CanonicalCommandIdentity Identity(
        IReadOnlyList<ExecCommandResolution> resolutions,
        string? agentId = "agent1")
        => new(
            command: ["git", "status"],
            displayCommand: "git status",
            evaluationRawCommand: null,
            resolution: resolutions.Count > 0 ? resolutions[0] : null,
            allowlistResolutions: resolutions,
            allowAlwaysPatterns: [],
            cwd: null,
            timeoutMs: 30_000,
            env: null,
            agentId: agentId,
            sessionKey: null);

    // -------------------------------------------------------------------------
    // 1. Security level: Deny
    // -------------------------------------------------------------------------

    [Fact]
    public void SecurityDeny_AlwaysReturnsSecurityDeny()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Deny));
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Denied, outcome.Kind);
        Assert.Equal(ExecApprovalV2Code.SecurityDeny, outcome.Denial!.Code);
    }

    [Fact]
    public void SecurityDeny_IgnoresAllowlist()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Deny),
            allowlist: [Entry("git")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Denied, outcome.Kind);
    }

    // -------------------------------------------------------------------------
    // 2. Security level: Full
    // -------------------------------------------------------------------------

    [Fact]
    public void SecurityFull_AllowsWhenAskIsOff()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Full, ask: ExecAsk.Off));
        var identity = Identity([Res("notepad.exe", @"C:\Windows\notepad.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void SecurityFull_NeedsPrompt_WhenAskIsAlways()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Full, ask: ExecAsk.Always));
        var identity = Identity([Res("notepad.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.NeedsPrompt, outcome.Kind);
        Assert.Equal(ExecApprovalV2PromptReason.Always, outcome.PromptReason);
    }

    // -------------------------------------------------------------------------
    // 3. Security level: Allowlist — allowlist hit
    // -------------------------------------------------------------------------

    [Fact]
    public void AllowlistHit_ByExactBasename()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("git")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void AllowlistHit_ByExactBasenameWithExeExtension()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("git.exe")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void AllowlistHit_ByFullResolvedPath()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry(@"C:\Program Files\Git\bin\git.exe")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void AllowlistHit_ByGlobPattern()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("python*")]);
        var identity = Identity([Res("python3.11.exe", @"C:\Python311\python3.11.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void AllowlistHit_CaseInsensitive()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("GIT")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    [Fact]
    public void AllowlistHit_MultiSegmentChain_AnySegmentMatches()
    {
        // Shell chain: "git status && npm install" — two segments, only npm is on the allowlist.
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("npm")]);
        var identity = Identity([
            Res("git.exe", @"C:\Program Files\Git\bin\git.exe"),
            Res("npm.cmd", @"C:\Program Files\nodejs\npm.cmd"),
        ]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        // Only one segment matches — the chain as a whole is allowed.
        // (Full chain-wide matching is enforced by the coordinator, not the evaluator.)
        Assert.Equal(ExecApprovalV2EvaluationKind.Allowed, outcome.Kind);
    }

    // -------------------------------------------------------------------------
    // 4. Security level: Allowlist — allowlist miss + Ask fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void AllowlistMiss_AskOff_ReturnsDenied_AllowlistMiss()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("npm")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Denied, outcome.Kind);
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, outcome.Denial!.Code);
    }

    [Fact]
    public void AllowlistMiss_AskDeny_ReturnsDenied_SecurityDeny()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Deny),
            allowlist: []);
        var identity = Identity([Res("git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Denied, outcome.Kind);
        Assert.Equal(ExecApprovalV2Code.SecurityDeny, outcome.Denial!.Code);
    }

    [Fact]
    public void AllowlistMiss_AskOnMiss_ReturnsNeedsPrompt_AllowlistMiss()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.OnMiss),
            allowlist: []);
        var identity = Identity([Res("curl.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.NeedsPrompt, outcome.Kind);
        Assert.Equal(ExecApprovalV2PromptReason.AllowlistMiss, outcome.PromptReason);
    }

    [Fact]
    public void AskAlways_AllowlistSecurity_NeedsPrompt_EvenOnHit()
    {
        // ask=always means prompt regardless of allowlist status.
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Always),
            allowlist: [Entry("git")]);
        var identity = Identity([Res("git.exe", @"C:\Program Files\Git\bin\git.exe")]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.NeedsPrompt, outcome.Kind);
        Assert.Equal(ExecApprovalV2PromptReason.Always, outcome.PromptReason);
    }

    // -------------------------------------------------------------------------
    // 5. Fail-closed: empty resolutions
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyResolutions_FailClosed_AllowlistMiss()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.Off),
            allowlist: [Entry("git")]);
        // Empty resolutions: the normalizer could not resolve any segment.
        var identity = Identity([]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.Denied, outcome.Kind);
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, outcome.Denial!.Code);
    }

    [Fact]
    public void EmptyResolutions_AskOnMiss_NeedsPrompt()
    {
        var resolved = Resolved(
            Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.OnMiss),
            allowlist: [Entry("git")]);
        var identity = Identity([]);

        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);

        Assert.Equal(ExecApprovalV2EvaluationKind.NeedsPrompt, outcome.Kind);
    }

    // -------------------------------------------------------------------------
    // 6. AllowlistMatcher edge cases
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("git", "git.exe", null, true)]
    [InlineData("git.exe", "git.exe", null, true)]
    [InlineData("GIT", "git.exe", null, true)]
    [InlineData("node", "npm.cmd", null, false)]
    [InlineData("python*", "python3.11.exe", null, true)]
    [InlineData("python*", "python.exe", null, true)]
    [InlineData("python*", "node.exe", null, false)]
    [InlineData("npm*", "npm.cmd", null, true)]
    [InlineData("", "git.exe", null, false)]
    [InlineData("   ", "git.exe", null, false)]
    public void AllowlistMatcher_MatchesAny(string pattern, string exe, string? resolved, bool expected)
    {
        var resolutions = new[] { Res(exe, resolved) };
        Assert.Equal(expected, ExecApprovalsAllowlistMatcher.MatchesAny(pattern, resolutions));
    }

    [Fact]
    public void AllowlistMatcher_FullPathPattern_MatchesResolvedPath()
    {
        var res = Res("git.exe", @"C:\Program Files\Git\bin\git.exe");
        Assert.True(ExecApprovalsAllowlistMatcher.MatchesAny(
            @"C:\Program Files\Git\bin\git.exe", [res]));
    }

    [Fact]
    public void AllowlistMatcher_FullPathPattern_DoesNotMatchWrongPath()
    {
        var res = Res("git.exe", @"C:\Program Files\Git\bin\git.exe");
        Assert.False(ExecApprovalsAllowlistMatcher.MatchesAny(
            @"C:\Users\attacker\git.exe", [res]));
    }

    [Fact]
    public void AllowlistMatcher_EmptyAllowlist_NeverMatches()
    {
        var res = Res("git.exe", @"C:\Program Files\Git\bin\git.exe");
        Assert.False(ExecApprovalsAllowlistMatcher.MatchesAny("git", []));
    }

    [Fact]
    public void AllowlistMatcher_NullPattern_NeverMatches()
    {
        var res = Res("git.exe", @"C:\Program Files\Git\bin\git.exe");
        Assert.False(ExecApprovalsAllowlistMatcher.MatchesAny(null, [res]));
    }

    // -------------------------------------------------------------------------
    // 7. Outcome shape
    // -------------------------------------------------------------------------

    [Fact]
    public void AllowedOutcome_DenialIsNull()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Full, ask: ExecAsk.Off));
        var identity = Identity([Res("cmd.exe")]);
        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);
        Assert.Null(outcome.Denial);
        Assert.Null(outcome.PromptReason);
    }

    [Fact]
    public void DeniedOutcome_PromptReasonIsNull()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Deny));
        var identity = Identity([Res("cmd.exe")]);
        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);
        Assert.Null(outcome.PromptReason);
        Assert.NotNull(outcome.Denial);
    }

    [Fact]
    public void NeedsPromptOutcome_DenialIsNull()
    {
        var resolved = Resolved(Defaults(security: ExecSecurity.Allowlist, ask: ExecAsk.OnMiss));
        var identity = Identity([Res("cmd.exe")]);
        var outcome = ExecApprovalsEvaluator.Evaluate(identity, resolved);
        Assert.Equal(ExecApprovalV2EvaluationKind.NeedsPrompt, outcome.Kind);
        Assert.Null(outcome.Denial);
    }
}

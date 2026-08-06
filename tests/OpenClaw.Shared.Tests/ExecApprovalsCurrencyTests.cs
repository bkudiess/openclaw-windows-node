using System.Collections.Generic;
using System.Linq;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalsCurrencyTests
{
    private static ExecApprovalsResolved Resolved(
        ExecSecurity security,
        ExecAsk ask,
        ExecSecurity askFallback = ExecSecurity.Deny,
        params string[] patterns)
        => new()
        {
            AgentId = "agent-1",
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = security,
                Ask = ask,
                AskFallback = askFallback,
            },
            Allowlist = patterns.Select(p => new ExecAllowlistEntry { Pattern = p }).ToList(),
        };

    [Fact]
    public void UnchangedPolicy_IsCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"]));
        Assert.True(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"])));
    }

    [Fact]
    public void SecurityTightened_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.False(snap.IsStillCurrent(Resolved(ExecSecurity.Deny, ExecAsk.OnMiss)));
    }

    [Fact]
    public void SecurityLoosened_StaysCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.True(snap.IsStillCurrent(Resolved(ExecSecurity.Full, ExecAsk.OnMiss)));
    }

    [Fact]
    public void AskRaised_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.False(snap.IsStillCurrent(Resolved(ExecSecurity.Allowlist, ExecAsk.Always)));
    }

    [Fact]
    public void AllowlistEntryRevoked_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*", "npm*"]));
        Assert.False(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"])));
    }

    [Fact]
    public void AllowlistEntryAdded_StaysCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"]));
        Assert.True(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*", "npm*"])));
    }

    [Fact]
    public void AskFallbackTightened_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Full, ExecAsk.Always, ExecSecurity.Full));

        Assert.False(snap.IsStillCurrent(
            Resolved(ExecSecurity.Full, ExecAsk.Always, ExecSecurity.Deny)));
    }

    [Fact]
    public void GeneratedRuleArgPatternChanged_IsNotCurrent()
    {
        var original = ExecArgPattern.BuildHashed(["git.exe", "status"]);
        var changed = ExecArgPattern.BuildHashed(["git.exe", "push"]);
        var snapshot = ExecApprovalsCurrency.Capture(ResolvedWithRule(original));

        Assert.False(snapshot.IsStillCurrent(ResolvedWithRule(changed)));
    }

    [Fact]
    public void GeneratedRuleSourceRemoved_IsNotCurrent()
    {
        var argPattern = ExecArgPattern.BuildHashed(["git.exe", "status"]);
        var snapshot = ExecApprovalsCurrency.Capture(ResolvedWithRule(argPattern));
        var manual = ResolvedWithRule(argPattern);
        manual.Allowlist[0].Source = null;

        Assert.False(snapshot.IsStillCurrent(manual));
    }

    private static ExecApprovalsResolved ResolvedWithRule(string argPattern) =>
        new()
        {
            AgentId = "agent-1",
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecSecurity.Deny,
            },
            Allowlist =
            [
                new ExecAllowlistEntry
                {
                    Pattern = "**/git.exe",
                    Source = ExecAllowlistEntry.AllowAlwaysSource,
                    ArgPattern = argPattern,
                },
            ],
        };
}

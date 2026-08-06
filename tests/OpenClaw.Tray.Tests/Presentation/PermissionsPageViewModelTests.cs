using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PermissionsPageViewModelTests
{
    [Fact]
    public async Task Load_ProjectsDefaultsWildcardMainAndExistingAgentScopes()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            defaults: new ExecApprovalsDefaults
            {
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecSecurity.Deny,
                AutoAllowSkills = false,
            },
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["research"] = new(),
            }));
        var viewModel = MakeViewModel(store);

        var result = await viewModel.LoadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["", "*", "main", "research"],
            viewModel.AvailableScopes.Select(scope => scope.Id));
        Assert.True(viewModel.IsDefaultsScope);
        Assert.Equal(ExecSecurity.Allowlist, viewModel.Security);
        Assert.Equal(ExecAsk.OnMiss, viewModel.Ask);
        Assert.Equal(ExecSecurity.Deny, viewModel.AskFallback);
        Assert.False(viewModel.AutoAllowSkills);
    }

    [Fact]
    public async Task AddAllowlistEntry_InvalidCommandTextDoesNotWrite()
    {
        var store = new RecordingExecApprovalsPolicyStore(File());
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.AddAllowlistEntryAsync("hostname");

        Assert.Equal(ExecPolicyOperationStatus.InvalidPattern, result.Status);
        Assert.Equal(0, store.ReplaceCount);
        Assert.Empty(viewModel.Allowlist);
    }

    [Fact]
    public async Task AddAllowlistEntry_ValidPatternPersistsToSelectedScopeAndSurvivesReload()
    {
        var store = new RecordingExecApprovalsPolicyStore(File());
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.WildcardScopeId);

        var result = await viewModel.AddAllowlistEntryAsync(" **/hostname.exe ");

        Assert.True(result.Succeeded);
        var entry = Assert.Single(
            store.Current.File.Agents![PermissionsPageViewModel.WildcardScopeId].Allowlist!);
        Assert.Equal("**/hostname.exe", entry.Pattern);
        Assert.NotNull(entry.Id);

        var reloaded = MakeViewModel(store);
        await reloaded.LoadAsync();
        reloaded.SelectScope(PermissionsPageViewModel.WildcardScopeId);
        Assert.Equal("**/hostname.exe", Assert.Single(reloaded.Allowlist).Pattern);
    }

    [Fact]
    public async Task UpdateAgentPolicy_PreservesOtherScopesAndInheritedFields()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["*"] = new()
                {
                    Ask = ExecAsk.OnMiss,
                    Allowlist =
                    [
                        new()
                        {
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:test",
                        },
                    ],
                },
                ["main"] = new() { AutoAllowSkills = true },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.UpdateSecurityAsync(ExecSecurity.Allowlist);

        Assert.True(result.Succeeded);
        var agents = store.Current.File.Agents!;
        Assert.Equal(ExecSecurity.Allowlist, agents["main"].Security);
        Assert.True(agents["main"].AutoAllowSkills);
        Assert.Equal(ExecAsk.OnMiss, agents["*"].Ask);
        var wildcardRule = Assert.Single(agents["*"].Allowlist!);
        Assert.Equal("**/git.exe", wildcardRule.Pattern);
        Assert.Equal(ExecAllowlistEntry.AllowAlwaysSource, wildcardRule.Source);
        Assert.Equal("sha256:argv:test", wildcardRule.ArgPattern);
    }

    [Fact]
    public async Task AddManualRule_SamePathAsGeneratedRule_PreservesBothAuthorities()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new()
                {
                    Allowlist =
                    [
                        new()
                        {
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:test",
                        },
                    ],
                },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.AddAllowlistEntryAsync("**/git.exe");

        Assert.True(result.Succeeded);
        var rules = store.Current.File.Agents!["main"].Allowlist!;
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, entry => entry.ArgPattern == "sha256:argv:test");
        Assert.Contains(rules, entry =>
            entry.ArgPattern is null
            && entry.Source is null);
    }

    [Fact]
    public async Task AddManualRule_ConvertsLegacyGeneratedPathOnlyRule()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new()
                {
                    Allowlist =
                    [
                        new()
                        {
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                        },
                    ],
                },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.AddAllowlistEntryAsync("**/git.exe");

        Assert.True(result.Succeeded);
        var rule = Assert.Single(store.Current.File.Agents!["main"].Allowlist!);
        Assert.Null(rule.Source);
        Assert.Null(rule.ArgPattern);
    }

    [Fact]
    public async Task RemoveRule_WithId_RemovesOnlySelectedArgumentBoundSibling()
    {
        var statusId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new()
                {
                    Allowlist =
                    [
                        new()
                        {
                            Id = statusId,
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:status",
                        },
                        new()
                        {
                            Id = logId,
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:log",
                        },
                        new() { Pattern = "**/git.exe" },
                    ],
                },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.RemoveAllowlistEntryAsync(
            statusId,
            "**/git.exe",
            "sha256:argv:status",
            ExecAllowlistEntry.AllowAlwaysSource);

        Assert.True(result.Succeeded);
        var rules = store.Current.File.Agents!["main"].Allowlist!;
        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, entry => entry.Id == statusId);
        Assert.Contains(rules, entry => entry.Id == logId);
        Assert.Contains(rules, entry => entry.Source is null);
    }

    [Fact]
    public async Task RemoveIdlessRule_MatchesCompleteAuthorityTuple()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new()
                {
                    Allowlist =
                    [
                        new()
                        {
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:status",
                        },
                        new()
                        {
                            Pattern = "**/git.exe",
                            Source = ExecAllowlistEntry.AllowAlwaysSource,
                            ArgPattern = "sha256:argv:log",
                        },
                    ],
                },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.RemoveAllowlistEntryAsync(
            id: null,
            pattern: "**/git.exe",
            argPattern: "sha256:argv:status",
            source: ExecAllowlistEntry.AllowAlwaysSource);

        Assert.True(result.Succeeded);
        var rule = Assert.Single(store.Current.File.Agents!["main"].Allowlist!);
        Assert.Equal("sha256:argv:log", rule.ArgPattern);
    }

    [Fact]
    public async Task CaseDistinctAgentScopes_RemainVisibleAndMutateExactScope()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
            {
                ["main"] = new() { Ask = ExecAsk.Off },
                ["Main"] = new() { Ask = ExecAsk.OnMiss },
            }));
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();

        Assert.Contains(viewModel.AvailableScopes, scope => scope.Id == "main");
        Assert.Contains(viewModel.AvailableScopes, scope => scope.Id == "Main");
        viewModel.SelectScope("Main");
        var result = await viewModel.UpdateAskAsync(ExecAsk.Always);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecAsk.Off, store.Current.File.Agents!["main"].Ask);
        Assert.Equal(ExecAsk.Always, store.Current.File.Agents["Main"].Ask);
    }

    [Fact]
    public async Task CaseOnlyExternalScopeChange_StopsCasRetryAndReloadsDefaults()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
            {
                ["Main"] = new() { Ask = ExecAsk.OnMiss },
            }))
        {
            ConflictsRemaining = 1,
            ConflictFileFactory = _ => File(
                agents: new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
                {
                    ["MAIN"] = new() { Ask = ExecAsk.Always },
                }),
        };
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope("Main");

        var result = await viewModel.UpdateAskAsync(ExecAsk.Off);

        Assert.Equal(ExecPolicyOperationStatus.Conflict, result.Status);
        Assert.True(viewModel.IsDefaultsScope);
        Assert.False(store.Current.File.Agents!.ContainsKey("Main"));
        Assert.True(store.Current.File.Agents.ContainsKey("MAIN"));
    }

    [Fact]
    public async Task CaseOnlyExternalScopeChange_DoesNotRecreateScopeForAllowlistGrant()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
            {
                ["Main"] = new(),
            }))
        {
            ConflictsRemaining = 1,
            ConflictFileFactory = _ => File(
                agents: new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
                {
                    ["MAIN"] = new(),
                }),
        };
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope("Main");

        var result = await viewModel.AddAllowlistEntryAsync("**/where.exe");

        Assert.Equal(ExecPolicyOperationStatus.Conflict, result.Status);
        Assert.True(viewModel.IsDefaultsScope);
        Assert.False(store.Current.File.Agents!.ContainsKey("Main"));
        Assert.Empty(store.Current.File.Agents["MAIN"].Allowlist ?? []);
    }

    [Fact]
    public async Task SaveConflict_ReloadsAndRetriesWithoutClobberingExternalScope()
    {
        var store = new RecordingExecApprovalsPolicyStore(File())
        {
            ConflictsRemaining = 1,
        };
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.UpdateAskAsync(ExecAsk.OnMiss);

        Assert.True(result.Succeeded);
        Assert.Equal(2, store.ReplaceCount);
        Assert.True(store.Current.File.Agents!.ContainsKey("external"));
        Assert.Equal(ExecAsk.OnMiss, store.Current.File.Agents["main"].Ask);
    }

    [Fact]
    public async Task SaveFailure_ReloadsLastSavedState()
    {
        var store = new RecordingExecApprovalsPolicyStore(File(
            agents: new Dictionary<string, ExecApprovalsAgent>
            {
                ["main"] = new() { Security = ExecSecurity.Allowlist },
            }))
        {
            ReplaceFailure = new IOException("read-only file"),
        };
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();
        viewModel.SelectScope(PermissionsPageViewModel.MainScopeId);

        var result = await viewModel.UpdateSecurityAsync(ExecSecurity.Full);

        Assert.Equal(ExecPolicyOperationStatus.SaveFailed, result.Status);
        Assert.Equal(ExecSecurity.Allowlist, viewModel.Security);
        Assert.Equal(ExecSecurity.Allowlist, store.Current.File.Agents!["main"].Security);
    }

    [Fact]
    public async Task DefaultsScope_RejectsAllowlistEntriesWithoutWriting()
    {
        var store = new RecordingExecApprovalsPolicyStore(File());
        var viewModel = MakeViewModel(store);
        await viewModel.LoadAsync();

        var result = await viewModel.AddAllowlistEntryAsync("**/git.exe");

        Assert.Equal(
            ExecPolicyOperationStatus.RulesUnavailableForDefaults,
            result.Status);
        Assert.Equal(0, store.ReplaceCount);
    }

    private static PermissionsPageViewModel MakeViewModel(
        IExecApprovalsPolicyStore store) =>
        new(store, new RecordingUiDispatcher());

    private static ExecApprovalsFile File(
        ExecApprovalsDefaults? defaults = null,
        Dictionary<string, ExecApprovalsAgent>? agents = null) =>
        new()
        {
            Version = 1,
            Defaults = defaults ?? new ExecApprovalsDefaults
            {
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecSecurity.Deny,
                AutoAllowSkills = false,
            },
            Agents = agents ?? new Dictionary<string, ExecApprovalsAgent>(),
        };

    private sealed class RecordingExecApprovalsPolicyStore(ExecApprovalsFile file)
        : IExecApprovalsPolicyStore
    {
        private int _hashVersion = 1;

        public ExecApprovalsSnapshot Current { get; private set; } =
            new(@"C:\policy\exec-approvals.json", true, "hash-1", file);

        public int ReplaceCount { get; private set; }
        public int ConflictsRemaining { get; set; }
        public IOException? ReplaceFailure { get; set; }
        public Func<ExecApprovalsFile, ExecApprovalsFile>? ConflictFileFactory { get; set; }

        public Task<ExecApprovalsSnapshot> GetSnapshotAsync() =>
            Task.FromResult(Current);

        public Task<ExecApprovalsSnapshot?> ReplaceAsync(
            string baseHash,
            ExecApprovalsFile replacement)
        {
            ReplaceCount++;
            if (ReplaceFailure is not null)
                throw ReplaceFailure;

            if (ConflictsRemaining > 0)
            {
                ConflictsRemaining--;
                var conflictFile = ConflictFileFactory?.Invoke(Current.File);
                if (conflictFile is null)
                {
                    var agents = Current.File.Agents is null
                        ? new Dictionary<string, ExecApprovalsAgent>()
                        : new Dictionary<string, ExecApprovalsAgent>(
                            Current.File.Agents,
                            StringComparer.Ordinal);
                    agents["external"] = new ExecApprovalsAgent
                    {
                        Security = ExecSecurity.Deny,
                    };
                    conflictFile = new ExecApprovalsFile
                    {
                        Version = 1,
                        Defaults = Current.File.Defaults,
                        Agents = agents,
                    };
                }
                Current = new ExecApprovalsSnapshot(
                    Current.Path,
                    true,
                    $"hash-{++_hashVersion}",
                    conflictFile);
                return Task.FromResult<ExecApprovalsSnapshot?>(null);
            }

            if (!string.Equals(baseHash, Current.Hash, StringComparison.Ordinal))
                return Task.FromResult<ExecApprovalsSnapshot?>(null);

            Current = new ExecApprovalsSnapshot(
                Current.Path,
                true,
                $"hash-{++_hashVersion}",
                replacement);
            return Task.FromResult<ExecApprovalsSnapshot?>(Current);
        }
    }
}

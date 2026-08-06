using OpenClaw.Shared.ExecApprovals;

namespace OpenClawTray.Presentation;

internal enum ExecPolicyOperationStatus
{
    Success,
    EmptyPattern,
    InvalidPattern,
    RulesUnavailableForDefaults,
    ReadFailed,
    SaveFailed,
    Conflict,
}

internal sealed record ExecPolicyOperationResult(
    ExecPolicyOperationStatus Status,
    string? Detail = null)
{
    public bool Succeeded => Status == ExecPolicyOperationStatus.Success;

    public static ExecPolicyOperationResult Success() =>
        new(ExecPolicyOperationStatus.Success);
}

internal sealed record ExecPolicyScopeOption(string Id);

/// <summary>
/// Owns the Permissions page's exec-approval V2 projection and CAS mutations.
/// The page remains the WinUI applicator; policy IO and mutation semantics live here.
/// </summary>
internal sealed class PermissionsPageViewModel : INavigationAware, IDisposable
{
    internal const string DefaultsScopeId = "";
    internal const string WildcardScopeId = "*";
    internal const string MainScopeId = "main";

    private const int MaxSaveAttempts = 3;

    private readonly IExecApprovalsPolicyStore _store;
    private readonly IUiDispatcher _dispatcher;
    private ExecApprovalsSnapshot? _snapshot;
    private IReadOnlyList<ExecPolicyScopeOption> _availableScopes =
        [new(DefaultsScopeId), new(WildcardScopeId), new(MainScopeId)];
    private string _selectedScopeId = DefaultsScopeId;

    public PermissionsPageViewModel(
        IExecApprovalsPolicyStore store,
        IUiDispatcher dispatcher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event EventHandler? StateChanged;

    internal bool IsActive { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal bool IsBusy { get; private set; }
    internal IUiDispatcher Dispatcher => _dispatcher;
    internal string? PolicyPath => _snapshot?.Path;
    internal string SelectedScopeId => _selectedScopeId;
    internal bool IsDefaultsScope => _selectedScopeId.Length == 0;
    internal IReadOnlyList<ExecPolicyScopeOption> AvailableScopes => _availableScopes;

    internal ExecSecurity? Security =>
        IsDefaultsScope
            ? _snapshot?.File.Defaults?.Security ?? ExecSecurity.Allowlist
            : SelectedAgent?.Security;

    internal ExecAsk? Ask =>
        IsDefaultsScope
            ? _snapshot?.File.Defaults?.Ask ?? ExecAsk.OnMiss
            : SelectedAgent?.Ask;

    internal ExecSecurity? AskFallback =>
        IsDefaultsScope
            ? _snapshot?.File.Defaults?.AskFallback ?? ExecSecurity.Deny
            : SelectedAgent?.AskFallback;

    internal bool? AutoAllowSkills =>
        IsDefaultsScope
            ? _snapshot?.File.Defaults?.AutoAllowSkills ?? false
            : SelectedAgent?.AutoAllowSkills;

    internal IReadOnlyList<ExecAllowlistEntry> Allowlist =>
        SelectedAgent?.Allowlist?.Select(CloneEntry).ToArray() ?? [];

    private ExecApprovalsAgent? SelectedAgent
    {
        get
        {
            if (IsDefaultsScope || _snapshot?.File.Agents is null)
                return null;
            _snapshot.File.Agents.TryGetValue(_selectedScopeId, out var agent);
            return agent;
        }
    }

    public void Activate(object? parameter) => IsActive = true;

    public void Deactivate() => IsActive = false;

    public void Dispose()
    {
        IsDisposed = true;
        StateChanged = null;
    }

    internal async Task<ExecPolicyOperationResult> LoadAsync()
    {
        SetBusy(true);
        try
        {
            ApplySnapshot(await _store.GetSnapshotAsync().ConfigureAwait(false));
            return ExecPolicyOperationResult.Success();
        }
        catch (IOException ex)
        {
            return new(ExecPolicyOperationStatus.ReadFailed, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(ExecPolicyOperationStatus.ReadFailed, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal void SelectScope(string? scopeId)
    {
        var normalized = NormalizeScopeId(scopeId);
        if (!_availableScopes.Any(scope =>
                string.Equals(scope.Id, normalized, StringComparison.Ordinal)))
        {
            normalized = DefaultsScopeId;
        }

        if (string.Equals(_selectedScopeId, normalized, StringComparison.Ordinal))
            return;

        _selectedScopeId = normalized;
        PublishState();
    }

    internal Task<ExecPolicyOperationResult> UpdateSecurityAsync(ExecSecurity? value) =>
        MutateSelectedPolicyAsync(
            (defaults, agent) =>
            {
                if (defaults is not null) defaults.Security = value;
                if (agent is not null) agent.Security = value;
            });

    internal Task<ExecPolicyOperationResult> UpdateAskAsync(ExecAsk? value) =>
        MutateSelectedPolicyAsync(
            (defaults, agent) =>
            {
                if (defaults is not null) defaults.Ask = value;
                if (agent is not null) agent.Ask = value;
            });

    internal Task<ExecPolicyOperationResult> UpdateAskFallbackAsync(ExecSecurity? value) =>
        MutateSelectedPolicyAsync(
            (defaults, agent) =>
            {
                if (defaults is not null) defaults.AskFallback = value;
                if (agent is not null) agent.AskFallback = value;
            });

    internal Task<ExecPolicyOperationResult> UpdateAutoAllowSkillsAsync(bool? value) =>
        MutateSelectedPolicyAsync(
            (defaults, agent) =>
            {
                if (defaults is not null) defaults.AutoAllowSkills = value;
                if (agent is not null) agent.AutoAllowSkills = value;
            });

    internal Task<ExecPolicyOperationResult> AddAllowlistEntryAsync(string? pattern)
    {
        if (IsDefaultsScope)
        {
            return Task.FromResult(new ExecPolicyOperationResult(
                ExecPolicyOperationStatus.RulesUnavailableForDefaults));
        }

        var trimmed = pattern?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return Task.FromResult(new ExecPolicyOperationResult(
                ExecPolicyOperationStatus.EmptyPattern));
        }

        if (!ExecApprovalsStore.IsValidAllowlistPattern(trimmed))
        {
            return Task.FromResult(new ExecPolicyOperationResult(
                ExecPolicyOperationStatus.InvalidPattern));
        }

        var scopeId = _selectedScopeId;
        var requiredExistingScopeId = GetRequiredExistingScopeId(scopeId);
        return MutateAsync(file =>
        {
            var agent = GetOrCreateAgent(file, scopeId);
            var allowlist = agent.Allowlist ??= [];
            var existing = allowlist.FirstOrDefault(entry =>
                string.Equals(
                    entry.Pattern?.Trim(),
                    trimmed,
                    StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(entry.ArgPattern));
            if (existing is not null)
            {
                existing.Pattern = trimmed;
                existing.Source = null;
                return;
            }

            allowlist.Add(new ExecAllowlistEntry
            {
                Id = Guid.NewGuid(),
                Pattern = trimmed,
            });
        }, requiredExistingScopeId);
    }

    internal Task<ExecPolicyOperationResult> RemoveAllowlistEntryAsync(
        Guid? id,
        string? pattern,
        string? argPattern = null,
        string? source = null)
    {
        if (IsDefaultsScope)
        {
            return Task.FromResult(new ExecPolicyOperationResult(
                ExecPolicyOperationStatus.RulesUnavailableForDefaults));
        }

        var scopeId = _selectedScopeId;
        var requiredExistingScopeId = GetRequiredExistingScopeId(scopeId);
        var normalizedPattern = pattern?.Trim();
        return MutateAsync(file =>
        {
            if (file.Agents is null
                || !file.Agents.TryGetValue(scopeId, out var agent)
                || agent?.Allowlist is null)
            {
                return;
            }

            agent.Allowlist.RemoveAll(entry =>
                id.HasValue
                    ? entry.Id == id
                    : !string.IsNullOrWhiteSpace(normalizedPattern)
                    && string.Equals(
                        entry.Pattern?.Trim(),
                        normalizedPattern,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        entry.ArgPattern,
                        argPattern,
                        StringComparison.Ordinal)
                    && string.Equals(
                        entry.Source,
                        source,
                        StringComparison.Ordinal));
            PruneEmptyAgent(file, scopeId, agent);
        }, requiredExistingScopeId);
    }

    private Task<ExecPolicyOperationResult> MutateSelectedPolicyAsync(
        Action<ExecApprovalsDefaults?, ExecApprovalsAgent?> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var scopeId = _selectedScopeId;
        var requiredExistingScopeId = GetRequiredExistingScopeId(scopeId);
        return MutateAsync(file =>
        {
            if (scopeId.Length == 0)
            {
                file.Defaults ??= new ExecApprovalsDefaults();
                mutation(file.Defaults, null);
                return;
            }

            var agent = GetOrCreateAgent(file, scopeId);
            mutation(null, agent);
            PruneEmptyAgent(file, scopeId, agent);
        }, requiredExistingScopeId);
    }

    private async Task<ExecPolicyOperationResult> MutateAsync(
        Action<ExecApprovalsFile> mutation,
        string? requiredExistingScopeId = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        SetBusy(true);
        try
        {
            for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
            {
                var current = attempt == 0 && _snapshot is not null
                    ? _snapshot
                    : await _store.GetSnapshotAsync().ConfigureAwait(false);
                if (attempt > 0
                    && requiredExistingScopeId is not null
                    && (current.File.Agents is null
                        || !current.File.Agents.ContainsKey(requiredExistingScopeId)))
                {
                    ApplySnapshot(current);
                    return new(ExecPolicyOperationStatus.Conflict);
                }
                var replacement = CloneFile(current.File);
                mutation(replacement);

                var updated = await _store.ReplaceAsync(current.Hash, replacement)
                    .ConfigureAwait(false);
                if (updated is null)
                    continue;

                ApplySnapshot(updated);
                return ExecPolicyOperationResult.Success();
            }

            var reloadError = await TryReloadAsync().ConfigureAwait(false);
            return new(ExecPolicyOperationStatus.Conflict, reloadError);
        }
        catch (IOException ex)
        {
            var reloadError = await TryReloadAsync().ConfigureAwait(false);
            return new(
                ExecPolicyOperationStatus.SaveFailed,
                CombineOperationErrors(ex.Message, reloadError));
        }
        catch (UnauthorizedAccessException ex)
        {
            var reloadError = await TryReloadAsync().ConfigureAwait(false);
            return new(
                ExecPolicyOperationStatus.SaveFailed,
                CombineOperationErrors(ex.Message, reloadError));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<string?> TryReloadAsync()
    {
        try
        {
            ApplySnapshot(await _store.GetSnapshotAsync().ConfigureAwait(false));
            return null;
        }
        catch (IOException ex)
        {
            return $"Reload failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Reload failed: {ex.Message}";
        }
    }

    private static string CombineOperationErrors(
        string primaryError,
        string? reloadError) =>
        string.IsNullOrWhiteSpace(reloadError)
            ? primaryError
            : $"{primaryError} {reloadError}";

    private void ApplySnapshot(ExecApprovalsSnapshot snapshot)
    {
        _snapshot = snapshot;
        var ids = new List<string>
        {
            DefaultsScopeId,
            WildcardScopeId,
            MainScopeId,
        };
        foreach (var agentId in snapshot.File.Agents?.Keys ?? (IEnumerable<string>)[])
        {
            if (!ids.Contains(agentId, StringComparer.Ordinal))
                ids.Add(agentId);
        }

        _availableScopes = ids.Select(id => new ExecPolicyScopeOption(id)).ToArray();
        if (!_availableScopes.Any(scope =>
                string.Equals(scope.Id, _selectedScopeId, StringComparison.Ordinal)))
        {
            _selectedScopeId = DefaultsScopeId;
        }
        PublishState();
    }

    private void SetBusy(bool value)
    {
        if (IsBusy == value)
            return;
        IsBusy = value;
        PublishState();
    }

    private void PublishState()
    {
        if (_dispatcher.HasThreadAccess)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _dispatcher.TryEnqueue(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    private static string NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? DefaultsScopeId : scopeId.Trim();

    private string? GetRequiredExistingScopeId(string scopeId) =>
        scopeId.Length > 0
        && _snapshot?.File.Agents?.ContainsKey(scopeId) == true
            ? scopeId
            : null;

    private static ExecApprovalsAgent GetOrCreateAgent(
        ExecApprovalsFile file,
        string scopeId)
    {
        file.Agents ??= new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal);
        if (!file.Agents.TryGetValue(scopeId, out var agent) || agent is null)
        {
            agent = new ExecApprovalsAgent();
            file.Agents[scopeId] = agent;
        }
        return agent;
    }

    private static void PruneEmptyAgent(
        ExecApprovalsFile file,
        string scopeId,
        ExecApprovalsAgent agent)
    {
        if (agent.Security is null
            && agent.Ask is null
            && agent.AskFallback is null
            && agent.AutoAllowSkills is null
            && (agent.Allowlist is null || agent.Allowlist.Count == 0))
        {
            file.Agents?.Remove(scopeId);
        }
    }

    private static ExecApprovalsFile CloneFile(ExecApprovalsFile source) =>
        new()
        {
            Version = source.Version,
            Socket = source.Socket is null
                ? null
                : new ExecApprovalsSocketConfig
                {
                    Path = source.Socket.Path,
                    Token = source.Socket.Token,
                },
            Defaults = source.Defaults is null
                ? null
                : new ExecApprovalsDefaults
                {
                    Security = source.Defaults.Security,
                    Ask = source.Defaults.Ask,
                    AskFallback = source.Defaults.AskFallback,
                    AutoAllowSkills = source.Defaults.AutoAllowSkills,
                },
            Agents = source.Agents?.ToDictionary(
                pair => pair.Key,
                pair => CloneAgent(pair.Value),
                StringComparer.Ordinal),
        };

    private static ExecApprovalsAgent CloneAgent(ExecApprovalsAgent source) =>
        new()
        {
            Security = source.Security,
            Ask = source.Ask,
            AskFallback = source.AskFallback,
            AutoAllowSkills = source.AutoAllowSkills,
            Allowlist = source.Allowlist?.Select(CloneEntry).ToList(),
        };

    private static ExecAllowlistEntry CloneEntry(ExecAllowlistEntry source) =>
        new()
        {
            Id = source.Id,
            Pattern = source.Pattern,
            Source = source.Source,
            ArgPattern = source.ArgPattern,
            LastUsedAt = source.LastUsedAt,
            LastResolvedPath = source.LastResolvedPath,
        };
}

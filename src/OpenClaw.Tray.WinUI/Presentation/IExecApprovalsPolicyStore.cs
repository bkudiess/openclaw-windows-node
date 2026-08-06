using OpenClaw.Shared.ExecApprovals;

namespace OpenClawTray.Presentation;

internal interface IExecApprovalsPolicyStore
{
    Task<ExecApprovalsSnapshot> GetSnapshotAsync();

    Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement);
}

internal sealed class ExecApprovalsPolicyStore(ExecApprovalsStore store)
    : IExecApprovalsPolicyStore
{
    private readonly ExecApprovalsStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public Task<ExecApprovalsSnapshot> GetSnapshotAsync() =>
        _store.GetSnapshotAsync();

    public Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement) =>
        _store.ReplaceAsync(baseHash, replacement);
}

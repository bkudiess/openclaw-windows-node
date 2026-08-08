namespace OpenClaw.Shared;

/// <summary>
/// Coalesces eager session refreshes while preserving the historical one-page
/// publication bound used by existing SessionsUpdated consumers.
/// </summary>
internal sealed class SessionPublicationCoordinator : IDisposable
{
    public const int MaximumPublishedSessions = 100;

    private readonly Func<SessionListRequest, CancellationToken, Task<SessionListResult>> _fetchPage;
    private readonly Func<
        int,
        IReadOnlyList<SessionInfo>,
        SessionInfo[]?> _tryCommit;
    private readonly Action<SessionInfo[]> _publish;
    private readonly object _gate = new();
    private CancellationTokenSource _generationCancellation = new();
    private PublicationBatch? _pending;
    private int _generation;
    private bool _workerRunning;
    private bool _disposed;

    public SessionPublicationCoordinator(
        Func<SessionListRequest, CancellationToken, Task<SessionListResult>> fetchPage,
        Func<int, IReadOnlyList<SessionInfo>, SessionInfo[]?> tryCommit,
        Action<SessionInfo[]> publish)
    {
        _fetchPage = fetchPage ?? throw new ArgumentNullException(nameof(fetchPage));
        _tryCommit = tryCommit ?? throw new ArgumentNullException(nameof(tryCommit));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public Task RequestRefreshAsync(
        string? agentId = null,
        int? expectedConnectionGeneration = null)
    {
        var normalizedAgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startWorker = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (expectedConnectionGeneration.HasValue &&
                expectedConnectionGeneration.Value != _generation)
            {
                throw new OperationCanceledException(
                    "Session publication request belongs to a stale connection generation.");
            }
            if (_pending is null || _pending.Generation != _generation)
            {
                _pending = new PublicationBatch(_generation, normalizedAgentId);
            }
            else
            {
                // One queued trailing batch is enough for any burst. Use the latest
                // requested scope while completing every coalesced caller together.
                _pending.AgentId = normalizedAgentId;
            }

            _pending.Waiters.Add(waiter);
            if (!_workerRunning)
            {
                _workerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
            _ = RunAsync();
        return waiter.Task;
    }

    public void AdvanceConnectionGeneration()
    {
        CancellationTokenSource oldGeneration;
        CancellationToken oldToken;
        PublicationBatch? canceled;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            oldGeneration = _generationCancellation;
            oldToken = oldGeneration.Token;
            _generationCancellation = new CancellationTokenSource();
            canceled = _pending;
            _pending = null;
        }

        CancelAndDispose(oldGeneration);
        if (canceled is not null)
            CancelBatch(canceled, oldToken);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            PublicationBatch? batch;
            CancellationToken generationToken;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _workerRunning = false;
                    return;
                }

                batch = _pending;
                _pending = null;
                generationToken = _generationCancellation.Token;
            }

            if (batch.Generation != GetGeneration())
            {
                CancelBatch(batch, generationToken);
                continue;
            }

            try
            {
                var page = await _fetchPage(
                    new SessionListRequest
                    {
                        AgentId = batch.AgentId,
                        Limit = MaximumPublishedSessions,
                    },
                    generationToken).ConfigureAwait(false);
                generationToken.ThrowIfCancellationRequested();
                var rows = BuildBoundedRows(page.Sessions);

                // This lock is the publication linearization point: generation
                // changes ordered before it suppress the callback, while changes
                // ordered after it cancel completion of the old batch. Never invoke
                // the external callback while holding the coordinator lock.
                lock (_gate)
                {
                    if (_disposed || batch.Generation != _generation)
                        throw new OperationCanceledException(generationToken);
                }
                var publication = _tryCommit(batch.Generation, rows);
                if (publication is null || !IsCurrent(batch.Generation))
                {
                    CancelBatch(batch, generationToken);
                    continue;
                }
                _publish(publication);

                if (IsCurrent(batch.Generation))
                {
                    foreach (var waiter in batch.Waiters)
                        waiter.TrySetResult();
                }
                else
                {
                    CancelBatch(batch, generationToken);
                }
            }
            catch (OperationCanceledException)
            {
                CancelBatch(batch, generationToken);
            }
            catch (Exception ex)
            {
                if (IsCurrent(batch.Generation))
                {
                    foreach (var waiter in batch.Waiters)
                        waiter.TrySetException(ex);
                }
                else
                {
                    CancelBatch(batch, generationToken);
                }
            }
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate)
            return !_disposed && generation == _generation;
    }

    private int GetGeneration()
    {
        lock (_gate)
            return _generation;
    }

    private static IReadOnlyList<SessionInfo> BuildBoundedRows(
        IReadOnlyList<SessionInfo> sessions)
    {
        var rows = new List<SessionInfo>(
            Math.Min(MaximumPublishedSessions, sessions.Count));
        var indicesByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Key))
                continue;
            if (indicesByKey.TryGetValue(session.Key, out var index))
            {
                rows[index] = session.Clone();
            }
            else if (rows.Count < MaximumPublishedSessions)
            {
                indicesByKey[session.Key] = rows.Count;
                rows.Add(session.Clone());
            }
        }
        return rows;
    }

    private static void CancelBatch(PublicationBatch batch, CancellationToken token)
    {
        foreach (var waiter in batch.Waiters)
            waiter.TrySetCanceled(token);
    }

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    public void Dispose()
    {
        CancellationTokenSource generation;
        CancellationToken generationToken;
        PublicationBatch? canceled;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            generation = _generationCancellation;
            generationToken = generation.Token;
            canceled = _pending;
            _pending = null;
        }

        CancelAndDispose(generation);
        if (canceled is not null)
            CancelBatch(canceled, generationToken);
    }

    private sealed class PublicationBatch(int generation, string? agentId)
    {
        public int Generation { get; } = generation;
        public string? AgentId { get; set; } = agentId;
        public List<TaskCompletionSource> Waiters { get; } = [];
    }
}

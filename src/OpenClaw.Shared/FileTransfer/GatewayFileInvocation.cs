namespace OpenClaw.Shared.FileTransfer;

/// <summary>One authenticated invocation, never reusable across connection generations.</summary>
internal sealed class GatewayFileInvocation : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _progressGate = new(1);
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<long, string, CancellationToken, ValueTask> _progress;
    private readonly Task _heartbeats;
    private long _progressSequence;
    private long _inputSequence;
    private readonly object _inputGate = new();

    public GatewayFileInvocation(long generation, Func<long, string, CancellationToken, ValueTask> progress,
        CancellationToken cancellationToken)
    {
        Generation = generation;
        _progress = progress;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel = new FileFrameChannel(SendProgressAsync, _lifetime.Token);
        _heartbeats = HeartbeatAsync();
    }

    public long Generation { get; }
    public FileFrameChannel Channel { get; }

    public void Receive(long seq, string payload)
    {
        lock (_inputGate)
        {
            if (seq < _inputSequence) return; // Upstream drops stale outer sequence numbers.
            if (seq != _inputSequence || seq > FileLimits.SafeInteger)
            {
                Channel.Abort();
                return;
            }
            _inputSequence++;
            Channel.Receive(payload);
        }
    }

    private async ValueTask SendProgressAsync(string chunk, CancellationToken cancellationToken)
    {
        await _progressGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _progress(_progressSequence++, chunk, cancellationToken).ConfigureAwait(false); }
        finally { _progressGate.Release(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                await SendProgressAsync("", _lifetime.Token).ConfigureAwait(false);
        }
        catch { Channel.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await Channel.DisposeAsync().ConfigureAwait(false);
        await _heartbeats.ConfigureAwait(false);
    }
}

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Trusted installation resolver. Implementations verify the product/RID payload manifest
/// and retain source and executable image/dependency identity until this lease is disposed.
/// The executor alone supplies an optional persistent cache root outside writable scratch.
/// A null directory retains the installed image for unsupported-Windows host execution.
/// </summary>
public interface IFixedFileWorkerResolver
{
    FixedFileWorkerLease Resolve(string? payloadCacheRoot = null, CancellationToken ct = default);
}

public abstract class FixedFileWorkerLease : IDisposable
{
    public abstract string ImagePath { get; }
    public abstract string PayloadDirectory { get; }
    public abstract void Dispose();
}

/// <summary>
/// An internal binary protocol, not argv, cwd, environment, policy or mode selection.
/// The callback must observe cancellation and service all pipes. Registered blocking pipe workers
/// remain owned through interruption and final drain. Native SDK pipes require synchronous IO on
/// these owned workers. Resources are null only in positive unsupported-Windows host mode.
/// </summary>
public sealed record FixedFileWorkload<T>(
    Func<NodeSandboxProcessIo, CancellationToken, Task<T>> Protocol, int TimeoutMs);

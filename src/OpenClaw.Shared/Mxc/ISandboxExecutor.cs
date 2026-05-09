using System.Text.Json;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Abstraction for executing a capability invocation inside containment.
/// Mirrors the <see cref="ICommandRunner"/> pattern that already exists for
/// system.run, broadened to cover any capability shape (system.run today;
/// location.get / browser.proxy / camera / etc. in later slices).
/// </summary>
/// <remarks>
/// Implementations:
/// <list type="bullet">
/// <item><see cref="OneShotAppContainerExecutor"/> — Slice 1: per-call AppContainer via Node + mxc-sdk.</item>
/// <item><c>WorkerSandboxExecutor</c> — Slice 7: long-lived worker over named-pipe JSON-RPC.</item>
/// <item><c>HostFallbackExecutor</c> — when containment unavailable in BestEffort mode.</item>
/// </list>
/// All implementations are expected to throw <see cref="SandboxUnavailableException"/>
/// when they cannot serve the request because of a missing backend (e.g. unsupported
/// Windows build, missing wxc-exec.exe). Callers in fail-closed mode translate that
/// into a denied invocation; callers in best-effort mode swap to a host runner.
/// </remarks>
public interface ISandboxExecutor
{
    /// <summary>Stable identifier for telemetry and the activity-stream badge.</summary>
    /// <example>"mxc-oneshot-appc", "mxc-isosession-worker", "host-fallback"</example>
    string Name { get; }

    /// <summary>True if this executor enforces containment. False = host fallback path.</summary>
    bool IsContained { get; }

    /// <summary>Execute the request inside containment.</summary>
    /// <exception cref="SandboxUnavailableException">
    /// Thrown when the executor's backend cannot serve this request.
    /// </exception>
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Capability invocation routed through an <see cref="ISandboxExecutor"/>.
/// Generic across capability shapes (shell exec, structured-data fetch, etc.).
/// </summary>
public sealed record SandboxExecutionRequest(
    string CapabilityCommand,
    JsonElement Args,
    SandboxPolicy Policy,
    int TimeoutMs,
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// Result of a sandboxed capability invocation. Mirrors <see cref="CommandResult"/>
/// for shell-shaped invocations, and adds <see cref="StructuredResult"/> for
/// capability-shaped invocations whose output is JSON.
/// </summary>
public sealed record SandboxExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long DurationMs,
    string ContainmentTag,
    JsonElement? StructuredResult = null);

/// <summary>
/// Thrown by an <see cref="ISandboxExecutor"/> when its backend cannot serve a
/// request (e.g. unsupported Windows build, missing wxc-exec.exe, OS feature off).
/// Caller policy decides whether to fail-closed or fall back.
/// </summary>
public sealed class SandboxUnavailableException : Exception
{
    public SandboxUnavailableException(string reason) : base(reason) { }
    public SandboxUnavailableException(string reason, Exception inner) : base(reason, inner) { }
}

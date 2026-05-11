using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Implements <see cref="ISandboxExecutor"/> by spawning <c>node.exe</c> with
/// <c>tools/mxc/run-command.cjs</c>, which calls
/// <c>@microsoft/mxc-sdk.spawnSandboxFromConfig({usePty:false})</c> to run the
/// payload inside a one-shot AppContainer.
/// </summary>
/// <remarks>
/// Slice 1 implementation. Slice 7 adds <c>WorkerSandboxExecutor</c> for
/// state-aware <c>isolation_session</c>; Slice 8 adds per-capability AppContainer
/// composition; this class stays in the codebase as the lightest-weight option
/// for low-frequency calls (e.g. <c>system.run</c>) and the fallback when
/// <c>isolation_session</c> isn't deployed.
/// </remarks>
public sealed class OneShotAppContainerExecutor : ISandboxExecutor
{
    public string Name => "mxc-oneshot-appc";
    public bool IsContained => true;

    private readonly MxcAvailability _availability;
    private readonly string _runCommandScriptPath;
    private readonly string _nodeExecutablePath;
    private readonly IOpenClawLogger _logger;
    private readonly long _maxOutputBytes;

    /// <summary>Default cap on stdout/stderr returned to the host (4 MiB).</summary>
    public const long DefaultMaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Optional environment variable override for the Node executable used by the
    /// runner. Falls back to <c>node.exe</c> on PATH.
    /// </summary>
    public const string NodeExecutableOverrideEnvVar = "OPENCLAW_NODE_EXEC";

    public OneShotAppContainerExecutor(
        MxcAvailability availability,
        string runCommandScriptPath,
        IOpenClawLogger? logger = null,
        long maxOutputBytes = DefaultMaxOutputBytes,
        string? nodeExecutableOverride = null)
    {
        _availability = availability;
        _runCommandScriptPath = runCommandScriptPath;
        _logger = logger ?? NullLogger.Instance;
        _maxOutputBytes = maxOutputBytes;
        _nodeExecutablePath = nodeExecutableOverride
            ?? Environment.GetEnvironmentVariable(NodeExecutableOverrideEnvVar)
            ?? "node.exe";
    }

    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default)
    {
        if (!_availability.IsAppContainerAvailable)
            throw new SandboxUnavailableException(
                _availability.UnsupportedReasons.FirstOrDefault() ?? "AppContainer unavailable");

        if (!_availability.IsWxcExecResolvable)
            throw new SandboxUnavailableException("wxc-exec.exe not found");

        if (!File.Exists(_runCommandScriptPath))
            throw new SandboxUnavailableException(
                $"run-command.cjs not found at {_runCommandScriptPath}");

        var bridgeRequest = new BridgeRequest(
            CapabilityCommand: request.CapabilityCommand,
            Args: request.Args,
            Policy: request.Policy,
            Cwd: request.Cwd,
            Env: request.Env,
            TimeoutMs: request.TimeoutMs,
            MaxOutputBytes: request.MaxOutputBytes ?? _maxOutputBytes,
            WxcExecPath: _availability.WxcExecPath);

        var requestJson = JsonSerializer.Serialize(bridgeRequest, BridgeJson);

        var psi = new ProcessStartInfo
        {
            FileName = _nodeExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(_runCommandScriptPath);

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new SandboxUnavailableException(
                $"Failed to start node.exe at '{_nodeExecutablePath}': {ex.Message}", ex);
        }

        // Caller-controlled timeout governs how long the bridge has to return.
        // Add a small grace so the bridge can clean up before we kill it.
        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs + 5000 : 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0)
            cts.CancelAfter(timeoutMs);

        try
        {
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), cts.Token);
            await process.StandardInput.FlushAsync(cts.Token);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        var stdoutCap = Math.Max(_maxOutputBytes, request.MaxOutputBytes ?? 0);
        // C# side cap: allow a bit of headroom so the bridge JSON envelope
        // (which contains the Node-capped command output) doesn't get truncated
        // by the outer reader. Add 256 KiB envelope overhead.
        var envelopeCap = stdoutCap + (256L * 1024L);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, envelopeCap, cts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, envelopeCap, cts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            KillProcessTree(process);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        sw.Stop();

        if (timedOut)
        {
            return new SandboxExecutionResult(
                ExitCode: -1,
                Stdout: stdout,
                Stderr: stderr.Length > 0 ? stderr : "Sandboxed invocation timed out.",
                TimedOut: true,
                DurationMs: sw.ElapsedMilliseconds,
                ContainmentTag: "mxc",
                StructuredResult: null);
        }

        // Bridge writes a single JSON envelope to stdout on completion.
        if (TryParseBridgeResponse(stdout, out var response))
        {
            return new SandboxExecutionResult(
                ExitCode: response.ExitCode,
                Stdout: response.Stdout,
                Stderr: response.Stderr,
                TimedOut: response.TimedOut,
                DurationMs: response.DurationMs == 0 ? sw.ElapsedMilliseconds : response.DurationMs,
                ContainmentTag: response.ContainmentTag ?? "mxc",
                StructuredResult: response.StructuredResult);
        }

        // Bridge crashed or returned malformed output. Surface as a sandbox failure
        // — node-side stderr likely has the diagnostic.
        _logger.Warn($"[mxc] bridge returned malformed output ({stdout.Length} bytes); stderr={Truncate(stderr, 200)}");
        return new SandboxExecutionResult(
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            TimedOut: false,
            DurationMs: sw.ElapsedMilliseconds,
            ContainmentTag: "mxc",
            StructuredResult: null);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, long maxBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        long bytesRead = 0;
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, ct); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }

            if (read == 0)
                break;

            // Approximate cap: chars × 2 bytes upper bound for UTF-16.
            bytesRead += read * 2;
            sb.Append(buffer, 0, read);
            if (bytesRead >= maxBytes)
            {
                sb.Append("\n[output truncated]");
                break;
            }
        }
        return sb.ToString();
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort */ }
    }

    private static bool TryParseBridgeResponse(string json, out BridgeResponse response)
    {
        response = default!;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            response = JsonSerializer.Deserialize<BridgeResponse>(json.Trim(), BridgeJson)!;
            return response is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static readonly JsonSerializerOptions BridgeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            // Enums must serialize as camelCase strings so @microsoft/mxc-sdk
            // (which expects "none" / "read" / "write" / "all") accepts them.
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase),
        },
    };

    private sealed record BridgeRequest(
        string CapabilityCommand,
        JsonElement Args,
        SandboxPolicy Policy,
        string? Cwd,
        IReadOnlyDictionary<string, string>? Env,
        int TimeoutMs,
        long MaxOutputBytes,
        string? WxcExecPath);

    private sealed record BridgeResponse(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        long DurationMs,
        string? ContainmentTag,
        JsonElement? StructuredResult);
}

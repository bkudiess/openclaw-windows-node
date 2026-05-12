using System.Text.Json;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Adapts the existing <see cref="ICommandRunner"/> seam so production
/// <c>system.run</c> invocations get sandboxed via MXC AppContainer.
/// Plugs into <c>SystemCapability.SetCommandRunner(...)</c> exactly where
/// <c>LocalCommandRunner</c> plugs in today.
/// </summary>
/// <remarks>
/// Honors <see cref="SettingsData.SystemRunSandboxEnabled"/>:
/// <list type="bullet">
/// <item><c>true</c> (default) — sandbox via MXC; deny invocation if MXC unavailable.</item>
/// <item><c>false</c> — bypass MXC; route through the host runner.</item>
/// </list>
/// There is no host-fallback path when sandbox is enabled and MXC is missing —
/// the call is denied with an explanatory error. Per user directive: "if sandbox
/// enabled, only run on sandbox."
/// </remarks>
public sealed class MxcCommandRunner : ICommandRunner
{
    public string Name => "mxc";

    private readonly ISandboxExecutor _executor;
    private readonly ICommandRunner _hostFallback;
    private readonly Func<SettingsData> _settingsProvider;
    private readonly Func<string> _settingsDirectoryPathProvider;
    private readonly IOpenClawLogger _logger;

    public MxcCommandRunner(
        ISandboxExecutor executor,
        ICommandRunner hostFallback,
        Func<SettingsData> settingsProvider,
        Func<string> settingsDirectoryPathProvider,
        IOpenClawLogger? logger = null)
    {
        _executor = executor;
        _hostFallback = hostFallback;
        _settingsProvider = settingsProvider;
        _settingsDirectoryPathProvider = settingsDirectoryPathProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var settings = _settingsProvider();

        if (!settings.SystemRunSandboxEnabled)
        {
            _logger.Info("[mxc] sandbox=disabled; routing system.run through host runner");
            return await _hostFallback.RunAsync(request, ct);
        }

        var policy = MxcPolicyBuilder.ForSystemRun(settings, _settingsDirectoryPathProvider());
        var argsJson = SerializeArgs(request);

        // Compute the effective timeout: take the smaller of the agent-supplied
        // timeout (request.TimeoutMs) and the user's sandbox cap (policy.TimeoutMs).
        // A zero/null on either side means "no cap from that side".
        var effectiveTimeoutMs = CombineTimeouts(request.TimeoutMs, policy.TimeoutMs);

        var sandboxRequest = new SandboxExecutionRequest(
            CapabilityCommand: "system.run",
            Args: argsJson,
            Policy: policy,
            TimeoutMs: effectiveTimeoutMs,
            Cwd: request.Cwd,
            Env: request.Env,
            MaxOutputBytes: settings.SandboxMaxOutputBytes > 0
                ? settings.SandboxMaxOutputBytes
                : null);

        try
        {
            var sandboxed = await _executor.ExecuteAsync(sandboxRequest, ct);
            return new CommandResult
            {
                Stdout = sandboxed.Stdout,
                Stderr = sandboxed.Stderr,
                ExitCode = sandboxed.ExitCode,
                TimedOut = sandboxed.TimedOut,
                DurationMs = sandboxed.DurationMs,
            };
        }
        catch (SandboxUnavailableException ex)
        {
            _logger.Warn(
                $"[mxc] system.run DENIED (sandbox enabled but unavailable: {ex.Message}). " +
                "Disable the sandbox toggle in Debug to fall back to host execution.");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr =
                    "Sandboxing is enabled for system.run on this machine, but MXC is unavailable. " +
                    $"Reason: {ex.Message}. " +
                    "Update Windows or disable the system.run sandbox in the Debug page to run on host.",
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (gateway disconnect, agent abort). Propagate so the
            // caller sees the cancellation rather than a fake "exited 0" response.
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed for ANY other error (bridge crashed, JSON malformed, IO
            // failure on stdin). Returning a -1 CommandResult is what the agent
            // pipeline understands — letting the exception escape here can crash
            // the node loop and ultimately the tray.
            _logger.Warn($"[mxc] system.run sandbox execution failed: {ex.GetType().Name}: {ex.Message}");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr =
                    "Sandboxed system.run failed with an unexpected error: " +
                    $"{ex.GetType().Name}: {ex.Message}",
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }
    }

    private static JsonElement SerializeArgs(CommandRequest request)
    {
        var payload = new
        {
            command = request.Command,
            shell = request.Shell ?? "powershell",
            args = request.Args ?? Array.Empty<string>(),
            cwd = request.Cwd,
            env = request.Env,
            timeoutMs = request.TimeoutMs,
        };
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static int CombineTimeouts(int agentMs, int? policyMs)
    {
        // Treat <= 0 as "no cap on this side."
        var hasAgent = agentMs > 0;
        var hasPolicy = policyMs is > 0;
        if (hasAgent && hasPolicy) return Math.Min(agentMs, policyMs!.Value);
        if (hasAgent) return agentMs;
        if (hasPolicy) return policyMs!.Value;
        return 0;
    }
}

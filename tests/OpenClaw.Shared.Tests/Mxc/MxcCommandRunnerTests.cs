using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcCommandRunnerTests
{
    private static SettingsData NewSettings(bool sandboxEnabled = true)
    {
        return new SettingsData
        {
            SystemRunSandboxEnabled = sandboxEnabled,
            SystemRunAllowOutbound = false,
            SystemRunAllowLocalNetwork = false,
        };
    }

    private static MxcCommandRunner NewRunner(
        ISandboxExecutor executor,
        ICommandRunner hostFallback,
        SettingsData settings)
    {
        return new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => "C:\\test\\settings",
            NullLogger.Instance);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DeniesWhenSandboxUnavailable()
    {
        var executor = new FakeSandboxExecutor { ThrowsUnavailable = true, UnavailableReason = "test reason" };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Sandboxing is enabled", result.Stderr);
        Assert.Contains("test reason", result.Stderr);
        // Fallback must NOT have been called.
        Assert.Null(fallback.LastRequest);
    }

    [Fact]
    public async Task RunAsync_SandboxDisabled_AlwaysRoutesToHost()
    {
        var executor = new FakeSandboxExecutor(); // healthy
        var fallback = new FakeCommandRunner
        {
            Result = new CommandResult { ExitCode = 0, Stdout = "host" },
        };
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: false));

        var result = await runner.RunAsync(new CommandRequest { Command = "echo hi" });

        Assert.Equal("host", result.Stdout);
        Assert.NotNull(fallback.LastRequest);
        // Executor must not have been touched.
        Assert.Null(executor.LastRequest);
    }

    [Fact]
    public async Task RunAsync_Success_MapsSandboxResultIntoCommandResult()
    {
        var executor = new FakeSandboxExecutor
        {
            Result = new SandboxExecutionResult(
                ExitCode: 0,
                Stdout: "hello world",
                Stderr: string.Empty,
                TimedOut: false,
                DurationMs: 123,
                ContainmentTag: "mxc"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "Get-Process",
            Shell = "powershell",
            Cwd = "C:\\",
            TimeoutMs = 5000,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello world", result.Stdout);
        Assert.Equal(123, result.DurationMs);
        Assert.False(result.TimedOut);

        // Sandbox request should carry the capability + command + shell.
        Assert.NotNull(executor.LastRequest);
        Assert.Equal("system.run", executor.LastRequest!.CapabilityCommand);
        var args = executor.LastRequest.Args;
        Assert.Equal("Get-Process", args.GetProperty("command").GetString());
        Assert.Equal("powershell", args.GetProperty("shell").GetString());
        Assert.Equal(5000, executor.LastRequest.TimeoutMs);
    }

    [Fact]
    public async Task RunAsync_SandboxEnabled_DoesNotFallBack_OnSandboxFailure()
    {
        // SandboxUnavailableException is the only exception that triggers the deny path.
        // A normal failed exec inside the sandbox propagates as an error CommandResult.
        var executor = new FakeSandboxExecutor
        {
            Result = new SandboxExecutionResult(
                ExitCode: 7,
                Stdout: string.Empty,
                Stderr: "sandboxed command failed",
                TimedOut: false,
                DurationMs: 1,
                ContainmentTag: "mxc"),
        };
        var fallback = new FakeCommandRunner();
        var runner = NewRunner(executor, fallback, NewSettings(sandboxEnabled: true));

        var result = await runner.RunAsync(new CommandRequest { Command = "fail-me" });

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("sandboxed command failed", result.Stderr);
        // Fallback must NOT have been used.
        Assert.Null(fallback.LastRequest);
    }

    private sealed class FakeSandboxExecutor : ISandboxExecutor
    {
        public string Name => "fake";
        public bool IsContained => true;

        public SandboxExecutionRequest? LastRequest { get; private set; }
        public SandboxExecutionResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, 0, "mxc");
        public bool ThrowsUnavailable { get; set; }
        public string UnavailableReason { get; set; } = "fake unavailable";

        public Task<SandboxExecutionResult> ExecuteAsync(
            SandboxExecutionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowsUnavailable)
                throw new SandboxUnavailableException(UnavailableReason);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public string Name => "fake-host";
        public CommandRequest? LastRequest { get; private set; }
        public CommandResult Result { get; set; } = new() { ExitCode = 0, Stdout = string.Empty };
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}

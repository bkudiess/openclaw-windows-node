using System.Text;
using OpenClawTray.Services.LocalGatewaySetup;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public class LocalGatewaySetupTests
{
    [Fact]
    public async Task DrainAsync_ReturnsCompletedReadImmediately()
    {
        var task = Task.FromResult("hello");
        var result = await WslExeCommandRunner.DrainAsync(task, TimeSpan.FromSeconds(1), new NullLogger(), isStderr: false);
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task DrainAsync_ReturnsEmpty_WhenReadHangsBeyondTimeout()
    {
        // Regression: PR #274 smoke test — `wsl.exe --list --verbose` returned but stdout
        // ReadToEndAsync hung indefinitely because the gateway distro / wslhost descendants
        // inherited and held the redirected stdout pipe handle. The wizard's "checking
        // system" step (HasDistroAsync → ListDistrosAsync) blocked forever. DrainAsync now
        // bounds the post-exit drain so the wizard surfaces partial output instead of
        // hanging the entire app.
        var neverCompletes = new TaskCompletionSource<string>().Task;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await WslExeCommandRunner.DrainAsync(neverCompletes, TimeSpan.FromMilliseconds(150), new NullLogger(), isStderr: false);
        sw.Stop();
        Assert.Equal(string.Empty, result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"DrainAsync should return promptly after timeout; took {sw.Elapsed}");
    }

    [Fact]
    public void ParseDistroList_ParsesVerboseWslOutput()
    {
        const string output = """
          NAME                   STATE           VERSION
        * Ubuntu                 Running         2
          OpenClawGateway        Stopped         2
          Legacy                 Stopped         1
        """;

        var distros = WslExeCommandRunner.ParseDistroList(output);

        Assert.Equal(3, distros.Count);
        Assert.Contains(distros, d => d.Name == "OpenClawGateway" && d.State == "Stopped" && d.Version == 2);
        Assert.Contains(distros, d => d.Name == "Legacy" && d.Version == 1);
    }

    [Fact]
    public void ParseStatus_ReadsDefaultAndWslVersions()
    {
        const string output = """
        Default Version: 1
        WSL version: 2.1.5.0
        Kernel version: 5.15.146.1-2
        """;

        var status = WslExeCommandRunner.ParseStatus(output);

        Assert.Equal(1, status.DefaultVersion);
        Assert.Equal("2.1.5.0", status.WslVersion);
        Assert.Equal("5.15.146.1-2", status.KernelVersion);
    }

    [Fact]
    public void RuntimeConfiguration_ReadsOnlyCleanWslEnvironment()
    {
        var environment = new FakeSetupEnvironment(new Dictionary<string, string?>
        {
            [LocalGatewaySetupRuntimeConfiguration.DistroNameVariable] = "OpenClawGatewayE2E",
            [LocalGatewaySetupRuntimeConfiguration.InstanceInstallLocationVariable] = @"C:\openclaw\wsl",
            [LocalGatewaySetupRuntimeConfiguration.AllowExistingDistroVariable] = "1"
        });

        var config = LocalGatewaySetupRuntimeConfiguration.FromEnvironment(environment);

        Assert.Equal("OpenClawGatewayE2E", config.DistroName);
        Assert.Equal(@"C:\openclaw\wsl", config.InstanceInstallLocation);
        Assert.True(config.AllowExistingDistro);
    }

    [Fact]
    public async Task Preflight_BlocksExistingOpenClawDistro()
    {
        var runner = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: true));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "distro_exists" && issue.Severity == LocalGatewaySetupSeverity.Blocking);
    }

    [Fact]
    public async Task Preflight_WslStatusFailure_IncludesWslLogsHelp()
    {
        var runner = new FakeWslCommandRunner { WslStatusExitCode = 1 };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: true));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "wsl_unavailable" && issue.Message.Contains("aka.ms/wsllogs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preflight_AllowsExistingGatewayOwnedLoopbackPort_WhenExistingDistroAllowed()
    {
        var runner = new FakeWslCommandRunner
        {
            Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)],
            CommandOutputByContains = { ["gateway status"] = "{\"ok\":true}" }
        };
        var preflight = new LocalGatewayPreflightProbe(runner, new FixedPortProbe(available: false));

        var result = await preflight.RunAsync(new LocalGatewaySetupOptions { AllowExistingDistro = true });

        Assert.True(result.CanContinue);
        Assert.Contains(result.Issues, issue => issue.Code == "gateway_port_already_active" && issue.Severity == LocalGatewaySetupSeverity.Warning);
        Assert.Contains(runner.Commands, command => command.Count == 8 && command[7].Contains("--url 'ws://localhost:18789'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_UsesCraigApprovedInstallCommand_AndTrustsExitCode()
    {
        using var temp = new TempDirectory();
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner();
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation });

        Assert.True(result.Success);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual([
            "--install",
            "Ubuntu-24.04",
            "--name",
            "OpenClawGateway",
            "--location",
            installLocation,
            "--no-launch",
            "--version",
            "2"]));
        Assert.DoesNotContain(wsl.Commands, command => command.Contains("--web-download"));
        Assert.DoesNotContain(wsl.Commands, command => command.Contains("--from-file"));
    }

    [Fact]
    public async Task WslStoreInstanceInstaller_FailedInstall_IncludesWslLogsHelpWithoutPostconditionRecovery()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner { InstallExitCode = 42 };
        var installer = new WslStoreInstanceInstaller(wsl, createDirectory: _ => { });

        var result = await installer.EnsureInstalledAsync(new LocalGatewaySetupOptions { InstanceInstallLocation = temp.Path });

        Assert.False(result.Success);
        Assert.Equal("wsl_instance_install_failed", result.ErrorCode);
        Assert.Contains("aka.ms/wsllogs", result.ErrorMessage!);
        Assert.Single(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
        Assert.DoesNotContain(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "true"]));
    }

    [Fact]
    public async Task WslFirstBootConfigurator_WritesCraigWslConfigurationThroughWslExe()
    {
        var wsl = new FakeWslCommandRunner();
        var configurator = new WslFirstBootConfigurator(wsl);

        var result = await configurator.ConfigureAsync(new LocalGatewaySetupOptions());

        Assert.True(result.Success);
        var command = Assert.Single(wsl.Commands, command => command.Count == 8 && command[5] == "bash" && command[6] == "-lc");
        Assert.Contains("cat >/etc/wsl.conf", command[7]);
        Assert.Contains("[automount]", command[7]);
        Assert.Contains("enabled=false", command[7]);
        Assert.Contains("appendWindowsPath=false", command[7]);
        Assert.Contains("cat >/etc/wsl-distribution.conf", command[7]);
        Assert.Contains("loginctl enable-linger openclaw", command[7]);
        Assert.DoesNotContain("machine-id", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolv.conf", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\wsl", command[7], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--manage", "OpenClawGateway", "--set-default-user", "openclaw"]));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--terminate", "OpenClawGateway"]));
    }

    [Fact]
    public async Task OpenClawInstallCliLinuxInstaller_UsesUpstreamInstallerAndRedactsEvents()
    {
        var wsl = new FakeWslCommandRunner
        {
            CommandOutputByContains = { ["install-cli.sh"] = "{\"event\":\"progress\",\"message\":\"bootstrapToken: secret-token\"}" }
        };
        var installer = new OpenClawInstallCliLinuxInstaller(wsl);

        var result = await installer.InstallAsync(new LocalGatewaySetupOptions { OpenClawInstallVersion = "next" });

        Assert.True(result.Success);
        Assert.Contains(wsl.Commands, command => command.Count == 8 && command[7].Contains("https://openclaw.ai/install-cli.sh", StringComparison.Ordinal));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "openclaw", "--", "/opt/openclaw/bin/openclaw", "--version"]));
        Assert.DoesNotContain("secret-token", result.Events![0].RawLine);
        Assert.Contains("<redacted>", result.Events![0].RawLine);
    }

    [Fact]
    public async Task GatewayConfigurationPreparer_WritesLoopbackOnlyConfigWithoutBindOrTokenValue()
    {
        var wsl = new FakeWslCommandRunner();
        var preparer = new OpenClawCliGatewayConfigurationPreparer(wsl);

        const string sharedToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var result = await preparer.PrepareAsync(new LocalGatewaySetupOptions(), sharedToken);

        Assert.True(result.Success);
        var command = Assert.Single(wsl.Commands, command =>
            command.Count == 8
            && command[7].Contains("config set gateway.mode local", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.port 18789 --strict-json", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.auth.mode token", StringComparison.Ordinal)
            && command[7].Contains("config set gateway.auth.token", StringComparison.Ordinal)
            && !command[7].Contains("gateway.bind", StringComparison.Ordinal)
            && !command[7].Contains("lan", StringComparison.Ordinal));
        Assert.Contains(": \"${OPENCLAW_SHARED_GATEWAY_TOKEN:?missing shared gateway token}\"", command[7]);
        Assert.Contains("printf '%s' \"$OPENCLAW_SHARED_GATEWAY_TOKEN\" >/var/lib/openclaw/gateway-token", command[7]);
        Assert.DoesNotContain("od -An -N32", command[7]);
        Assert.DoesNotContain(sharedToken, string.Join(" ", command));
        var environment = Assert.Single(wsl.Environments);
        Assert.Equal(sharedToken, environment[SharedGatewayTokenEnvironment.VariableName]);
    }

    [Fact]
    public async Task EndpointResolver_UsesOnlyLocalhostCandidate()
    {
        var wsl = new FakeWslCommandRunner { CommandOutputByContains = { ["hostname -I"] = "172.30.138.183" } };
        var health = new ReachableOnlyHealthProbe("ws://localhost:18789");
        var resolver = new LocalGatewayEndpointResolver();

        var result = await resolver.ResolveAsync(new LocalGatewaySetupOptions { GatewayUrl = "ws://127.0.0.1:18789" }, "ws://127.0.0.1:18789", health, wsl);

        Assert.True(result.Success);
        Assert.Equal("ws://localhost:18789", result.GatewayUrl);
        Assert.Equal(["ws://localhost:18789"], health.Attempts);
        Assert.DoesNotContain(wsl.Commands, command => string.Join(" ", command).Contains("hostname -I", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootstrapTokenProvider_RunsGatewayQrCommandAndDecodesSetupCode()
    {
        var setupPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"url\":\"ws://localhost:18789\",\"bootstrapToken\":\"minted-token\"}"))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var runner = new FakeWslCommandRunner { RunInDistroOutput = $"{{\"setupCode\":\"{setupPayload}\",\"expiresAtMs\":1893456000000}}" };
        var provider = new WslGatewayCliBootstrapTokenProvider(runner, "/opt/openclaw/bin/openclaw");

        var result = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway", GatewayUrl = "ws://localhost:18789" });

        Assert.True(result.Success);
        Assert.Equal("minted-token", result.BootstrapToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1893456000000), result.ExpiresAtUtc);
        Assert.Contains(runner.Commands, command => command.Count == 3 && command[2].Contains("'/opt/openclaw/bin/openclaw' qr --json --url 'ws://localhost:18789'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedGatewayTokenProvider_GeneratesFreshLowercaseHexToken_WhenWslTokenMissing()
    {
        var runner = new FakeWslCommandRunner();
        var provider = new WslGatewayCliSharedGatewayTokenProvider(runner);

        var first = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });
        var second = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(SharedGatewayTokenSource.Generated, first.Source);
        Assert.Matches("^[0-9a-f]{64}$", first.Token!);
        Assert.Matches("^[0-9a-f]{64}$", second.Token!);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task SharedGatewayTokenProvider_PreservesExistingSafeWslToken()
    {
        var existing = new string('a', 64);
        var runner = new FakeWslCommandRunner
        {
            CommandOutputByContains = { ["cat /var/lib/openclaw/gateway-token"] = existing + "\n" }
        };
        var provider = new WslGatewayCliSharedGatewayTokenProvider(runner);

        var result = await provider.MintAsync(new LocalGatewaySetupState { DistroName = "OpenClawGateway" });

        Assert.True(result.Success);
        Assert.Equal(existing, result.Token);
        Assert.Equal(SharedGatewayTokenSource.PreservedFromWsl, result.Source);
    }

    [Fact]
    public async Task SettingsSharedGatewayTokenProvisioner_PersistsTokenOnlyAfterGatewayConfigSucceeds()
    {
        var settings = new FakeSetupSettings();
        var tokenProvider = new FakeSharedGatewayTokenProvider(new string('b', 64));
        var preparer = new FakeGatewayConfigurationPreparer();
        var provisioner = new SettingsSharedGatewayTokenProvisioner(settings, tokenProvider, preparer);

        var result = await provisioner.ProvisionAsync(new LocalGatewaySetupState(), new LocalGatewaySetupOptions());

        Assert.True(result.Success);
        Assert.Equal(tokenProvider.Token, settings.Token);
        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(tokenProvider.Token, preparer.LastSharedGatewayToken);
    }

    [Fact]
    public async Task SettingsSharedGatewayTokenProvisioner_DoesNotPersistTokenWhenGatewayConfigFails()
    {
        var settings = new FakeSetupSettings();
        var tokenProvider = new FakeSharedGatewayTokenProvider(new string('c', 64));
        var preparer = new FakeGatewayConfigurationPreparer { Result = new GatewayConfigurationResult(false, "boom", "failed") };
        var provisioner = new SettingsSharedGatewayTokenProvisioner(settings, tokenProvider, preparer);

        var result = await provisioner.ProvisionAsync(new LocalGatewaySetupState(), new LocalGatewaySetupOptions());

        Assert.False(result.Success);
        Assert.Equal("", settings.Token);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(tokenProvider.Token, preparer.LastSharedGatewayToken);
    }

    [Fact]
    public async Task SettingsBootstrapTokenProvisioner_IgnoresSharedToken_WhenBootstrapTokenMissing()
    {
        var settings = new FakeSetupSettings { Token = "shared" };
        var provider = new FakeBootstrapTokenProvider("bootstrap");
        var provisioner = new SettingsBootstrapTokenProvisioner(settings, provider);

        var result = await provisioner.MintAsync(new LocalGatewaySetupState());

        Assert.True(result.Success);
        Assert.Equal("bootstrap", settings.BootstrapToken);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public void WslEnvironmentPassthrough_AppendsSharedTokenToExistingWslenvWithoutLoggingValues()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var environment = WslExeCommandRunner.BuildProcessEnvironment(
            new Dictionary<string, string> { ["WSLENV"] = "EXISTING/u" },
            new Dictionary<string, string> { [SharedGatewayTokenEnvironment.VariableName] = token });

        Assert.Equal(token, environment[SharedGatewayTokenEnvironment.VariableName]);
        Assert.Equal("EXISTING/u:OPENCLAW_SHARED_GATEWAY_TOKEN/u", environment["WSLENV"]);
        Assert.DoesNotContain(token, "[WSL] wsl.exe -d OpenClawGateway -u openclaw -- bash -lc <redacted>");
    }

    [Fact]
    public void WslEnvironmentPassthrough_AppendsGatewayTokenToExistingWslenvWithoutLoggingValues()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var environment = WslExeCommandRunner.BuildProcessEnvironment(
            new Dictionary<string, string> { ["WSLENV"] = "EXISTING/u" },
            new Dictionary<string, string> { [OpenClawGatewayTokenEnvironment.VariableName] = token });

        Assert.Equal(token, environment[OpenClawGatewayTokenEnvironment.VariableName]);
        Assert.Equal("EXISTING/u:OPENCLAW_GATEWAY_TOKEN/u", environment["WSLENV"]);
        Assert.DoesNotContain(token, "[WSL] wsl.exe -d OpenClawGateway -- bash -lc <redacted>");
    }

    [Fact]
    public async Task Engine_SharedGatewayProvisioning_ClosesBug6NonBootstrapSetupPath()
    {
        using var temp = new TempDirectory();
        var settings = new FakeSetupSettings();
        var sharedToken = new string('d', 64);
        var sharedProvider = new FakeSharedGatewayTokenProvider(sharedToken);
        var gatewayPreparer = new FakeGatewayConfigurationPreparer();
        var connector = new RecordingGatewayOperatorConnector();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway"), EnableWindowsTrayNodeByDefault = false },
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(new FakeWslCommandRunner(), new FixedPortProbe(available: true)),
            new FakeWslCommandRunner(),
            new SuccessfulHealthProbe(),
            new SettingsBootstrapTokenProvisioner(settings, new FakeBootstrapTokenProvider("bootstrap")),
            new SettingsOperatorPairingService(settings, connector),
            new FakeProvisioner(),
            wslInstanceInstaller: new WslStoreInstanceInstaller(new FakeWslCommandRunner(), createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: gatewayPreparer,
            gatewayServiceManager: new FakeGatewayServiceManager(),
            sharedGatewayTokenProvisioner: new SettingsSharedGatewayTokenProvisioner(settings, sharedProvider, gatewayPreparer));

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.Equal(sharedToken, settings.Token);
        Assert.Equal(sharedToken, connector.LastToken);
        Assert.False(connector.LastTokenIsBootstrap);
        Assert.Equal(OpenClawTray.Services.GatewayCredentialResolver.SourceSettingsToken,
            OpenClawTray.Services.GatewayCredentialResolver.Resolve(settings.Token, settings.BootstrapToken, null)!.Source);
    }

    [Fact]
    public async Task Engine_RunsCleanPhaseListThroughWindowsTrayNode()
    {
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.True(state.IsLocalOnly);
        Assert.Equal("ws://localhost:18789", state.GatewayUrl);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.CreateWslInstance);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.MintBootstrapToken);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.PairOperator);
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.PairWindowsTrayNode);
        Assert.DoesNotContain(state.History, h => h.Phase.ToString().Contains("Worker", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.History, h => h.Phase.ToString().Contains("Import", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, provisioning.BootstrapMintCalls);
        Assert.Equal(1, provisioning.OperatorPairCalls);
        Assert.Equal(1, provisioning.WindowsNodeReadinessCalls);
        Assert.Equal(1, provisioning.WindowsNodePairCalls);
    }

    [Fact]
    public async Task Engine_StopsBeforeInstall_WhenPreflightBlocks()
    {
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions(),
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning);

        var state = await engine.RunLocalOnlyAsync();

        // Preflight emits a single Blocking issue (distro_exists, since AllowExistingDistro
        // defaults to false). Engine now propagates that issue's code/message into
        // state.FailureCode/UserMessage instead of the generic "preflight_blocked"/
        // "This PC is not ready" pair, so retry surfaces actionable text. Block is
        // marked retryable so the page renders a Try Again button — preflight failures
        // are inherently fixable (free up the port, remove the existing distro, etc.)
        // rather than terminal.
        Assert.Equal(LocalGatewaySetupStatus.FailedRetryable, state.Status);
        Assert.Equal("distro_exists", state.FailureCode);
        Assert.Contains("OpenClawGateway", state.UserMessage);
        Assert.DoesNotContain(wsl.Commands, command => command.Count > 0 && command[0] == "--install");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Setup-engine progress-visibility tests:
    //   - Stale Failed*/Cancelled state is reset on fresh RunLocalOnlyAsync,
    //     so a prior failure never silently no-ops the next attempt.
    //   - At least one StateChanged event fires before any wsl invocation,
    //     and at least one fires before the method returns on every code path.
    //   - Preflight blocks propagate the most-specific issue's message into
    //     state.UserMessage so the page surfaces actionable text.
    //   - wsl --status failure is classified into specific issue codes
    //     (wsl_virtualization_disabled, wsl_vm_platform_missing, fallback
    //     wsl_unavailable) with redacted raw output preserved in Detail.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Engine_RunLocalOnly_ResetsStaleFailedTerminal_AndProgressesPastPreflight()
    {
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        // Pre-seed a Failed-state file as if a prior run had been blocked by preflight.
        // Without the reset, every RunPhaseAsync below early-exits silently and the page
        // sits on empty pending bullets forever.
        var stateStore = new LocalGatewaySetupStateStore(statePath);
        await stateStore.SaveAsync(new LocalGatewaySetupState
        {
            Phase = LocalGatewaySetupPhase.Failed,
            Status = LocalGatewaySetupStatus.FailedTerminal,
            FailureCode = "preflight_blocked",
            UserMessage = "This PC is not ready for local WSL gateway setup.",
            Issues = new()
            {
                new LocalGatewaySetupIssue("wsl_unavailable", "WSL is not available or is blocked by policy.", LocalGatewaySetupSeverity.Blocking)
            }
        });

        // Fresh-everything wsl/installer/etc. so this run's preflight passes and phases run.
        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            stateStore,
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        // Ran to completion — proves the stale FailedTerminal didn't silently kill the run.
        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.Null(state.FailureCode);
        // Stale Issues from the prior run must be cleared so they don't leak into the new run's diagnostic surface.
        Assert.DoesNotContain(state.Issues, i => i.Code == "wsl_unavailable");
        // Phases beyond Preflight actually executed.
        Assert.Contains(state.History, h => h.Phase == LocalGatewaySetupPhase.CreateWslInstance);
    }

    [Fact]
    public async Task Engine_RunLocalOnly_ResetsStaleFailedRetryable()
    {
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var stateStore = new LocalGatewaySetupStateStore(statePath);
        await stateStore.SaveAsync(new LocalGatewaySetupState
        {
            Phase = LocalGatewaySetupPhase.Failed,
            Status = LocalGatewaySetupStatus.FailedRetryable,
            FailureCode = "wsl_instance_install_failed"
        });

        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            stateStore,
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.Null(state.FailureCode);
    }

    [Fact]
    public async Task Engine_RunLocalOnly_ResetsStaleCancelled()
    {
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var stateStore = new LocalGatewaySetupStateStore(statePath);
        await stateStore.SaveAsync(new LocalGatewaySetupState
        {
            Phase = LocalGatewaySetupPhase.Cancelled,
            Status = LocalGatewaySetupStatus.Cancelled
        });

        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            stateStore,
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
    }

    [Fact]
    public async Task Engine_RunLocalOnly_AlwaysPublishesAtLeastOneStateChange_OnSuccessPath()
    {
        // Every code path through RunLocalOnlyAsync must publish at least one
        // StateChanged event before returning. Without this, the page sits on
        // its initial empty render forever, regardless of what the engine does.
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        int events = 0;
        engine.StateChanged += _ => events++;

        await engine.RunLocalOnlyAsync();

        Assert.True(events >= 1, $"Expected ≥1 StateChanged event; got {events}.");
    }

    [Fact]
    public async Task Engine_RunLocalOnly_AlwaysPublishesAtLeastOneStateChange_OnPreflightBlockedPath()
    {
        // Same publish invariant along the preflight-blocked path, where a stale
        // FailedTerminal in the state store would otherwise make every phase
        // early-exit silently and zero events fire.
        using var temp = new TempDirectory();
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var stateStore = new LocalGatewaySetupStateStore(statePath);
        // Fresh state with a wsl runner that fails preflight (port unavailable + distro exists).
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions(),
            stateStore,
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning);

        int events = 0;
        engine.StateChanged += _ => events++;

        await engine.RunLocalOnlyAsync();

        Assert.True(events >= 1, $"Expected ≥1 StateChanged event on preflight-block path; got {events}.");
    }

    [Fact]
    public async Task Engine_RunLocalOnly_FirstStateChangedShowsPreflightActive_BeforeAnyWslCall()
    {
        // The first StateChanged event must fire BEFORE the first wsl invocation
        // AND must already report Phase=Preflight, Status=Running. If the engine
        // published with Phase=NotStarted (or just a UserMessage subtitle change)
        // the page would render no spinner — the user sees a blank window for
        // the entire ~60s cold-start of HasDistroAsync. The page's stage map only
        // marks a bullet active when state.Phase falls inside the stage's phase
        // range, so this is the right contract.
        using var temp = new TempDirectory();
        var wsl = new CallSequenceRecordingFakeWsl();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        int firstEventWslCommandCount = -1;
        LocalGatewaySetupPhase firstEventPhase = LocalGatewaySetupPhase.NotStarted;
        LocalGatewaySetupStatus firstEventStatus = LocalGatewaySetupStatus.Pending;
        engine.StateChanged += s =>
        {
            if (firstEventWslCommandCount == -1)
            {
                firstEventWslCommandCount = wsl.Commands.Count;
                firstEventPhase = s.Phase;
                firstEventStatus = s.Status;
            }
        };

        await engine.RunLocalOnlyAsync();

        Assert.True(firstEventWslCommandCount >= 0, "Expected at least one StateChanged event.");
        Assert.Equal(0, firstEventWslCommandCount); // First StateChanged must precede first wsl call.
        Assert.Equal(LocalGatewaySetupPhase.Preflight, firstEventPhase);
        Assert.Equal(LocalGatewaySetupStatus.Running, firstEventStatus);
    }

    [Fact]
    public async Task Engine_RunLocalOnly_DoesNotAddDuplicatePreflightHistoryEntries()
    {
        // The pre-StartPhase(Preflight) call before HasDistroAsync used to
        // produce a duplicate History entry (one from the engine's own
        // pre-publish, one from RunPhaseAsync's StartPhase) for the same
        // logical Preflight boundary. RunPhaseAsync now skips its
        // StartPhase when the phase is already running for the same phase,
        // so History should contain exactly one Preflight entry per run.
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway") },
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new FakeGatewayServiceManager());

        var state = await engine.RunLocalOnlyAsync();

        var preflightEntries = state.History.Count(h => h.Phase == LocalGatewaySetupPhase.Preflight);
        Assert.Equal(1, preflightEntries);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PhaseFailureHints — when a setup phase blocks, the Block message is
    // augmented with phase-specific "things to try" guidance so the user
    // doesn't have to guess (regression observed 2026-05-08 where a user
    // enabled Hyper-V unnecessarily before realising a reboot was the fix).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PhaseFailureHints_CreateWslInstance_MentionsRestart()
    {
        var hint = PhaseFailureHints.HintFor(LocalGatewaySetupPhase.CreateWslInstance);
        Assert.NotNull(hint);
        Assert.Contains("restart", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Virtual Machine Platform", hint);
    }

    [Fact]
    public void PhaseFailureHints_ConfigureWslInstance_MentionsRestart()
    {
        var hint = PhaseFailureHints.HintFor(LocalGatewaySetupPhase.ConfigureWslInstance);
        Assert.NotNull(hint);
        Assert.Contains("restart", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PhaseFailureHints_InstallOpenClawCli_MentionsInternet()
    {
        var hint = PhaseFailureHints.HintFor(LocalGatewaySetupPhase.InstallOpenClawCli);
        Assert.NotNull(hint);
        Assert.Contains("internet", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PhaseFailureHints_StartGateway_MentionsWslShutdown()
    {
        var hint = PhaseFailureHints.HintFor(LocalGatewaySetupPhase.StartGateway);
        Assert.NotNull(hint);
        Assert.Contains("wsl --shutdown", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LocalGatewaySetupPhase.NotStarted)]
    [InlineData(LocalGatewaySetupPhase.Preflight)]
    [InlineData(LocalGatewaySetupPhase.EnsureWslEnabled)]
    [InlineData(LocalGatewaySetupPhase.MintBootstrapToken)]
    [InlineData(LocalGatewaySetupPhase.PairOperator)]
    [InlineData(LocalGatewaySetupPhase.Complete)]
    [InlineData(LocalGatewaySetupPhase.Failed)]
    public void PhaseFailureHints_PhasesWithoutSpecificGuidance_ReturnsNull(LocalGatewaySetupPhase phase)
    {
        // Preflight has its own classifier (ClassifyWslStatusFailure) that
        // already produces specific messages; we don't double-up on hints.
        // Phases past PairOperator are loopback-internal and rarely user-fixable.
        Assert.Null(PhaseFailureHints.HintFor(phase));
    }

    [Fact]
    public void PhaseFailureHints_Augment_AppendsHintToBaseMessage()
    {
        var augmented = PhaseFailureHints.Augment(
            LocalGatewaySetupPhase.CreateWslInstance,
            "Failed to create the OpenClaw Gateway WSL instance.");

        Assert.StartsWith("Failed to create the OpenClaw Gateway WSL instance.", augmented);
        Assert.Contains("Things to try:", augmented);
    }

    [Fact]
    public void PhaseFailureHints_Augment_LeavesBaseMessage_WhenNoHintApplies()
    {
        var augmented = PhaseFailureHints.Augment(
            LocalGatewaySetupPhase.PairOperator,
            "Some operator-pairing failure.");

        Assert.Equal("Some operator-pairing failure.", augmented);
    }

    [Fact]
    public void PhaseFailureHints_Augment_HandlesEmptyBaseMessage()
    {
        var augmented = PhaseFailureHints.Augment(LocalGatewaySetupPhase.CreateWslInstance, "");
        Assert.Contains("Things to try:", augmented);
        Assert.DoesNotContain("\n\nThings to try:", augmented); // No leading double newline.
    }

    [Fact]
    public async Task Engine_PreflightBlocked_PropagatesSpecificIssueIntoUserMessage()
    {
        // When preflight blocks, state.UserMessage must come from the most-specific
        // blocking issue's message — not the generic "This PC is not ready" string.
        // The page renders UserMessage; without this propagation the actionable
        // error text is buried in state.Issues which the page never displays.
        using var temp = new TempDirectory();
        var wsl = new FakeWslCommandRunner
        {
            WslStatusExitCode = 1,
            WslStatusOutput = "WSL2 is unable to start since virtualization is not enabled on this machine.\nFor information please visit https://aka.ms/enablevirtualization\n"
        };
        var provisioning = new FakeProvisioner();
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions(),
            new LocalGatewaySetupStateStore(System.IO.Path.Combine(temp.Path, "setup-state.json")),
            new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true)),
            wsl,
            new SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.FailedRetryable, state.Status);
        Assert.Equal("wsl_virtualization_disabled", state.FailureCode);
        Assert.NotNull(state.UserMessage);
        Assert.Contains("aka.ms/enablevirtualization", state.UserMessage);
        // Detail with redacted raw wsl --status output should be carried through too.
        var blockingIssue = state.Issues.First(i => i.Code == "wsl_virtualization_disabled");
        Assert.NotNull(blockingIssue.Detail);
        Assert.Contains("wsl_status_exit_code=1", blockingIssue.Detail);
    }

    [Fact]
    public async Task Preflight_VirtualizationDisabled_EmitsSpecificIssueWithEnableVirtLink()
    {
        // Known WSL failure modes get specific issue codes.
        // Virtualization-disabled is the highest-priority detection because the
        // remediation page (aka.ms/enablevirtualization) covers BOTH the firmware
        // virtualization toggle AND the Virtual Machine Platform feature.
        var wsl = new FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "WSL2 is unable to start since virtualization is not enabled on this machine.\nPlease ensure the \"Virtual Machine Platform\" optional component is enabled and virtualization is turned on in your computer's firmware settings.\nFor information please visit https://aka.ms/enablevirtualization\n"
        };
        var probe = new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true));

        var result = await probe.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        var virt = result.Issues.Single(i => i.Code == "wsl_virtualization_disabled");
        Assert.Equal(LocalGatewaySetupSeverity.Blocking, virt.Severity);
        Assert.Contains("aka.ms/enablevirtualization", virt.Message);
        Assert.Contains("wsl --install --no-distribution", virt.Message);
    }

    [Fact]
    public async Task Preflight_VmPlatformMissing_EmitsSpecificIssueWithInstallCommand()
    {
        // Output mentions VM Platform but NOT the virtualization phrase (e.g. on a
        // machine where firmware virt is fine but the Windows feature was uninstalled).
        var wsl = new FakeWslCommandRunner
        {
            WslStatusExitCode = -1,
            WslStatusOutput = "Please enable the \"Virtual Machine Platform\" optional component to use WSL.\n"
        };
        var probe = new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true));

        var result = await probe.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        var vmp = result.Issues.Single(i => i.Code == "wsl_vm_platform_missing");
        Assert.Equal(LocalGatewaySetupSeverity.Blocking, vmp.Severity);
        Assert.Contains("Virtual Machine Platform", vmp.Message);
        Assert.Contains("wsl --install --no-distribution", vmp.Message);
        // No spurious aka.ms/enablevirtualization (that's the virt-disabled path).
        Assert.DoesNotContain("aka.ms/enablevirtualization", vmp.Message);
    }

    [Fact]
    public async Task Preflight_GenericFailure_StillFallsBackToWslUnavailable()
    {
        // Defense-in-depth: an unexpected wsl --status output (Microsoft has
        // changed these strings between WSL versions) must still produce a known
        // issue code rather than crashing or silently succeeding.
        var wsl = new FakeWslCommandRunner
        {
            WslStatusExitCode = 1,
            WslStatusOutput = "Some entirely new error string we have never seen before."
        };
        var probe = new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true));

        var result = await probe.RunAsync(new LocalGatewaySetupOptions());

        Assert.False(result.CanContinue);
        var fallback = result.Issues.Single(i => i.Code == "wsl_unavailable");
        Assert.Equal(LocalGatewaySetupSeverity.Blocking, fallback.Severity);
        Assert.NotNull(fallback.Detail);
        Assert.Contains("Some entirely new error", fallback.Detail);
    }

    [Fact]
    public async Task Preflight_AllWslStatusFailureIssues_IncludeRawWslOutputInDetail()
    {
        // Every issue triggered by wsl --status failure includes the redacted raw
        // output in Detail, so support / debug-bundle have the diagnostic context
        // even when we can't classify the failure.
        var cases = new[]
        {
            ("WSL2 is unable to start since virtualization is not enabled.", "wsl_virtualization_disabled"),
            ("Please enable the \"Virtual Machine Platform\" optional component.", "wsl_vm_platform_missing"),
            ("New unrecognized failure mode.", "wsl_unavailable")
        };

        foreach (var (output, expectedCode) in cases)
        {
            var wsl = new FakeWslCommandRunner { WslStatusExitCode = 1, WslStatusOutput = output };
            var probe = new LocalGatewayPreflightProbe(wsl, new FixedPortProbe(available: true));

            var result = await probe.RunAsync(new LocalGatewaySetupOptions());

            var issue = result.Issues.Single(i => i.Code == expectedCode);
            Assert.NotNull(issue.Detail);
            Assert.Contains(output.Substring(0, Math.Min(20, output.Length)), issue.Detail);
            Assert.Contains("wsl_status_exit_code=1", issue.Detail);
        }
    }

    /// <summary>
    /// Subclass of <see cref="FakeWslCommandRunner"/> that's identical in behaviour
    /// — included only to make the
    /// <see cref="Engine_RunLocalOnly_PublishesInitializingState_BeforeAnyWslCall"/>
    /// test self-documenting (the Commands list is what we observe; the type name
    /// signals intent).
    /// </summary>
    private sealed class CallSequenceRecordingFakeWsl : IWslCommandRunner
    {
        private readonly FakeWslCommandRunner _inner = new();
        public List<IReadOnlyList<string>> Commands => _inner.Commands;

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
            => _inner.RunAsync(arguments, cancellationToken, environment);
        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
            => _inner.ListDistrosAsync(cancellationToken);
        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
            => _inner.TerminateDistroAsync(name, cancellationToken);
        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
            => _inner.UnregisterDistroAsync(name, cancellationToken);
        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
            => _inner.RunInDistroAsync(name, command, cancellationToken, environment);
    }

    [Fact]
    public async Task LifecycleManager_RepairTerminatesOnlyGatewayDistroAndRestartsGatewayService()
    {
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Running", 2)] };
        var manager = new LocalGatewayLifecycleManager(new LocalGatewaySetupOptions(), wsl, new SuccessfulHealthProbe());

        var result = await manager.RepairAsync();

        Assert.True(result.Success);
        Assert.Contains("distro_terminated", result.Steps!);
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["--terminate", "OpenClawGateway"]));
        Assert.Contains(wsl.Commands, command => command.SequenceEqual(["-d", "OpenClawGateway", "-u", "root", "--", "systemctl", "restart", "openclaw-gateway.service"]));
        Assert.DoesNotContain(wsl.Commands, command => command.SequenceEqual(["--shutdown"]));
        Assert.DoesNotContain(wsl.Commands, command => string.Join(" ", command).Contains("openclaw-worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LifecycleManager_RemoveUnregistersDistroAndClearsLocalCredentials()
    {
        var settings = new FakeSetupSettings { Token = "token", BootstrapToken = "bootstrap", EnableNodeMode = true };
        var wsl = new FakeWslCommandRunner { Distros = [new WslDistroInfo("OpenClawGateway", "Stopped", 2)] };
        var manager = new LocalGatewayLifecycleManager(new LocalGatewaySetupOptions(), wsl, new SuccessfulHealthProbe(), settings);

        var result = await manager.RemoveAsync(new LocalGatewayRemoveRequest(ConfirmRemove: true, ClearLocalCredentials: true));

        Assert.True(result.Success);
        Assert.Contains("OpenClawGateway", wsl.UnregisteredDistros);
        Assert.Equal("", settings.Token);
        Assert.Equal("", settings.BootstrapToken);
        Assert.False(settings.EnableNodeMode);
        Assert.True(settings.SaveCalled);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { Token = "existing-token" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenBootstrapTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { BootstrapToken = "bootstrap-abc" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenNonDefaultGatewayUrlAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { GatewayUrl = "ws://my-server:9000" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenOperatorDeviceTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "device-key-ed25519.json"),
            """{"DeviceToken":"op-device-token-value"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenNodeDeviceTokenExistsAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "device-key-ed25519.json"),
            """{"NodeDeviceToken":"node-device-token-value"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_ThrowsInvalidOperation_WhenActiveSetupStateAndNotConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path);
        var setupStatePath = Path.Combine(temp.Path, "setup-state.json");
        File.WriteAllText(setupStatePath, """{"Phase":"ConfigureWslInstance"}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGatewaySetupEngineFactory.CreateLocalOnly(
                settings,
                identityDataPath: temp.Path,
                setupStatePath: setupStatePath,
                replaceExistingConfigurationConfirmed: false));

        Assert.Contains("existing_config_replacement_not_confirmed", ex.Message);
    }

    [Fact]
    public void CreateLocalOnly_Succeeds_WhenTokenExistsAndConfirmed()
    {
        using var temp = new TempDirectory();
        var settings = new OpenClawTray.Services.SettingsManager(temp.Path) { Token = "existing-token" };

        var engine = LocalGatewaySetupEngineFactory.CreateLocalOnly(
            settings,
            replaceExistingConfigurationConfirmed: true);

        Assert.NotNull(engine);
    }

    internal sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        public List<WslDistroInfo> Distros { get; set; } = [];
        public List<string> UnregisteredDistros { get; } = [];
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];
        public int WslStatusExitCode { get; set; }
        public string WslStatusOutput { get; set; } = "";
        public string RunInDistroOutput { get; set; } = "";
        public int InstallExitCode { get; set; }
        public Dictionary<string, string> CommandOutputByContains { get; } = new();

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(arguments);
            if (environment is not null)
                Environments.Add(new Dictionary<string, string>(environment));
            if (arguments.SequenceEqual(["--status"]))
                return Task.FromResult(new WslCommandResult(WslStatusExitCode, WslStatusOutput, ""));

            if (arguments.Count > 0 && arguments[0] == "--install")
                return Task.FromResult(new WslCommandResult(InstallExitCode, "", InstallExitCode == 0 ? "" : "install failed"));

            var joined = string.Join(" ", arguments);
            foreach (var pair in CommandOutputByContains)
            {
                if (joined.Contains(pair.Key, StringComparison.Ordinal))
                    return Task.FromResult(new WslCommandResult(0, pair.Value, ""));
            }

            return Task.FromResult(new WslCommandResult(0, "", ""));
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WslDistroInfo>>(Distros.ToArray());

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            Commands.Add(["--terminate", name]);
            return Task.FromResult(new WslCommandResult(0, "", ""));
        }

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            UnregisteredDistros.Add(name);
            Distros.RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new WslCommandResult(0, "", ""));
        }

        public Task<WslCommandResult> RunInDistroAsync(string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? environment = null)
        {
            Commands.Add(command);
            if (environment is not null)
                Environments.Add(new Dictionary<string, string>(environment));
            return Task.FromResult(new WslCommandResult(0, RunInDistroOutput, ""));
        }
    }

    private sealed class FakeSetupEnvironment : ILocalGatewaySetupEnvironment
    {
        private readonly IReadOnlyDictionary<string, string?> _values;
        public FakeSetupEnvironment(IReadOnlyDictionary<string, string?> values) => _values = values;
        public string? GetVariable(string name) => _values.TryGetValue(name, out var value) ? value : null;
    }

    internal sealed class FixedPortProbe : IPortProbe
    {
        private readonly bool _available;
        public FixedPortProbe(bool available) => _available = available;
        public bool IsPortAvailable(int port) => _available;
    }

    internal sealed class SuccessfulHealthProbe : ILocalGatewayHealthProbe
    {
        public Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalGatewayHealthResult(true));
    }

    private sealed class ReachableOnlyHealthProbe : ILocalGatewayHealthProbe
    {
        private readonly string _reachableGatewayUrl;
        public ReachableOnlyHealthProbe(string reachableGatewayUrl) => _reachableGatewayUrl = reachableGatewayUrl;
        public List<string> Attempts { get; } = [];
        public Task<LocalGatewayHealthResult> WaitForHealthyAsync(string gatewayUrl, CancellationToken cancellationToken = default)
        {
            Attempts.Add(gatewayUrl);
            return Task.FromResult(gatewayUrl.Equals(_reachableGatewayUrl, StringComparison.OrdinalIgnoreCase)
                ? new LocalGatewayHealthResult(true)
                : new LocalGatewayHealthResult(false, "unreachable"));
        }
    }

    private sealed class FakeProvisioner : IBootstrapTokenProvisioner, IOperatorPairingService, IWindowsTrayNodeProvisioner
    {
        public int BootstrapMintCalls { get; private set; }
        public int OperatorPairCalls { get; private set; }
        public int WindowsNodeReadinessCalls { get; private set; }
        public int WindowsNodePairCalls { get; private set; }

        public Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            BootstrapMintCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IOperatorPairingService.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            OperatorPairCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            WindowsNodeReadinessCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            WindowsNodePairCalls++;
            return Task.FromResult(new ProvisioningResult(true));
        }
    }

    private sealed class FakeSetupSettings : ILocalGatewaySetupSettings
    {
        public string GatewayUrl { get; set; } = "";
        public string Token { get; set; } = "";
        public string BootstrapToken { get; set; } = "";
        public bool UseSshTunnel { get; set; } = true;
        public bool EnableNodeMode { get; set; }
        public bool SaveCalled { get; private set; }
        public int SaveCount { get; private set; }
        public void Save()
        {
            SaveCalled = true;
            SaveCount++;
        }
    }

    internal sealed class FakeWslInstanceConfigurator : IWslInstanceConfigurator
    {
        public Task<WslInstanceConfigurationResult> ConfigureAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslInstanceConfigurationResult(true));
    }

    internal sealed class FakeOpenClawLinuxInstaller : IOpenClawLinuxInstaller
    {
        public Task<OpenClawLinuxInstallResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OpenClawLinuxInstallResult(true));
    }

    private sealed class RecordingGatewayOperatorConnector : IGatewayOperatorConnector
    {
        public string? LastToken { get; private set; }
        public bool LastTokenIsBootstrap { get; private set; }

        public Task<GatewayOperatorConnectionResult> ConnectAsync(string gatewayUrl, string token, bool tokenIsBootstrapToken = false, CancellationToken cancellationToken = default)
        {
            LastToken = token;
            LastTokenIsBootstrap = tokenIsBootstrapToken;
            return Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
        }

        public Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(string gatewayUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
    }

    private sealed class FakeBootstrapTokenProvider : IBootstrapTokenProvider
    {
        private readonly string _token;
        public FakeBootstrapTokenProvider(string token) => _token = token;
        public int Calls { get; private set; }
        public Task<BootstrapTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new BootstrapTokenResult(true, _token));
        }
    }

    private sealed class FakeSharedGatewayTokenProvider : ISharedGatewayTokenProvider
    {
        public FakeSharedGatewayTokenProvider(string token) => Token = token;
        public string Token { get; }
        public Task<SharedGatewayTokenResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedGatewayTokenResult(true, Token, SharedGatewayTokenSource.Generated));
    }

    internal sealed class FakeGatewayConfigurationPreparer : IGatewayConfigurationPreparer
    {
        public GatewayConfigurationResult Result { get; set; } = new(true);
        public string? LastSharedGatewayToken { get; private set; }
        public Task<GatewayConfigurationResult> PrepareAsync(LocalGatewaySetupOptions options, string sharedGatewayToken, CancellationToken cancellationToken = default)
        {
            LastSharedGatewayToken = sharedGatewayToken;
            return Task.FromResult(Result);
        }
    }

    internal sealed class FakeGatewayServiceManager : IGatewayServiceManager
    {
        public Task<GatewayServiceOperationResult> InstallAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayServiceOperationResult(true));

        public Task<GatewayServiceOperationResult> StartAsync(LocalGatewaySetupOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayServiceOperationResult(true));
    }

    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }
}

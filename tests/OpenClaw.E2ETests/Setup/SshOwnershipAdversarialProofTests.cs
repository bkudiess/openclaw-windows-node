using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

[Collection("E2E Setup")]
public sealed class SshOwnershipAdversarialProofTests
{
    private readonly E2ESetupFixture _fixture;

    public SshOwnershipAdversarialProofTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
    }

    [E2EFact]
    public async Task UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing()
    {
        var proofDir = Path.Combine(_fixture.ArtifactDir, "pr1076-proof");
        var profileDir = Path.Combine(proofDir, "profile");
        var sshDir = Path.Combine(profileDir, ".ssh");
        var sshConfigPath = Path.Combine(profileDir, "ssh_config");
        Directory.CreateDirectory(sshDir);
        var gatewayPath = Path.Combine(_fixture.DataDir, "gateways.json");
        var settingsPath = Path.Combine(_fixture.DataDir, "settings.json");
        var originalGatewayBytes = File.ReadAllBytes(gatewayPath);
        var originalSettingsBytes = File.ReadAllBytes(settingsPath);
        var sshPort = E2ESetupFixture.AllocateFreePort();
        var tunnelPort = AllocateFreeForwardPortPair();
        var screenshotDegraded = Path.Combine(proofDir, "04-connection-degraded.png");
        var captureUiProof = string.Equals(
            Environment.GetEnvironmentVariable("OPENCLAW_CAPTURE_UI_PROOF"),
            "1",
            StringComparison.Ordinal);
        TcpListener? adversary = null;
        (string? Operator, string? Node) beforeTokens = (null, null);

        try
        {
            beforeTokens = ReadRoleTokens();
            Assert.False(string.IsNullOrWhiteSpace(beforeTokens.Operator));
            Assert.False(string.IsNullOrWhiteSpace(beforeTokens.Node));

            await _fixture.StopTrayAsync();
            await ConfigureProofSshAsync(profileDir, sshDir, sshPort);
            var proofSshd = await StartProofSshdAsync(profileDir, sshDir, sshPort);
            WriteObject("00-proof-sshd.json", new
            {
                unitName = proofSshd.UnitName,
                processId = proofSshd.ProcessId,
                executablePath = proofSshd.ExecutablePath,
                commandLine = proofSshd.CommandLine,
                hostAddress = proofSshd.HostAddress,
            });
            var identityFile = Path.Combine(sshDir, "id_ed25519").Replace('\\', '/');
            await File.WriteAllTextAsync(
                sshConfigPath,
                $"""
                Host *
                    BatchMode yes
                    IdentitiesOnly yes
                    IdentityFile "{identityFile}"
                    UserKnownHostsFile NUL
                    StrictHostKeyChecking no
                """);
            PatchActiveGateway(tunnelPort, proofSshd.HostAddress, sshPort, browserControlPort: null);
            _fixture.SetTrayEnvironmentVariable("HOME", profileDir);
            _fixture.SetTrayEnvironmentVariable("USERPROFILE", profileDir);
            _fixture.SetTrayEnvironmentVariable("OPENCLAW_E2E_SSH_CONFIG_FILE", sshConfigPath);
            if (captureUiProof)
            {
                _fixture.SetTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST", "1");
                _fixture.SetTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR", proofDir);
            }
            await _fixture.StartTrayAsync();

            var ownedListeners = WindowsTcpListenerSnapshot.Capture().Listeners
                .Where(listener => listener.Port == tunnelPort)
                .ToArray();
            var sshListeners = ownedListeners
                .Where(listener =>
                    string.Equals(listener.ProcessName, "ssh", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.NotEmpty(sshListeners);
            var sshProcessId = Assert.Single(sshListeners.Select(listener => listener.ProcessId).Distinct());
            var sshListener = Assert.Single(
                sshListeners,
                listener => listener.Address.Equals(IPAddress.Loopback));
            using var ready = await ReadStatusAsync();
            AssertReady(ready.RootElement);
            WriteJson("01-valid-ready.json", ready.RootElement);
            WriteObject("02-owned-listener.json", new
            {
                tunnelPort,
                owned = true,
                listenerCount = ownedListeners.Length,
                processName = sshListener.ProcessName,
                processId = sshProcessId,
                addresses = sshListeners.Select(listener => listener.Address.ToString()).ToArray(),
            });

            adversary = new TcpListener(IPAddress.Parse("127.0.0.2"), tunnelPort);
            adversary.Start();
            var competingListeners = WindowsTcpListenerSnapshot.Capture().Listeners
                .Where(listener => listener.Port == tunnelPort)
                .ToArray();
            Assert.Contains(competingListeners, listener => listener.ProcessId == Environment.ProcessId);
            Assert.Contains(competingListeners, listener => listener.ProcessId == sshListener.ProcessId);
            WriteObject("03-competing-listener.json", new
            {
                tunnelPort,
                listenerCount = competingListeners.Length,
                unrelatedListenerPresent = true,
                ownedProcessId = sshListener.ProcessId,
                unrelatedProcessId = Environment.ProcessId,
            });

            using (var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.reconnectNode"))
            {
                Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
            }
            using var degraded = await WaitForStatusAsync(
                status =>
                    status.GetProperty("overallState").GetString() == "Degraded" &&
                    status.GetProperty("nodeState").GetString() == "Error",
                TimeSpan.FromSeconds(45));
            Assert.Contains(
                "credentials were not sent",
                degraded.RootElement.GetProperty("nodeError").GetString(),
                StringComparison.OrdinalIgnoreCase);
            WriteJson("04-degraded-status.json", degraded.RootElement);
            using (var connectionStatus = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.status"))
            {
                WriteJson("04-connection-diagnostics.json", connectionStatus.RootElement);
            }
            if (captureUiProof)
                await NavigateAndCaptureAsync("connection", screenshotDegraded);

            adversary.Stop();
            adversary = null;

            using (var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.reconnectNode"))
            {
                Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
            }
            await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(90));
            await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60));
            using var recovered = await ReadStatusAsync();
            AssertReady(recovered.RootElement);
            WriteJson("05-recovered-ready.json", recovered.RootElement);

            var afterTokens = ReadRoleTokens();
            Assert.Equal(beforeTokens.Operator, afterTokens.Operator);
            Assert.Equal(beforeTokens.Node, afterTokens.Node);
            using (var approvals = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.pendingApprovals"))
            {
                Assert.True(approvals.RootElement.GetProperty("connected").GetBoolean());
                Assert.Equal(0, approvals.RootElement.GetProperty("totalPending").GetInt32());
                Assert.Empty(approvals.RootElement.GetProperty("devicePending").EnumerateArray());
                Assert.Empty(approvals.RootElement.GetProperty("nodePending").EnumerateArray());
                WriteJson("05-pending-approvals.json", approvals.RootElement);
            }

            await _fixture.StopTrayAsync();
            PatchActiveGateway(
                tunnelPort,
                proofSshd.HostAddress,
                sshPort,
                browserControlPort: tunnelPort + 4);
            await _fixture.StartTrayAsync(waitForConnection: false);
            using var endpointStatus = await WaitForConnectionDiagnosticsAsync(
                status =>
                    status.TryGetProperty("gateway", out var gateway) &&
                    gateway.TryGetProperty("browserProxyCaveat", out var caveat) &&
                    caveat.ValueKind == JsonValueKind.String &&
                    caveat.GetString()?.Contains(
                        "not the managed browser-proxy forward",
                        StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(45));
            var endpointGateway = endpointStatus.RootElement.GetProperty("gateway");
            Assert.Equal(
                tunnelPort + 4,
                endpointGateway.GetProperty("browserControlPort").GetInt32());
            WriteJson("06-unverified-browser-endpoint.json", endpointStatus.RootElement);

            await _fixture.StopTrayAsync();
            var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
            var clear = DeviceIdentityStore.BeginTransactionalTokenClear(identityDir);
            Assert.True(clear.Success, clear.Error);
            Assert.NotNull(clear.Transaction);
            var newerOperatorToken = $"proof-operator-{Guid.NewGuid():N}";
            var newerNodeToken = $"proof-node-{Guid.NewGuid():N}";
            var lateWriter = new DeviceIdentity(identityDir);
            lateWriter.Initialize();
            lateWriter.StoreDeviceTokenForRole("operator", newerOperatorToken);
            lateWriter.StoreDeviceTokenForRole("node", newerNodeToken);
            var restore = DeviceIdentityStore.RestoreTransactionalTokenClear(clear.Transaction!);
            Assert.Equal(DeviceTokenRestoreOutcome.Superseded, restore.Outcome);
            var lateWriterTokens = ReadRoleTokens();
            Assert.Equal(newerOperatorToken, lateWriterTokens.Operator);
            Assert.Equal(newerNodeToken, lateWriterTokens.Node);
            Assert.NotEqual(beforeTokens.Operator, lateWriterTokens.Operator);
            Assert.NotEqual(beforeTokens.Node, lateWriterTokens.Node);
            WriteObject("07-late-writer-rollback.json", new
            {
                restoreOutcome = restore.Outcome.ToString(),
                newerOperatorCredentialPreserved = true,
                newerNodeCredentialPreserved = true,
                originalOperatorCredentialWasNotRestored = true,
                originalNodeCredentialWasNotRestored = true,
            });

            WriteObject("proof-summary.json", new
            {
                head = ResolveHeadSha(),
                distro = _fixture.DistroName,
                gatewayPort = _fixture.GatewayPort,
                sshPort,
                tunnelPort,
                ambiguousListenerOwnershipRejectedBeforeCredentialSend = true,
                recoveredReady = true,
                sameOperatorCredential = true,
                sameNodeCredential = true,
                lateWriterWonRollback = true,
                degradedScreenshot = captureUiProof && File.Exists(screenshotDegraded),
                unverifiedEndpointDiagnostics = true,
            });
            WriteRedactedTrayLog();
        }
        finally
        {
            adversary?.Stop();
            await _fixture.StopTrayAsync();
            File.WriteAllBytes(gatewayPath, originalGatewayBytes);
            File.WriteAllBytes(settingsPath, originalSettingsBytes);
            if (!string.IsNullOrWhiteSpace(beforeTokens.Operator) &&
                !string.IsNullOrWhiteSpace(beforeTokens.Node))
            {
                var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
                var originalIdentity = new DeviceIdentity(identityDir);
                originalIdentity.Initialize();
                originalIdentity.StoreDeviceTokenForRole("operator", beforeTokens.Operator);
                originalIdentity.StoreDeviceTokenForRole("node", beforeTokens.Node);
            }
            _fixture.RemoveTrayEnvironmentVariable("HOME");
            _fixture.RemoveTrayEnvironmentVariable("USERPROFILE");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_E2E_SSH_CONFIG_FILE");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
            var sshdUnit = $"openclaw-pr1076-sshd-{sshPort}.service";
            await _fixture.RunInWslAsync(
                $"systemctl stop '{sshdUnit}' 2>/dev/null || true; " +
                $"systemctl reset-failed '{sshdUnit}' 2>/dev/null || true",
                TimeSpan.FromSeconds(15),
                inputViaStdin: true,
                user: "root");
            try { Directory.Delete(profileDir, recursive: true); } catch { }
            await _fixture.StartTrayAsync();
        }

        return;

        (string? Operator, string? Node) ReadRoleTokens()
        {
            var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(identityDir, "device-key-ed25519.json")));
            return (
                ReadString(document.RootElement, "DeviceToken"),
                ReadString(document.RootElement, "NodeDeviceToken"));
        }

        void PatchActiveGateway(
            int localTunnelPort,
            string sshHost,
            int localSshPort,
            int? browserControlPort)
        {
            var root = JsonNode.Parse(File.ReadAllText(gatewayPath))!.AsObject();
            var activeId = root["activeId"]!.GetValue<string>();
            var records = root["gateways"]!.AsArray();
            var active = records
                .Select(node => node!.AsObject())
                .Single(record => record["id"]!.GetValue<string>() == activeId);
            active["sshTunnel"] = JsonSerializer.SerializeToNode(
                new
                {
                    user = "openclaw",
                    host = sshHost,
                    remotePort = _fixture.GatewayPort,
                    localPort = localTunnelPort,
                    includeBrowserProxyForward = true,
                    sshPort = localSshPort,
                });
            if (browserControlPort.HasValue)
                active["browserControlPort"] = browserControlPort.Value;
            else
                active.Remove("browserControlPort");
            File.WriteAllText(
                gatewayPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        void WriteJson(string fileName, JsonElement element) =>
            File.WriteAllText(
                Path.Combine(proofDir, fileName),
                JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<object>(element.GetRawText()),
                    new JsonSerializerOptions { WriteIndented = true }));

        void WriteObject(string fileName, object value) =>
            File.WriteAllText(
                Path.Combine(proofDir, fileName),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

        void WriteRedactedTrayLog()
        {
            var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
            if (!File.Exists(logPath))
                return;
            var selected = File.ReadLines(logPath)
                .Where(line =>
                    line.Contains("listener", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("tunnel", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
                .TakeLast(200);
            File.WriteAllLines(
                Path.Combine(proofDir, "selected-tray-log.redacted.txt"),
                selected.Select(TokenSanitizer.SanitizeLogMessage));
        }
    }

    private async Task ConfigureProofSshAsync(
        string profileDir,
        string sshDir,
        int sshPort)
    {
        var keyPath = Path.Combine(sshDir, "id_ed25519");
        var keygen = await RunProcessAsync(
            "ssh-keygen.exe",
            ["-q", "-t", "ed25519", "-N", "", "-f", keyPath]);
        Assert.Equal(0, keygen.ExitCode);

        var install = await _fixture.RunInWslAsync(
            "set -e; export DEBIAN_FRONTEND=noninteractive; command -v sshd >/dev/null || { apt-get update -qq; apt-get install -y -qq --no-install-recommends openssh-server; }; ssh-keygen -A; install -d -m 0755 /run/sshd",
            TimeSpan.FromMinutes(3),
            user: "root");
        Assert.Equal(0, install.ExitCode);

        var publicKey = await File.ReadAllTextAsync(keyPath + ".pub");
        var publicKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey));
        var authorize = await _fixture.RunInWslAsync(
            $"set -e; install -d -m 700 -o openclaw -g openclaw /home/openclaw/.ssh; echo '{publicKeyBase64}' | base64 -d > /home/openclaw/.ssh/authorized_keys; chown openclaw:openclaw /home/openclaw/.ssh/authorized_keys; chmod 600 /home/openclaw/.ssh/authorized_keys",
            TimeSpan.FromSeconds(30),
            user: "root");
        Assert.Equal(0, authorize.ExitCode);

        _fixture.SetTrayEnvironmentVariable("HOME", profileDir);
        _fixture.SetTrayEnvironmentVariable("USERPROFILE", profileDir);
    }

    private async Task<ProofSshdProcess> StartProofSshdAsync(
        string profileDir,
        string sshDir,
        int sshPort)
    {
        var unitName = $"openclaw-pr1076-sshd-{sshPort}.service";
        var start = await _fixture.RunInWslAsync(
            $"set -e; systemctl stop '{unitName}' 2>/dev/null || true; " +
            $"systemctl reset-failed '{unitName}' 2>/dev/null || true; " +
            $"systemd-run --quiet --unit='{unitName}' --collect --property=Type=exec " +
            $"/usr/sbin/sshd -D -e -p {sshPort} -o KexAlgorithms=curve25519-sha256",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true,
            user: "root");
        Assert.Equal(0, start.ExitCode);

        var inspect = await _fixture.RunInWslAsync(
            "for i in $(seq 1 50); do " +
            $"if systemctl is-active --quiet '{unitName}'; then " +
            $"pid=$(systemctl show '{unitName}' -p MainPID --value); " +
            "if [ \"$pid\" != '0' ] && [ -r \"/proc/$pid/cmdline\" ]; then " +
            "exe=$(readlink -f \"/proc/$pid/exe\" 2>/dev/null || true); " +
            "cmd=$(tr '\\0' ' ' < \"/proc/$pid/cmdline\" 2>/dev/null || true); " +
            $"if [ \"$exe\" = '/usr/sbin/sshd' ] && [[ \"$cmd\" == *'-D'* ]] && [[ \"$cmd\" == *'-e'* ]] && [[ \"$cmd\" == *'-p {sshPort}'* ]]; then " +
            "printf '%s\\n%s\\n%s\\n' \"$pid\" \"$exe\" \"$cmd\"; exit 0; fi; fi; " +
            "fi; sleep 0.1; done; " +
            $"systemctl status '{unitName}' --no-pager >&2 || true; " +
            $"journalctl -u '{unitName}' -n 50 --no-pager >&2 || true; exit 1",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true,
            user: "root");
        Assert.Equal(0, inspect.ExitCode);
        var inspectionLines = inspect.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(inspectionLines.Length >= 3, $"Missing sshd PID/command proof: {inspect.Stdout}");
        Assert.True(int.TryParse(inspectionLines[0], out var pid), $"Invalid sshd PID: {inspectionLines[0]}");
        Assert.Equal("/usr/sbin/sshd", inspectionLines[1]);
        Assert.Contains("-D", inspectionLines[2], StringComparison.Ordinal);
        Assert.Contains("-e", inspectionLines[2], StringComparison.Ordinal);
        Assert.Contains($"-p {sshPort}", inspectionLines[2], StringComparison.Ordinal);

        var address = await _fixture.RunInWslAsync(
            "hostname -I | cut -d' ' -f1",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true,
            user: "root");
        Assert.Equal(0, address.ExitCode);
        var hostAddress = address.Stdout.Trim();
        Assert.True(
            IPAddress.TryParse(hostAddress, out _),
            $"Invalid WSL address: {TokenSanitizer.SanitizeLogMessage(hostAddress)}");

        ProcessResult? keyscan = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            keyscan = await RunProcessAsync(
                "ssh-keyscan.exe",
                ["-p", sshPort.ToString(), hostAddress],
                timeout: TimeSpan.FromSeconds(5));
            if (keyscan.ExitCode == 0 && !string.IsNullOrWhiteSpace(keyscan.Stdout))
                break;
            await Task.Delay(250);
        }
        Assert.NotNull(keyscan);
        Assert.True(
            keyscan.ExitCode == 0 && !string.IsNullOrWhiteSpace(keyscan.Stdout),
            $"ssh-keyscan failed: {TokenSanitizer.SanitizeLogMessage(keyscan.Stderr)}");
        await File.WriteAllTextAsync(Path.Combine(sshDir, "known_hosts"), keyscan.Stdout);

        var preflight = await RunProcessAsync(
            "ssh.exe",
            [
                "-o", "BatchMode=yes",
                "-o", "IdentitiesOnly=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", $"UserKnownHostsFile={Path.Combine(sshDir, "known_hosts")}",
                "-i", Path.Combine(sshDir, "id_ed25519"),
                "-p", sshPort.ToString(),
                $"openclaw@{hostAddress}",
                "true"
            ],
            new Dictionary<string, string>
            {
                ["HOME"] = profileDir,
                ["USERPROFILE"] = profileDir,
            },
            TimeSpan.FromSeconds(30));
        Assert.True(
            preflight.ExitCode == 0,
            $"SSH preflight failed ({preflight.ExitCode}): " +
            TokenSanitizer.SanitizeLogMessage(preflight.Stderr));
        return new ProofSshdProcess(
            unitName,
            pid,
            inspectionLines[1],
            inspectionLines[2],
            hostAddress);
    }

    private async Task<JsonDocument> ReadStatusAsync() =>
        await _fixture.Client!.CallToolExpectSuccessAsync("app.status");

    private async Task<JsonDocument> WaitForStatusAsync(
        Func<JsonElement, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string last = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var document = await ReadStatusAsync();
            last = document.RootElement.GetRawText();
            if (predicate(document.RootElement))
                return JsonDocument.Parse(last);
            await Task.Delay(500);
        }
        throw new TimeoutException($"Status predicate was not satisfied. Last: {last}");
    }

    private async Task<JsonDocument> WaitForConnectionDiagnosticsAsync(
        Func<JsonElement, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var last = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var status = await _fixture.Client!.CallToolExpectSuccessAsync("app.connection.status");
            last = status.RootElement.GetRawText();
            if (predicate(status.RootElement))
                return status;
            status.Dispose();
            await Task.Delay(500);
        }

        throw new TimeoutException(
            "Connection diagnostics predicate was not satisfied. Last: " +
            TokenSanitizer.SanitizeLogMessage(last));
    }

    private static void AssertReady(JsonElement status)
    {
        Assert.Equal("Ready", status.GetProperty("overallState").GetString());
        Assert.Equal("Connected", status.GetProperty("operatorState").GetString());
        Assert.Equal("Connected", status.GetProperty("nodeState").GetString());
        Assert.True(status.GetProperty("nodePaired").GetBoolean());
    }

    private async Task NavigateAndCaptureAsync(string page, string outputPath)
    {
        var captureStartedAt = DateTime.UtcNow;
        using var navigate = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.navigate",
            new { page });
        Assert.True(navigate.RootElement.GetProperty("navigated").GetBoolean());
        var captureDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            "Connection");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var capture = Directory.Exists(captureDirectory)
                ? Directory.EnumerateFiles(captureDirectory, "capture-*.png")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTimeUtc >= captureStartedAt.AddSeconds(-1))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (capture is not null)
            {
                try
                {
                    var isComposedFrame = false;
                    using (var stream = new FileStream(
                               capture.FullName,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite))
                    using (var bitmap = new Bitmap(stream))
                    {
                        var sampledColors = new HashSet<int>();
                        var xStep = Math.Max(1, bitmap.Width / 40);
                        var yStep = Math.Max(1, bitmap.Height / 40);
                        for (var y = 0; y < bitmap.Height; y += yStep)
                        {
                            for (var x = 0; x < bitmap.Width; x += xStep)
                                sampledColors.Add(bitmap.GetPixel(x, y).ToArgb());
                        }
                        isComposedFrame = sampledColors.Count >= 8;
                    }

                    if (isComposedFrame)
                    {
                        File.Copy(capture.FullName, outputPath, overwrite: true);
                        return;
                    }
                }
                catch (ArgumentException)
                {
                    // Capture is still being encoded; retry.
                }
                catch (IOException)
                {
                    // Capture is still being encoded; retry.
                }
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            "Connection page did not produce a composed XAML frame for proof capture.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        }
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }

            await process.WaitForExitAsync();
            var timeoutMessage =
                $"Process timed out after {effectiveTimeout.TotalSeconds:F0} seconds.";
            var stderrText = await stderr;
            return new ProcessResult(
                -1,
                await stdout,
                string.IsNullOrWhiteSpace(stderrText)
                    ? timeoutMessage
                    : $"{stderrText.TrimEnd()}{Environment.NewLine}{timeoutMessage}");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    private static string ResolveHeadSha()
    {
        var result = RunProcessAsync("git.exe", ["rev-parse", "HEAD"])
            .GetAwaiter()
            .GetResult();
        return result.ExitCode == 0 ? result.Stdout.Trim() : "unknown";
    }

    private static int AllocateFreeForwardPortPair()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.Next(20_000, 40_000);
            TcpListener? gatewayForward = null;
            TcpListener? browserForward = null;
            try
            {
                gatewayForward = StartExclusiveDualStackListener(candidate);
                browserForward = StartExclusiveDualStackListener(candidate + 2);
                return candidate;
            }
            catch (SocketException)
            {
                // Try another pair.
            }
            finally
            {
                browserForward?.Stop();
                gatewayForward?.Stop();
            }
        }

        throw new InvalidOperationException("Unable to allocate an SSH forward port pair.");
    }

    private static TcpListener StartExclusiveDualStackListener(int port)
    {
        var listener = new TcpListener(IPAddress.IPv6Any, port);
        listener.Server.DualMode = true;
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return listener;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ProofSshdProcess(
        string UnitName,
        int ProcessId,
        string ExecutablePath,
        string CommandLine,
        string HostAddress);

}

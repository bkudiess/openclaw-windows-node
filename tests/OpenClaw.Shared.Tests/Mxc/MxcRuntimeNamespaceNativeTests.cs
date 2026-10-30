using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

[Collection("MxcOwnership")]
public sealed class MxcRuntimeNamespaceNativeTests
{
    [MxcNativeFact]
    public Task NativeSdk_AncestorDenyAllowsOnlyExecutorIssuedCodeAndScratchChildren() => ProbeAsync(false);

    [MxcNativeFact]
    public Task NativeBuilder_TwoConcurrentOperationsCannotAccessSiblingRuntimeUnderBroadWriteGrant() => ProbeAsync(true);

    private static async Task ProbeAsync(bool productionPolicy)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
            ".mxc-test-runs", Guid.NewGuid().ToString("N"));
        var context = MxcSyntheticContext.Create(root);
        var profile = context.RequireFolder("Profile");
        var runs = Directory.CreateDirectory(Path.Combine(profile, "OpenClawMxc", "Runs")).FullName;
        var sensitive = context.ProtectedPaths[0];
        context = context with { ExecutorPrivatePaths = [runs] };
        var operations = new[] { "first", "second" }.Select(name => Directory.CreateDirectory(Path.Combine(runs, name)).FullName).ToArray();
        foreach (var operation in operations)
        {
            Directory.CreateDirectory(Path.Combine(operation, "worker"));
            Directory.CreateDirectory(Path.Combine(operation, "scratch"));
            File.WriteAllText(Path.Combine(operation, "sentinel"), "sibling");
        }
        File.WriteAllText(Path.Combine(sensitive, "sentinel"), "sensitive");
        var aclPaths = operations.SelectMany(p => new[] { p, Path.Combine(p, "worker"), Path.Combine(p, "scratch") })
            .Concat([root, profile, runs, sensitive]).ToArray();
        var acls = aclPaths.ToDictionary(p => p, Acl);
        var count = productionPolicy ? 2 : 1;
        var readyCount = 0;
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var artifacts = Environment.GetEnvironmentVariable("OPENCLAW_MXC_PROOF_ROOT")!;
        Directory.CreateDirectory(artifacts);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, count).Select(ExecuteAsync));
            Assert.All(operations, operation =>
            {
                Assert.Equal("sibling", File.ReadAllText(Path.Combine(operation, "sentinel")));
                Assert.False(File.Exists(Path.Combine(operation, "new")));
                Assert.False(File.Exists(Path.Combine(operation, "worker", "added.dll")));
            });
            Assert.Equal("sensitive", File.ReadAllText(Path.Combine(sensitive, "sentinel")));
            Assert.All(acls, pair => Assert.Equal(pair.Value, Acl(pair.Key)));
        }
        finally { Directory.Delete(root, true); }

        async Task ExecuteAsync(int index)
        {
            var code = Path.Combine(operations[index], "worker");
            var scratch = Path.Combine(operations[index], "scratch");
            var sibling = operations[1 - index];
            var script = Path.Combine(code, "probe.ps1");
            File.WriteAllText(script, """
                param($Runs, $Sibling, $Sensitive, $Scratch, $Code)
                $ErrorActionPreference = 'Stop'
                [Console]::WriteLine('READY')
                if ([Console]::ReadLine() -ne 'go') { throw 'handshake' }
                function Denied($Name, [scriptblock]$Action) {
                    try { & $Action; throw "UNEXPECTED_ACCESS:$Name" }
                    catch [UnauthorizedAccessException] { [Console]::WriteLine("DENIED:$Name") }
                }
                [IO.File]::WriteAllText((Join-Path $Scratch 'own'), 'scratch')
                if ([IO.File]::ReadAllText((Join-Path $Scratch 'own')) -ne 'scratch') { throw 'scratch' }
                [Console]::WriteLine('OWN_SCRATCH_OK')
                Denied 'code-write' { [IO.File]::AppendAllText((Join-Path $Code 'probe.ps1'), 'bad') }
                Denied 'code-add' { [IO.File]::WriteAllText((Join-Path $Code 'added.dll'), 'bad') }
                Denied 'namespace-list' { [IO.Directory]::GetDirectories($Runs) }
                Denied 'sibling-read' { [IO.File]::ReadAllText((Join-Path $Sibling 'sentinel')) }
                Denied 'sibling-write' { [IO.File]::WriteAllText((Join-Path $Sibling 'sentinel'), 'bad') }
                Denied 'sibling-add' { [IO.File]::WriteAllText((Join-Path $Sibling 'new'), 'bad') }
                Denied 'sibling-code' { [IO.File]::WriteAllText((Join-Path $Sibling 'worker\added.dll'), 'bad') }
                Denied 'sibling-scratch' { [IO.File]::WriteAllText((Join-Path $Sibling 'scratch\new'), 'bad') }
                Denied 'protected-read' { [IO.File]::ReadAllText((Join-Path $Sensitive 'sentinel')) }
                Denied 'protected-write' { [IO.File]::WriteAllText((Join-Path $Sensitive 'sentinel'), 'bad') }
                """);
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string[] argv = [powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, runs, sibling, sensitive, scratch, code];
            ContainerRequest request;
            if (productionPolicy)
            {
                var settings = new SettingsData { SystemRunFilesystemScope = SystemRunFilesystemScope.UserFilesReadWrite };
                var resources = MxcRequestBuilder.ComposeResources(settings, sensitive, scratch, context,
                    MxcFolderAdmission.ReadMetadata, fixedWorkerDirectory: code);
                request = MxcRequestBuilder.CreateRequest(argv, scratch, settings, resources);
            }
            else request = new(MxcRequestBuilder.RenderArgv(argv))
            {
                Containment = new Containment.ProcessContainer { LearningMode = false },
                Filesystem = new()
                {
                    ReadonlyPaths = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), code],
                    ReadwritePaths = [profile, scratch], DeniedPaths = [runs, sensitive], ClearPolicyOnExit = true,
                },
                Network = new()
                {
                    Egress = new() { Default = NetworkAction.Deny },
                    Ingress = new() { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
                },
                Ui = new() { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
                WorkingDirectory = scratch,
            };
            var probe = MxcContainer.Probe(request);
            Assert.Null(probe.Error);
            Assert.Equal(IsolationTier.BaseContainer, probe.Tier);
            Assert.False(probe.NeedsDaclAugmentation);
            Assert.True(probe.Probes.BaseContainerSupportsDenyPaths);
            Assert.DoesNotContain(Path.GetPathRoot(root)!, request.Filesystem!.ReadonlyPaths);
            using var process = await MxcContainer.SpawnAsync(request,
                new SpawnOptions { Telemetry = new() { Enabled = false } }, CancellationToken.None);
            using var os = Process.GetProcessById((int)process.Id);
            var identity = new
            {
                Pid = process.Id, StartTimeUtc = os.StartTime.ToUniversalTime().ToString("O"), Executable = powershell,
                CommandLine = request.Command, Root = root, Port = (int?)null, productionPolicy, index,
            };
            var name = $"namespace-{(productionPolicy ? "builder" : "sdk")}-{index}";
            File.WriteAllText(Path.Combine(artifacts, name + "-process.json"), JsonSerializer.Serialize(identity));
            using var reader = new StreamReader(process.StandardOutput!);
            var readyRead = Task.Run(reader.ReadLine);
            var stderr = Task.Run(() => new StreamReader(process.StandardError!).ReadToEnd());
            Task<string>? stdout = null;
            try
            {
                Assert.Equal("READY", await readyRead.WaitAsync(TimeSpan.FromSeconds(15)));
                if (Interlocked.Increment(ref readyCount) == count) allReady.SetResult();
                await allReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                using (var input = new StreamWriter(process.StandardInput!)) { input.WriteLine("go"); input.Flush(); }
                stdout = Task.Run(reader.ReadToEnd);
                var result = await process.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
                var output = await stdout;
                var error = await stderr;
                File.WriteAllText(Path.Combine(artifacts, name + "-result.json"),
                    JsonSerializer.Serialize(new { identity, result, output, error, Exited = os.HasExited, ConcurrentProcesses = count }));
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("OWN_SCRATCH_OK", output);
                foreach (var check in new[] { "code-write", "code-add", "namespace-list", "sibling-read", "sibling-write",
                             "sibling-add", "sibling-code", "sibling-scratch", "protected-read", "protected-write" })
                    Assert.Contains("DENIED:" + check, output);
            }
            finally
            {
                if (!os.HasExited) process.Kill();
                await process.WaitAsync(CancellationToken.None);
                await readyRead;
                if (stdout is not null) await stdout;
                await stderr;
            }
        }
    }

    private static string Acl(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }
}

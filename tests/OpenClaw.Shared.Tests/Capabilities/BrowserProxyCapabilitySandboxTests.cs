using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Capabilities;

/// <summary>
/// Tests for the MXC-sandbox routing branch added to <see cref="BrowserProxyCapability"/>.
/// The non-sandbox (inline) path is exercised by the existing
/// <c>BrowserProxyCapabilityTests</c> in <c>CapabilityTests.cs</c>.
/// </summary>
public class BrowserProxyCapabilitySandboxTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static SettingsData Settings(bool flagOn = true) => new()
    {
        BrowserProxySandboxEnabled = flagOn,
    };

    [Fact]
    public async Task ExecuteAsync_FlagOff_RoutesThroughInlineHttp()
    {
        // sandboxEnabled=false ⇒ legacy in-process HttpClient path runs.
        // The fake executor must remain untouched.
        var executor = new FakeBrowserProxyExecutor();
        var handler = new CapturingControlHostHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler,
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: false),
            isSandboxAvailable: () => true,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-flag-off",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/health"}""")
        });

        Assert.True(res.Ok);
        Assert.Null(executor.LastRequest);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxUnavailable_RoutesThroughInlineHttp()
    {
        // Even with the flag ON, when the host availability probe returns
        // false the capability must fall through to in-process — never deny.
        var executor = new FakeBrowserProxyExecutor();
        var handler = new CapturingControlHostHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler,
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => false,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-mxc-unavailable",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/health"}""")
        });

        Assert.True(res.Ok);
        Assert.Null(executor.LastRequest);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxEnabledAndAvailable_RoutesThroughWorker()
    {
        // The fake executor stands in for wxc-exec — when invoked, it reads
        // the req.json the capability staged and writes a resp.json that the
        // capability then unmarshals back to the gateway.
        var executor = new FakeBrowserProxyExecutor(workerResponse: """{"ok":true,"result":{"snapshot":"abc"}}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "secret-token",
            handler: new ShouldNotBeCalledHandler(),
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => true,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-sandbox-happy",
            Command = "browser.proxy",
            Args = Parse("""{"method":"POST","path":"/snapshot","body":{"limit":1}}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(executor.LastRequest);
        Assert.Equal("browser.proxy", executor.LastRequest!.CapabilityCommand);

        // Confirm the policy reflects loopback-only + the resolved control port.
        var network = executor.LastRequest.Policy.Network;
        Assert.NotNull(network);
        Assert.False(network!.AllowOutbound);
        Assert.Equal(18791, network.LoopbackProxyPort); // gateway + 2
    }

    [Fact]
    public async Task ExecuteAsync_WorkerReportsFileRootDenied_SurfacesErrorMessage()
    {
        var executor = new FakeBrowserProxyExecutor(workerResponse:
            """{"ok":false,"errorCode":"FILE_ROOT_DENIED","errorMessage":"path outside allowed file roots: C:\\Users\\me\\.ssh\\id_rsa"}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler: new ShouldNotBeCalledHandler(),
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => true,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-file-root-denied",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/snapshot"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("outside allowed file roots", res.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WorkerUnreachable_TranslatesToReachabilityGuidance()
    {
        var executor = new FakeBrowserProxyExecutor(workerResponse:
            """{"ok":false,"errorCode":"CONTROL_HOST_UNREACHABLE","errorMessage":"connection refused"}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler: new ShouldNotBeCalledHandler(),
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => true,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-unreachable",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/health"}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("127.0.0.1:18791", res.Error);
        Assert.Contains("gateway port + 2", res.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxUnavailableException_FallsBackToInline()
    {
        // The executor reports unavailable mid-call (e.g. wxc-exec deleted at
        // runtime). The capability must fall back to in-process, not deny.
        var executor = new FakeBrowserProxyExecutor { ThrowsUnavailable = true };
        var handler = new CapturingControlHostHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler,
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => true,
            workerExePath: "C:\\fake\\worker.exe",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-runtime-unavailable",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/health"}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(executor.LastRequest);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_NoWorkerExe_RoutesThroughInline()
    {
        // workerExePath null/empty ⇒ sandbox wiring incomplete ⇒ inline path.
        var executor = new FakeBrowserProxyExecutor();
        var handler = new CapturingControlHostHandler("""{"ok":true}""");
        var cap = new BrowserProxyCapability(
            NullLogger.Instance,
            "ws://127.0.0.1:18789",
            "token",
            handler,
            sandboxExecutor: executor,
            settingsProvider: () => Settings(flagOn: true),
            isSandboxAvailable: () => true,
            workerExePath: "",
            allowedFileRoots: new[] { System.IO.Path.GetTempPath() });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "bp-no-worker",
            Command = "browser.proxy",
            Args = Parse("""{"method":"GET","path":"/health"}""")
        });

        Assert.True(res.Ok);
        Assert.Null(executor.LastRequest);
        Assert.NotNull(handler.LastRequest);
    }

    /// <summary>
    /// Fake <see cref="ISandboxExecutor"/> that stands in for wxc-exec in tests.
    /// Reads the request envelope produced by <see cref="BrowserProxyCapability"/>,
    /// writes the configured worker response to the staged resp.json, and
    /// returns success (or throws <see cref="SandboxUnavailableException"/>
    /// when configured).
    /// </summary>
    private sealed class FakeBrowserProxyExecutor : ISandboxExecutor
    {
        private readonly string _workerResponseJson;
        public string Name => "fake-browser-proxy";
        public bool IsContained => true;
        public SandboxExecutionRequest? LastRequest { get; private set; }
        public bool ThrowsUnavailable { get; set; }

        public FakeBrowserProxyExecutor(string workerResponse = """{"ok":true,"result":{}}""")
        {
            _workerResponseJson = workerResponse;
        }

        public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowsUnavailable)
                throw new SandboxUnavailableException("fake unavailable");

            // The capability passes workerExePath / requestFilePath / responseFilePath
            // in Args — we mimic the worker by reading the request and writing the
            // pre-configured response into the staged resp.json.
            if (request.Args.ValueKind == JsonValueKind.Object
                && request.Args.TryGetProperty("responseFilePath", out var respPath)
                && respPath.ValueKind == JsonValueKind.String)
            {
                System.IO.File.WriteAllText(respPath.GetString()!, _workerResponseJson);
            }

            return Task.FromResult(new SandboxExecutionResult(
                ExitCode: 0,
                Stdout: string.Empty,
                Stderr: string.Empty,
                TimedOut: false,
                DurationMs: 1,
                ContainmentTag: "fake-browser-proxy"));
        }
    }

    private sealed class CapturingControlHostHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _status;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingControlHostHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseBody = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ShouldNotBeCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            throw new InvalidOperationException(
                "HttpMessageHandler should not be invoked when the sandbox path is selected.");
        }
    }
}

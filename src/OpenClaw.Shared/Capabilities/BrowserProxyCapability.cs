using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Capabilities;

public class BrowserProxyCapability : NodeCapabilityBase
{
    private const int DefaultTimeoutMs = 20_000;
    private const int MaxTimeoutMs = 120_000;
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private static readonly string[] s_commands = ["browser.proxy"];
    private readonly string _gatewayUrl;
    private readonly string _bearerToken;
    private readonly int? _sshRemoteGatewayPort;
    private readonly HttpClient _httpClient;

    // Optional MXC sandbox plumbing — null when not wired. See TryUseSandbox
    // for the activation matrix.
    private readonly ISandboxExecutor? _sandboxExecutor;
    private readonly Func<SettingsData>? _settingsProvider;
    private readonly Func<bool>? _isSandboxAvailable;
    private readonly Action? _invalidateAvailability;
    private readonly Func<string>? _settingsDirectoryPathProvider;
    private readonly string? _workerExePath;
    private readonly IReadOnlyList<string> _allowedFileRoots;

    public BrowserProxyCapability(
        IOpenClawLogger logger,
        string gatewayUrl,
        string? bearerToken,
        HttpMessageHandler? handler = null,
        int? sshRemoteGatewayPort = null,
        ISandboxExecutor? sandboxExecutor = null,
        Func<SettingsData>? settingsProvider = null,
        Func<bool>? isSandboxAvailable = null,
        Action? invalidateAvailability = null,
        Func<string>? settingsDirectoryPathProvider = null,
        string? workerExePath = null,
        IReadOnlyList<string>? allowedFileRoots = null) : base(logger)
    {
        _gatewayUrl = gatewayUrl;
        _bearerToken = bearerToken ?? "";
        _sshRemoteGatewayPort = sshRemoteGatewayPort;
        _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
        _sandboxExecutor = sandboxExecutor;
        _settingsProvider = settingsProvider;
        _isSandboxAvailable = isSandboxAvailable;
        _invalidateAvailability = invalidateAvailability;
        _settingsDirectoryPathProvider = settingsDirectoryPathProvider;
        _workerExePath = workerExePath;
        _allowedFileRoots = allowedFileRoots ?? Array.Empty<string>();
    }

    public override string Category => "browser";
    public override IReadOnlyList<string> Commands => s_commands;

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        if (!string.Equals(request.Command, "browser.proxy", StringComparison.OrdinalIgnoreCase))
            return Error($"Unknown command: {request.Command}");

        if (!TryResolveControlEndpoint(_gatewayUrl, out var controlPort, out var endpointError))
            return Error(endpointError);

        var method = GetStringArg(request.Args, "method", "GET")?.ToUpperInvariant() ?? "GET";
        if (method is not ("GET" or "POST" or "DELETE"))
            method = "GET";

        var rawPath = GetStringArg(request.Args, "path", "");
        if (!TryNormalizePath(rawPath, out var path, out var pathError))
            return Error(pathError);

        var timeoutMs = Math.Clamp(GetIntArg(request.Args, "timeoutMs", DefaultTimeoutMs), 1, MaxTimeoutMs);

        if (TryUseSandbox(out var settings) && settings is not null)
        {
            return await ExecuteSandboxedAsync(controlPort, method, path, request.Args, timeoutMs, settings);
        }

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

        var uri = BuildUri(controlPort, path, request.Args);
        try
        {
            using var httpRequest = CreateHttpRequest(method, uri, request.Args, usePasswordAuth: false);
            using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
            var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                !string.IsNullOrWhiteSpace(_bearerToken))
            {
                using var passwordRequest = CreateHttpRequest(method, uri, request.Args, usePasswordAuth: true);
                using var passwordResponse = await _httpClient.SendAsync(passwordRequest, timeoutCts.Token);
                var passwordResponseText = await passwordResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                return BuildProxyResponse(passwordResponse, passwordResponseText);
            }

            return BuildProxyResponse(response, responseText);
        }
        catch (TaskCanceledException)
        {
            return Error($"browser proxy timed out for {method} {path} after {timeoutMs}ms. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn($"browser proxy: control host unreachable on 127.0.0.1:{controlPort}: {ex.Message}");
            return Error($"Browser control host is not reachable on 127.0.0.1:{controlPort}. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}");
        }
        catch (JsonException ex)
        {
            Logger.Warn($"browser proxy: control host returned invalid JSON: {ex.Message}");
            return Error("Browser control host returned invalid JSON");
        }
        catch (IOException ex)
        {
            Logger.Warn($"browser proxy: file read failed: {ex.Message}");
            return Error("Browser proxy file read failed");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn($"browser proxy: file read denied: {ex.Message}");
            return Error("Browser proxy file read denied");
        }
    }

    /// <summary>
    /// True when the sandbox is wired, the
    /// <see cref="SettingsData.BrowserProxySandboxEnabled"/> flag is on, and
    /// the host availability probe currently reports MXC available.
    /// </summary>
    private bool TryUseSandbox(out SettingsData? settings)
    {
        settings = null;
        if (_sandboxExecutor is null
            || _settingsProvider is null
            || _isSandboxAvailable is null
            || string.IsNullOrEmpty(_workerExePath))
        {
            return false;
        }

        var snapshot = _settingsProvider();
        if (snapshot is null) return false;

        if (!snapshot.BrowserProxySandboxEnabled)
        {
            Logger.Info("[mxc] browser.proxy sandbox=disabled; routing through in-process path");
            return false;
        }

        if (!_isSandboxAvailable())
        {
            Logger.Warn(
                "[mxc] browser.proxy UNCONTAINED: sandbox unavailable on this host. " +
                "Falling back to in-process execution.");
            return false;
        }

        settings = snapshot;
        return true;
    }

    private async Task<NodeInvokeResponse> ExecuteSandboxedAsync(
        int controlPort,
        string method,
        string path,
        JsonElement args,
        int timeoutMs,
        SettingsData settings)
    {
        // Per-invocation scratch under TEMP. Deleted in finally so secrets in
        // req.json don't outlive the call.
        var scratchDir = Path.Combine(Path.GetTempPath(), "openclaw-browser-proxy-" + Guid.NewGuid().ToString("N")[..12]);
        var requestFile = Path.Combine(scratchDir, "req.json");
        var responseFile = Path.Combine(scratchDir, "resp.json");

        try
        {
            Directory.CreateDirectory(scratchDir);

            var workerRequest = BuildWorkerRequestJson(controlPort, method, path, args, timeoutMs);
            await File.WriteAllTextAsync(requestFile, workerRequest, Encoding.UTF8);

            var settingsDir = _settingsDirectoryPathProvider?.Invoke() ?? "";
            var policy = MxcPolicyBuilder.ForBrowserProxy(settings, settingsDir, controlPort, _allowedFileRoots);

            // Pack worker invocation params into Args for MxcConfigBuilder.
            var mxcArgs = JsonSerializer.SerializeToElement(new
            {
                workerExePath = _workerExePath,
                requestFilePath = requestFile,
                responseFilePath = responseFile,
            });

            var sandboxRequest = new SandboxExecutionRequest(
                CapabilityCommand: "browser.proxy",
                Args: mxcArgs,
                Policy: policy,
                TimeoutMs: timeoutMs + 5_000,
                Cwd: scratchDir);

            try
            {
                var sandboxed = await _sandboxExecutor!.ExecuteAsync(sandboxRequest);
                if (sandboxed.TimedOut)
                    return Error($"browser proxy timed out for {method} {path} after {timeoutMs}ms. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}");

                if (!File.Exists(responseFile))
                {
                    Logger.Warn($"browser proxy worker produced no response file (exit={sandboxed.ExitCode}). stderr={sandboxed.Stderr}");
                    return Error("Browser proxy worker produced no response");
                }

                var responseJson = await File.ReadAllTextAsync(responseFile);
                return BuildSandboxedProxyResponse(responseJson, controlPort);
            }
            catch (SandboxUnavailableException ex)
            {
                _invalidateAvailability?.Invoke();
                Logger.Warn($"[mxc] browser.proxy sandbox unavailable at runtime ({ex.Message}); falling back to in-process for this call");
                return await ExecuteInProcessFallbackAsync(controlPort, method, path, args, timeoutMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warn($"[mxc] browser.proxy sandbox execution failed: {ex.GetType().Name}: {ex.Message}");
                return Error("Browser proxy sandboxed execution failed");
            }
        }
        finally
        {
            try { if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private string BuildWorkerRequestJson(int controlPort, string method, string path, JsonElement args, int timeoutMs)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("query", out var q)
            && q.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in q.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
                if (value != null) query[prop.Name] = value;
            }
        }

        string? profile = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("profile", out var p)
            && p.ValueKind == JsonValueKind.String)
        {
            profile = p.GetString();
        }

        object? body = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("body", out var b))
        {
            body = JsonSerializer.Deserialize<JsonElement>(b.GetRawText());
        }

        var payload = new
        {
            controlPort,
            bearerToken = _bearerToken,
            method,
            path,
            query,
            profile,
            body,
            timeoutMs,
            allowedFileRoots = _allowedFileRoots,
        };
        return JsonSerializer.Serialize(payload);
    }

    private NodeInvokeResponse BuildSandboxedProxyResponse(string responseJson, int controlPort)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(responseJson) ? "{}" : responseJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Logger.Warn($"browser proxy worker returned invalid JSON: {ex.Message}");
            return Error("Browser proxy worker returned invalid JSON");
        }

        bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok)
        {
            var code = root.TryGetProperty("errorCode", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString() : null;
            var message = root.TryGetProperty("errorMessage", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() : null;

            return code switch
            {
                "CONTROL_HOST_UNREACHABLE" =>
                    Error($"Browser control host is not reachable on 127.0.0.1:{controlPort}. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}"),
                "TIMEOUT" =>
                    Error($"browser proxy timed out. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}"),
                "AUTH_FAILED" =>
                    Error(BuildAuthenticationFailureGuidance()),
                "INVALID_JSON" =>
                    Error("Browser control host returned invalid JSON"),
                "FILE_ROOT_DENIED" =>
                    Error(message ?? "Browser proxy file path outside allowed roots"),
                "FILE_TOO_LARGE" =>
                    Error(message ?? "Browser proxy file too large"),
                "FILE_READ_DENIED" =>
                    Error("Browser proxy file read denied"),
                "FILE_READ_FAILED" =>
                    Error("Browser proxy file read failed"),
                "HTTP_ERROR" =>
                    Error(message ?? "Browser control host returned an HTTP error"),
                _ => Error(message ?? "Browser proxy worker reported an error"),
            };
        }

        var result = root.TryGetProperty("result", out var resultEl)
            ? resultEl
            : JsonDocument.Parse("{}").RootElement;

        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            return Success(new { result, files = filesEl });

        return Success(new { result });
    }

    private async Task<NodeInvokeResponse> ExecuteInProcessFallbackAsync(
        int controlPort, string method, string path, JsonElement args, int timeoutMs)
    {
        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var uri = BuildUri(controlPort, path, args);
        try
        {
            using var httpRequest = CreateHttpRequest(method, uri, args, usePasswordAuth: false);
            using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
            var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(_bearerToken))
            {
                using var passwordRequest = CreateHttpRequest(method, uri, args, usePasswordAuth: true);
                using var passwordResponse = await _httpClient.SendAsync(passwordRequest, timeoutCts.Token);
                var passwordResponseText = await passwordResponse.Content.ReadAsStringAsync(timeoutCts.Token);
                return BuildProxyResponse(passwordResponse, passwordResponseText);
            }

            return BuildProxyResponse(response, responseText);
        }
        catch (TaskCanceledException)
        {
            return Error($"browser proxy timed out for {method} {path} after {timeoutMs}ms. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn($"browser proxy: control host unreachable on 127.0.0.1:{controlPort}: {ex.Message}");
            return Error($"Browser control host is not reachable on 127.0.0.1:{controlPort}. {BuildReachabilityGuidance(controlPort, _sshRemoteGatewayPort)}");
        }
    }

    private HttpRequestMessage CreateHttpRequest(string method, Uri uri, JsonElement args, bool usePasswordAuth)
    {
        var httpRequest = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrWhiteSpace(_bearerToken))
        {
            if (usePasswordAuth)
            {
                httpRequest.Headers.TryAddWithoutValidation("x-openclaw-password", _bearerToken);
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($":{_bearerToken}")));
            }
            else
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
            }
        }

        if (method is "POST" or "DELETE" &&
            args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("body", out var body))
        {
            httpRequest.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        return httpRequest;
    }

    private NodeInvokeResponse BuildProxyResponse(HttpResponseMessage response, string responseText)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return Error(BuildAuthenticationFailureGuidance());
        if (!response.IsSuccessStatusCode)
            return Error(string.IsNullOrWhiteSpace(responseText) ? $"Browser control host returned HTTP {(int)response.StatusCode}" : responseText);

        using var doc = string.IsNullOrWhiteSpace(responseText)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(responseText);
        var result = doc.RootElement.Clone();
        var files = TryCollectFiles(result);

        return files.Count == 0
            ? Success(new { result })
            : Success(new { result, files });
    }

    private string BuildAuthenticationFailureGuidance()
    {
        return string.IsNullOrWhiteSpace(_bearerToken)
            ? "Browser control host rejected the unauthenticated request. Windows has no gateway shared token saved for browser-control auth; enter the matching gateway token in Settings or run the browser-control host with compatible auth."
            : "Browser control host rejected authentication. Verify the gateway token saved in Settings matches the browser-control host auth token or password.";
    }

    private static bool TryResolveControlEndpoint(string gatewayUrl, out int controlPort, out string error)
    {
        controlPort = 0;
        error = "";
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gatewayUri) || gatewayUri.Port <= 0)
        {
            error = "Browser proxy requires a gateway URL with an explicit local port.";
            return false;
        }

        controlPort = gatewayUri.Port + 2;
        if (controlPort > 65535)
        {
            error = "Browser proxy control port is outside the valid TCP port range.";
            return false;
        }

        return true;
    }

    private static string BuildReachabilityGuidance(int localControlPort, int? sshRemoteGatewayPort)
    {
        var sshForward = sshRemoteGatewayPort is >= 1 and <= 65533
            ? $"ssh -N -L {localControlPort}:127.0.0.1:{sshRemoteGatewayPort.Value + 2} <user>@<host>"
            : $"ssh -N -L {localControlPort}:127.0.0.1:<remote-gateway-port+2> <user>@<host>";

        return $"Start the local OpenClaw browser control host on gateway port + 2 ({localControlPort}). If the gateway is reached through SSH, also forward the browser-control port with: {sshForward}";
    }

    private static bool TryNormalizePath(string? rawPath, out string path, out string error)
    {
        path = "";
        error = "";
        var candidate = rawPath?.Trim() ?? "";
        if (candidate.Length == 0)
        {
            error = "INVALID_REQUEST: path required";
            return false;
        }

        if (candidate.Contains("://", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            error = "INVALID_REQUEST: browser.proxy path must be a local control path, not a URL";
            return false;
        }

        path = candidate.StartsWith("/", StringComparison.Ordinal) ? candidate : "/" + candidate;
        return true;
    }

    private static Uri BuildUri(int controlPort, string path, JsonElement args)
    {
        var builder = new UriBuilder("http", "127.0.0.1", controlPort, path);
        var query = new List<string>();
        if (args.ValueKind != JsonValueKind.Object)
            return builder.Uri;

        if (args.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in queryElement.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
                if (value != null)
                    query.Add($"{Uri.EscapeDataString(prop.Name)}={Uri.EscapeDataString(value)}");
            }
        }

        if (args.TryGetProperty("profile", out var profileElement) &&
            profileElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(profileElement.GetString()))
        {
            query.Add($"profile={Uri.EscapeDataString(profileElement.GetString()!)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static List<object> TryCollectFiles(JsonElement result)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPath(result, "path", paths);
        CollectPath(result, "imagePath", paths);
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("download", out var download) &&
            download.ValueKind == JsonValueKind.Object)
        {
            CollectPath(download, "path", paths);
        }

        var files = new List<object>();
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                continue;
            if (info.Length > MaxFileBytes)
                throw new IOException($"browser proxy file exceeds {MaxFileBytes / (1024 * 1024)}MB: {path}");

            var bytes = File.ReadAllBytes(path);
            files.Add(new
            {
                path,
                base64 = Convert.ToBase64String(bytes),
                mimeType = GuessMimeType(path)
            });
        }

        return files;
    }

    private static void CollectPath(JsonElement source, string propertyName, HashSet<string> paths)
    {
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var path = value.GetString();
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }

    private static string? GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".html" or ".htm" => "text/html",
            _ => null
        };
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenClaw.BrowserProxy.Worker;

/// <summary>
/// MXC-sandboxed worker that performs a single browser.proxy HTTP call.
/// Spawned by <c>OpenClaw.Shared.Capabilities.BrowserProxyCapability</c> via
/// <c>wxc-exec.exe</c> with a tight loopback + file-roots policy.
/// </summary>
/// <remarks>
/// Contract:
/// <list type="bullet">
/// <item>Reads <c>req.json</c> (path passed via <c>--request</c>).</item>
/// <item>Calls <c>http://127.0.0.1:&lt;controlPort&gt;&lt;path&gt;?&lt;query&gt;</c> with optional bearer/password auth.</item>
/// <item>Walks the response JSON for <c>path</c>, <c>imagePath</c>, <c>download.path</c> fields; if any are present, base64-encodes the referenced files — but only if the path is under one of the <c>allowedFileRoots</c> declared in the request.</item>
/// <item>Writes <c>resp.json</c> (path passed via <c>--response</c>) with structured success / error fields.</item>
/// </list>
/// Exit codes:
/// <list type="bullet">
/// <item>0 — wrote a response file (which itself may indicate <c>ok:false</c>).</item>
/// <item>1 — invalid CLI invocation (missing/invalid flags).</item>
/// <item>2 — could not read or parse <c>req.json</c>.</item>
/// </list>
/// All other failures (HTTP, auth, file-root violation, oversize file) are
/// reported via <c>resp.json</c> with <c>ok:false</c> and a stable
/// <c>errorCode</c> string so the host capability can translate cleanly.
/// </remarks>
internal static class Program
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int FallbackTimeoutMs = 20_000;

    internal static async Task<int> Main(string[] args)
    {
        if (!TryParseCli(args, out var requestPath, out var responsePath, out var cliError))
        {
            await Console.Error.WriteLineAsync(cliError);
            return 1;
        }

        WorkerRequest? request;
        try
        {
            var json = await File.ReadAllTextAsync(requestPath);
            request = JsonSerializer.Deserialize<WorkerRequest>(json, JsonOpts);
            if (request is null) throw new InvalidDataException("request file deserialized to null");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"failed to read request file: {ex.Message}");
            return 2;
        }

        var response = await ExecuteAsync(request);
        try
        {
            await File.WriteAllTextAsync(
                responsePath,
                JsonSerializer.Serialize(response, JsonOpts),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"failed to write response file: {ex.Message}");
            return 2;
        }

        return 0;
    }

    internal static async Task<WorkerResponse> ExecuteAsync(WorkerRequest request)
    {
        if (request.ControlPort <= 0 || request.ControlPort > 65535)
            return Error("INVALID_REQUEST", "controlPort out of range");
        if (string.IsNullOrWhiteSpace(request.Method))
            return Error("INVALID_REQUEST", "method required");
        if (string.IsNullOrWhiteSpace(request.Path))
            return Error("INVALID_REQUEST", "path required");

        var method = request.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "DELETE"))
            method = "GET";

        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : FallbackTimeoutMs;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        using var http = new HttpClient();

        var uri = BuildUri(request);

        try
        {
            using var firstReq = BuildHttpRequest(method, uri, request, passwordAuth: false);
            using var firstResp = await http.SendAsync(firstReq, cts.Token);
            var firstBody = await firstResp.Content.ReadAsStringAsync(cts.Token);

            if (firstResp.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(request.BearerToken))
            {
                using var secondReq = BuildHttpRequest(method, uri, request, passwordAuth: true);
                using var secondResp = await http.SendAsync(secondReq, cts.Token);
                var secondBody = await secondResp.Content.ReadAsStringAsync(cts.Token);
                return BuildResponse(secondResp, secondBody, request.AllowedFileRoots ?? new List<string>());
            }

            return BuildResponse(firstResp, firstBody, request.AllowedFileRoots ?? new List<string>());
        }
        catch (TaskCanceledException)
        {
            return Error("TIMEOUT", $"browser proxy timed out after {timeoutMs}ms");
        }
        catch (HttpRequestException ex)
        {
            return Error("CONTROL_HOST_UNREACHABLE", $"control host unreachable on 127.0.0.1:{request.ControlPort}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return Error("INVALID_JSON", $"control host returned invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Error("FILE_READ_FAILED", $"file read failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error("FILE_READ_DENIED", $"file read denied: {ex.Message}");
        }
    }

    private static HttpRequestMessage BuildHttpRequest(
        string method, Uri uri, WorkerRequest request, bool passwordAuth)
    {
        var msg = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrEmpty(request.BearerToken))
        {
            if (passwordAuth)
            {
                msg.Headers.TryAddWithoutValidation("x-openclaw-password", request.BearerToken);
                msg.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($":{request.BearerToken}")));
            }
            else
            {
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);
            }
        }

        if (method is "POST" or "DELETE" && request.Body.HasValue)
        {
            var bodyText = request.Body.Value.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : request.Body.Value.GetRawText();
            msg.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
        }

        return msg;
    }

    private static Uri BuildUri(WorkerRequest request)
    {
        var path = request.Path!.StartsWith('/') ? request.Path : "/" + request.Path;
        var builder = new UriBuilder("http", "127.0.0.1", request.ControlPort, path);

        var pairs = new List<string>();
        if (request.Query is { Count: > 0 })
        {
            foreach (var kvp in request.Query)
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value is null) continue;
                pairs.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.Profile))
            pairs.Add($"profile={Uri.EscapeDataString(request.Profile)}");

        builder.Query = string.Join("&", pairs);
        return builder.Uri;
    }

    private static WorkerResponse BuildResponse(HttpResponseMessage response, string body, IReadOnlyList<string> allowedFileRoots)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return Error("AUTH_FAILED", "control host rejected authentication");
        if (!response.IsSuccessStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)response.StatusCode}" : body;
            return Error("HTTP_ERROR", detail, (int)response.StatusCode);
        }

        JsonElement result;
        try
        {
            using var doc = string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(body);
            result = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Error("INVALID_JSON", $"control host returned invalid JSON: {ex.Message}");
        }

        var files = TryCollectFiles(result, allowedFileRoots, out var fileError);
        if (fileError is not null)
            return Error(fileError.Code, fileError.Message);

        return new WorkerResponse
        {
            Ok = true,
            Result = result,
            Files = files.Count == 0 ? null : files,
        };
    }

    internal static List<WorkerFile> TryCollectFiles(JsonElement result, IReadOnlyList<string> allowedFileRoots, out WorkerError? error)
    {
        error = null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPath(result, "path", paths);
        CollectPath(result, "imagePath", paths);
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("download", out var download)
            && download.ValueKind == JsonValueKind.Object)
        {
            CollectPath(download, "path", paths);
        }

        var files = new List<WorkerFile>();
        foreach (var path in paths)
        {
            if (!IsUnderAllowedRoot(path, allowedFileRoots))
            {
                error = new WorkerError("FILE_ROOT_DENIED", $"path outside allowed file roots: {path}");
                return files;
            }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex)
            {
                error = new WorkerError("FILE_READ_FAILED", $"FileInfo failed for {path}: {ex.Message}");
                return files;
            }
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                continue;
            if (info.Length > MaxFileBytes)
            {
                error = new WorkerError("FILE_TOO_LARGE", $"file exceeds {MaxFileBytes / (1024 * 1024)}MB: {path}");
                return files;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (UnauthorizedAccessException ex)
            {
                error = new WorkerError("FILE_READ_DENIED", $"file read denied: {ex.Message}");
                return files;
            }
            catch (IOException ex)
            {
                error = new WorkerError("FILE_READ_FAILED", $"file read failed: {ex.Message}");
                return files;
            }

            files.Add(new WorkerFile
            {
                Path = path,
                Base64 = Convert.ToBase64String(bytes),
                MimeType = GuessMimeType(path),
            });
        }

        return files;
    }

    private static void CollectPath(JsonElement source, string name, HashSet<string> paths)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        if (!source.TryGetProperty(name, out var value)) return;
        if (value.ValueKind != JsonValueKind.String) return;
        var path = value.GetString();
        if (!string.IsNullOrWhiteSpace(path))
            paths.Add(path);
    }

    internal static bool IsUnderAllowedRoot(string path, IReadOnlyList<string> allowedRoots)
    {
        if (allowedRoots.Count == 0) return false;
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return false; }
        var normalized = Path.TrimEndingDirectorySeparator(fullPath);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string normalizedRoot;
            try { normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch { continue; }
            if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".html" or ".htm" => "text/html",
        _ => null,
    };

    private static WorkerResponse Error(string code, string message, int? httpStatus = null) => new()
    {
        Ok = false,
        ErrorCode = code,
        ErrorMessage = message,
        HttpStatus = httpStatus,
    };

    private static bool TryParseCli(
        string[] args, out string requestPath, out string responsePath, out string error)
    {
        requestPath = "";
        responsePath = "";
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--request":
                    if (++i >= args.Length) { error = "--request requires a path"; return false; }
                    requestPath = args[i];
                    break;
                case "--response":
                    if (++i >= args.Length) { error = "--response requires a path"; return false; }
                    responsePath = args[i];
                    break;
                default:
                    error = $"unknown arg: {args[i]}";
                    return false;
            }
        }
        if (string.IsNullOrWhiteSpace(requestPath)) { error = "missing --request"; return false; }
        if (string.IsNullOrWhiteSpace(responsePath)) { error = "missing --response"; return false; }
        return true;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

internal sealed class WorkerRequest
{
    public int ControlPort { get; set; }
    public string? BearerToken { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Query { get; set; }
    public string? Profile { get; set; }
    public JsonElement? Body { get; set; }
    public int TimeoutMs { get; set; }
    public List<string>? AllowedFileRoots { get; set; }
}

internal sealed class WorkerResponse
{
    public bool Ok { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int? HttpStatus { get; set; }
    public JsonElement? Result { get; set; }
    public List<WorkerFile>? Files { get; set; }
}

internal sealed class WorkerFile
{
    public string Path { get; set; } = "";
    public string Base64 { get; set; } = "";
    public string? MimeType { get; set; }
}

internal sealed record WorkerError(string Code, string Message);

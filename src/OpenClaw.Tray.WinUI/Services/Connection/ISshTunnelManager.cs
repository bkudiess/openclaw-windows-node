using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Manages an SSH tunnel lifecycle for a gateway connection.
/// Wraps the existing SshTunnelService behind a clean interface.
/// </summary>
public interface ISshTunnelManager : IDisposable
{
    bool IsActive { get; }
    Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct);
    Task StopAsync();
    string? LocalTunnelUrl { get; }
}

/// <summary>
/// Implementation wrapping the existing SshTunnelService.
/// </summary>
public sealed class SshTunnelManager : ISshTunnelManager
{
    private readonly OpenClawTray.Services.SshTunnelService _service;
    private readonly IOpenClawLogger _logger;

    public SshTunnelManager(OpenClawTray.Services.SshTunnelService service, IOpenClawLogger logger)
    {
        _service = service;
        _logger = logger;
    }

    public bool IsActive => _service.IsRunning;
    public string? LocalTunnelUrl => IsActive ? $"ws://localhost:{_service.CurrentLocalPort}" : null;

    public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
    {
        _logger.Info($"[SshTunnelMgr] Starting tunnel {config.User}@{config.Host}:{config.RemotePort} → localhost:{config.LocalPort}");
        _service.EnsureStarted(config.User, config.Host, config.RemotePort, config.LocalPort);
        var localUrl = $"ws://localhost:{config.LocalPort}";
        _logger.Info($"[SshTunnelMgr] Tunnel started, local URL: {localUrl}");
        return Task.FromResult(localUrl);
    }

    public Task StopAsync()
    {
        _service.Stop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // SshTunnelService lifecycle is managed by App — don't dispose here
    }
}

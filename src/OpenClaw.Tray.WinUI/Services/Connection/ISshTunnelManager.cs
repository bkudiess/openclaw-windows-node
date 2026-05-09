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
        _logger.Info($"[SshTunnelMgr] Starting tunnel {config.User}@{config.Host}:{config.RemotePort}");
        // SshTunnelService.EnsureStarted is synchronous — wrap
        // Note: the actual tunnel is managed by the existing SshTunnelService via SettingsManager
        // Full integration will come when the manager takes over tunnel lifecycle from App
        return Task.FromResult($"ws://localhost:{config.LocalPort}");
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

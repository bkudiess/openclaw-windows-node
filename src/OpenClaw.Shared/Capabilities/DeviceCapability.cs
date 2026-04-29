using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Device metadata and lightweight health/status capability.
/// </summary>
public class DeviceCapability : NodeCapabilityBase
{
    public override string Category => "device";

    private static readonly string[] _commands =
    [
        "device.info",
        "device.status"
    ];

    public override IReadOnlyList<string> Commands => _commands;

    public DeviceCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return Task.FromResult(request.Command switch
        {
            "device.info" => HandleInfo(),
            "device.status" => HandleStatus(request),
            _ => Error($"Unknown command: {request.Command}")
        });
    }

    private NodeInvokeResponse HandleInfo()
    {
        Logger.Info("device.info");

        var assembly = typeof(DeviceCapability).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        return Success(new
        {
            deviceName = Environment.MachineName,
            modelIdentifier = GetModelIdentifier(),
            systemName = OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            systemVersion = RuntimeInformation.OSDescription,
            appVersion = version,
            appBuild = assembly.GetName().Version?.ToString() ?? version,
            locale = CultureInfo.CurrentCulture.Name
        });
    }

    private NodeInvokeResponse HandleStatus(NodeInvokeRequest request)
    {
        Logger.Info("device.status");

        var requestedSections = GetStringArrayArg(request.Args, "sections");
        bool WantSection(string name) => requestedSections.Length == 0 || requestedSections.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (requestedSections.Length > 0)
        {
            var validSections = new[] { "os", "cpu", "memory", "disk", "battery" };
            var unknown = requestedSections.Except(validSections, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknown.Length > 0)
            {
                return Error($"Unknown section(s): {string.Join(", ", unknown)}. Valid values: {string.Join(", ", validSections)}");
            }
        }

        var storage = GetStorageStatus(Logger);
        var network = GetNetworkStatus(Logger);

        return Success(new
        {
            collectedAt = DateTime.UtcNow.ToString("O"),
            os = WantSection("os") ? (object)GetOsInfo() : null,
            cpu = WantSection("cpu") ? (object)GetCpuInfo() : null,
            memory = WantSection("memory") ? (object)GetMemoryInfo(Logger) : null,
            disk = WantSection("disk") ? (object)new { drives = GetDriveInfo(Logger) } : null,
            battery = WantSection("battery") ? (object)new
            {
                level = (double?)null,
                state = "unknown",
                lowPowerModeEnabled = false
            } : null,
            // Preserve legacy fields for backward compatibility.
            thermal = new { state = "nominal" },
            storage,
            network,
            uptimeSeconds = Environment.TickCount64 / 1000.0
        });
    }

    private static object GetOsInfo() => new
    {
        version = Environment.OSVersion.Version.ToString(),
        architecture = RuntimeInformation.OSArchitecture.ToString(),
        machineName = Environment.MachineName,
        uptimeSeconds = (long)(Environment.TickCount64 / 1000L)
    };

    private static object GetCpuInfo() => new
    {
        name = GetModelIdentifier(),
        logicalProcessors = Environment.ProcessorCount,
        usagePercent = (double?)null
    };

    private static object GetMemoryInfo(IOpenClawLogger logger)
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            var total = info.TotalAvailableMemoryBytes;
            if (total > 0)
            {
                var used = info.MemoryLoadBytes;
                var available = Math.Max(0, total - used);
                return new
                {
                    totalBytes = total,
                    availableBytes = available,
                    usagePercent = Math.Round((double)used / total * 100, 1)
                };
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: memory info unavailable: {ex.Message}");
        }

        return new { totalBytes = 0L, availableBytes = 0L, usagePercent = (double?)null };
    }

    private static object[] GetDriveInfo(IOpenClawLogger logger)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d =>
                {
                    try
                    {
                        var total = d.TotalSize;
                        var free = d.AvailableFreeSpace;
                        return (object)new
                        {
                            name = d.RootDirectory.FullName,
                            label = d.VolumeLabel,
                            totalBytes = total,
                            freeBytes = free,
                            usagePercent = total > 0 ? Math.Round((double)(total - free) / total * 100, 1) : (double?)null,
                            format = d.DriveFormat
                        };
                    }
                    catch
                    {
                        return (object)new
                        {
                            name = d.RootDirectory.FullName,
                            label = string.Empty,
                            totalBytes = 0L,
                            freeBytes = 0L,
                            usagePercent = (double?)null,
                            format = string.Empty
                        };
                    }
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: drive enumeration failed: {ex.Message}");
            return [];
        }
    }

    private static string GetModelIdentifier()
    {
        var processorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(processorIdentifier))
        {
            return processorIdentifier;
        }

        return $"{RuntimeInformation.OSArchitecture}".ToLowerInvariant();
    }

    private static object GetStorageStatus(IOpenClawLogger logger)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                ?? Path.GetPathRoot(AppContext.BaseDirectory)
                ?? string.Empty;
            var drive = !string.IsNullOrWhiteSpace(root)
                ? new DriveInfo(root)
                : DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);

            if (drive is { IsReady: true })
            {
                var totalBytes = drive.TotalSize;
                var freeBytes = drive.AvailableFreeSpace;
                return new
                {
                    totalBytes,
                    freeBytes,
                    usedBytes = Math.Max(0, totalBytes - freeBytes)
                };
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: storage status unavailable: {ex.Message}");
        }

        return new
        {
            totalBytes = 0L,
            freeBytes = 0L,
            usedBytes = 0L
        };
    }

    private static object GetNetworkStatus(IOpenClawLogger logger)
    {
        var interfaces = Array.Empty<string>();
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Select(MapInterfaceType)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: network interfaces unavailable: {ex.Message}");
        }

        var isAvailable = false;
        try
        {
            isAvailable = NetworkInterface.GetIsNetworkAvailable();
        }
        catch (Exception ex)
        {
            logger.Warn($"device.status: network availability unavailable: {ex.Message}");
        }

        return new
        {
            status = isAvailable ? "satisfied" : "unsatisfied",
            isExpensive = false,
            isConstrained = false,
            interfaces
        };
    }

    private static string MapInterfaceType(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "wifi",
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT => "wired",
            NetworkInterfaceType.Ppp
                or NetworkInterfaceType.Wwanpp
                or NetworkInterfaceType.Wwanpp2 => "cellular",
            _ => "other"
        };
    }
}

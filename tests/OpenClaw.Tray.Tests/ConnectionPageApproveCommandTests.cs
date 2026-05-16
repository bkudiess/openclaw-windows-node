using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the CLI approve commands emitted by <c>ConnectionPagePlan</c>. The
/// OpenClaw CLI registers approve as a noun-first subcommand
/// (<c>openclaw nodes approve &lt;requestId&gt;</c> and
/// <c>openclaw devices approve &lt;requestId&gt;</c>). The previous verb-first
/// strings (<c>openclaw approve node …</c>) silently failed when users
/// pasted them on the gateway host, so this test guards against regressing
/// back to that broken form.
///
/// Source-parsed for the same reason as <c>FluentIconCatalogTests</c>:
/// <c>OpenClaw.Tray.Tests</c> is a pure net10.0 project that does not
/// reference the WinUI tray assembly.
/// </summary>
public sealed class ConnectionPageApproveCommandTests
{
    private static string ReadPlanSource()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPagePlan.cs");
        return File.ReadAllText(path);
    }

    private static string GetRepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    [Fact]
    public void NodeApproveCommand_UsesNounFirstSubcommand()
    {
        var src = ReadPlanSource();
        // The active emission must be the noun-first form. Asserting the
        // exact interpolated literal pins both the command path and the
        // placement of the request id.
        Assert.Contains("$\"openclaw nodes approve {reqId}\"", src);
        // And the legacy broken form must not return as an *emitted* string —
        // we check for the interpolated/quoted variants, not the bare phrase,
        // because the explanatory comment also names the legacy command.
        Assert.DoesNotContain("$\"openclaw approve node {reqId}\"", src);
        Assert.DoesNotContain("\"openclaw approve node\"", src);
    }

    [Fact]
    public void DevicesApproveCommand_UsesNounFirstSubcommand()
    {
        var src = ReadPlanSource();
        Assert.Contains("$\"openclaw devices approve {reqId}\"", src);
        Assert.DoesNotContain("$\"openclaw approve device {reqId}\"", src);
        Assert.DoesNotContain("\"openclaw approve device\"", src);
    }

    [Fact]
    public void MissingRequestId_EmitsDiscoveryHint_NotBareApprove()
    {
        // CLI's approve subcommands require a <requestId> positional. A bare
        // `openclaw nodes approve` / `openclaw devices approve` exits
        // non-zero with "missing required argument", so the user-facing
        // fallback must lead with a discovery command instead.
        var src = ReadPlanSource();
        Assert.Contains("openclaw nodes pending", src);
        Assert.Contains("openclaw devices list", src);
        // Defense in depth: assert there's no bare end-of-string approve
        // literal (which would be the broken legacy fallback).
        Assert.DoesNotContain("\"openclaw nodes approve\"", src);
        Assert.DoesNotContain("\"openclaw devices approve\"", src);
    }
}


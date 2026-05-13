using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins that the STT card controls in PrivacyAndSecurityPage.xaml are localized
/// (have an x:Uid) and that en-us\Resources.resw provides matching keys.
///
/// LocalizationValidationTests catches drift between locales, but does not
/// catch the case where a developer adds a control with hardcoded English
/// text and never registers it. This test closes that hole for the
/// privacy-sensitive microphone surface (deep-link to VoiceSettingsPage).
///
/// resw key prefixes remain "CapabilitiesPage_*" because they are arbitrary
/// identifiers tied to translated values across five locales — renaming them
/// would require coordinating every translation file with no behavior gain.
/// </summary>
public sealed class PrivacyAndSecurityPageLocalizationCoverageTests
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string GetPageXamlPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "PrivacyAndSecurityPage.xaml");

    private static string GetEnUsReswPath() =>
        Path.Combine(GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

    private static HashSet<string> LoadReswKeys()
    {
        var doc = XDocument.Load(GetEnUsReswPath());
        return doc.Descendants("data")
            .Select(e => e.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> LoadXamlUids()
    {
        var doc = XDocument.Load(GetPageXamlPath());
        return doc.Descendants()
            .Select(e => e.Attribute(XNs + "Uid")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Contract for the STT surface on the Privacy &amp; security page. The TTS card
    /// previously lived here but moved to the Voice &amp; Audio page; its
    /// localization is verified by that page's own tests.
    /// </summary>
    public static IEnumerable<object[]> SttAndTtsCardUids => new[]
    {
        // STT card (deep-link to dedicated voice settings)
        new object[] { "CapabilitiesPage_SttCardHeader",        new[] { ".Text" } },
        new object[] { "CapabilitiesPage_SttCardDescription",   new[] { ".Text" } },
        new object[] { "CapabilitiesPage_SttMoreSettingsLink",  new[] { ".Content" } },
    };

    [Theory]
    [MemberData(nameof(SttAndTtsCardUids))]
    public void SttOrTtsControl_HasXUid_InPrivacyAndSecurityPageXaml(string uid, string[] _)
    {
        var uids = LoadXamlUids();
        Assert.Contains(uid, uids);
    }

    [Theory]
    [MemberData(nameof(SttAndTtsCardUids))]
    public void SttOrTtsControl_AllExpectedReswKeys_ExistInEnUs(string uid, string[] suffixes)
    {
        var keys = LoadReswKeys();
        var missing = suffixes
            .Select(suffix => uid + suffix)
            .Where(key => !keys.Contains(key))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Missing en-us resw keys for x:Uid '{uid}': {string.Join(", ", missing)}");
    }
}

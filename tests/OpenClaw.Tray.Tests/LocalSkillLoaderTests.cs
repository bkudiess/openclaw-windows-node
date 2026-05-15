using System;
using System.IO;
using System.Linq;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class LocalSkillLoaderTests : IDisposable
{
    private readonly string _scratchRoot;

    public LocalSkillLoaderTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(), "openclaw-local-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchRoot))
        {
            try { Directory.Delete(_scratchRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string WriteSkill(string name, string contents)
    {
        var dir = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), contents);
        return dir;
    }

    [Fact]
    public void LoadFromDir_ReturnsEmpty_WhenDirectoryMissing()
    {
        var result = LocalSkillLoader.LoadFromDir(Path.Combine(_scratchRoot, "does-not-exist"));
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromDir_ReturnsEmpty_WhenDirectoryHasNoSkillMd()
    {
        var dir = Path.Combine(_scratchRoot, "empty");
        Directory.CreateDirectory(dir);
        var result = LocalSkillLoader.LoadFromDir(_scratchRoot);
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromDir_ParsesFlatFrontmatter()
    {
        WriteSkill("test-skill", """
---
name: test-skill
description: Test description with quotes and commas, fine.
---

# Body
""");
        var result = LocalSkillLoader.LoadFromDir(_scratchRoot);
        var skill = Assert.Single(result);
        Assert.Equal("test-skill", skill.Id);
        Assert.Equal("test-skill", skill.Name);
        Assert.Equal("Test description with quotes and commas, fine.", skill.Description);
        Assert.Equal("", skill.Emoji);
        Assert.Empty(skill.Os);
    }

    [Fact]
    public void LoadFromDir_ParsesNestedMetadata()
    {
        WriteSkill("test-skill", """
---
name: test-skill
description: A test.
metadata:
  openclaw:
    emoji: "🧪"
    os: ["win32"]
    requires:
      bins: ["pwsh"]
---

# Body
""");
        var skill = Assert.Single(LocalSkillLoader.LoadFromDir(_scratchRoot));
        Assert.Equal("🧪", skill.Emoji);
        Assert.Equal(new[] { "win32" }, skill.Os.ToArray());
    }

    [Fact]
    public void LoadFromDir_ParsesMultipleOsValues()
    {
        WriteSkill("xplat", """
---
name: xplat
description: Multi-OS.
metadata:
  openclaw:
    os: ["darwin", "linux", "win32"]
---
""");
        var skill = Assert.Single(LocalSkillLoader.LoadFromDir(_scratchRoot));
        Assert.Equal(new[] { "darwin", "linux", "win32" }, skill.Os.ToArray());
    }

    [Fact]
    public void LoadFromDir_HandlesSingleQuotedValues()
    {
        WriteSkill("single-quoted", """
---
name: 'single-quoted'
description: 'Single-quoted description'
metadata:
  openclaw:
    emoji: '🔥'
---
""");
        var skill = Assert.Single(LocalSkillLoader.LoadFromDir(_scratchRoot));
        Assert.Equal("single-quoted", skill.Name);
        Assert.Equal("Single-quoted description", skill.Description);
        Assert.Equal("🔥", skill.Emoji);
    }

    [Fact]
    public void LoadFromDir_FallsBackToFolderName_WhenNameMissing()
    {
        WriteSkill("foldername-fallback", """
---
description: Only description
---
""");
        var skill = Assert.Single(LocalSkillLoader.LoadFromDir(_scratchRoot));
        Assert.Equal("foldername-fallback", skill.Id);
        Assert.Equal("foldername-fallback", skill.Name);
    }

    [Fact]
    public void LoadFromDir_SkipsDirectoriesWithoutSkillMd()
    {
        // Create one valid skill plus one bare directory.
        WriteSkill("valid", """
---
name: valid
description: ok
---
""");
        Directory.CreateDirectory(Path.Combine(_scratchRoot, "no-md"));
        File.WriteAllText(Path.Combine(_scratchRoot, "no-md", "README.md"), "not a skill");

        var result = LocalSkillLoader.LoadFromDir(_scratchRoot);
        Assert.Single(result);
        Assert.Equal("valid", result[0].Id);
    }

    [Fact]
    public void LoadFromDir_DoesNotThrow_OnMalformedYaml()
    {
        WriteSkill("malformed", "no frontmatter at all\n");
        WriteSkill("missing-closing", """
---
name: missing-closing
description: never closes the frontmatter
""");
        var result = LocalSkillLoader.LoadFromDir(_scratchRoot);
        // Both should be skipped without throwing.
        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromDir_LoadsRealCatalogSkill_Shape()
    {
        // Verifies the parser handles a real-world catalog SKILL.md frontmatter shape.
        WriteSkill("windows-clipboard", """
---
name: windows-clipboard
description: Read and write the Windows clipboard from PowerShell — plain text, images, and file drop lists.
metadata:
  openclaw:
    emoji: "📋"
    os: ["win32"]
    requires:
      bins: ["pwsh"]
---

# Windows Clipboard

Body content here.
""");
        var skill = Assert.Single(LocalSkillLoader.LoadFromDir(_scratchRoot));
        Assert.Equal("windows-clipboard", skill.Id);
        Assert.Equal("windows-clipboard", skill.Name);
        Assert.StartsWith("Read and write the Windows clipboard", skill.Description);
        Assert.Equal("📋", skill.Emoji);
        Assert.Equal(new[] { "win32" }, skill.Os.ToArray());
    }
}

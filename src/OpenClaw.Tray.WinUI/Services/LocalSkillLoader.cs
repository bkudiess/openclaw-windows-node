using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OpenClawTray.Services;

namespace OpenClawTray.Services;

/// <summary>
/// Loads skills that ship inside the Windows node itself (under <c>src/skills/</c> at the repo,
/// copied to <c>&lt;output&gt;/skills/</c> at build time). Surfaces them in the tray's Skills page
/// alongside gateway-reported skills so a user can browse what the node bundles.
/// </summary>
/// <remarks>
/// Local skills are discovery-only on the tray. The gateway is still the source of truth for
/// what an agent can actually invoke. Skills that originate from the gateway take precedence
/// when the same id appears in both places.
/// </remarks>
internal static class LocalSkillLoader
{
    public sealed class LocalSkill
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Emoji { get; init; } = "";
        public IReadOnlyList<string> Os { get; init; } = Array.Empty<string>();
    }

    // YAML frontmatter is between two `---` markers on otherwise-blank lines. The closing
    // `---` may or may not be followed by another line (skills sometimes end at the closing
    // delimiter with no body — `\z` makes the trailing newline optional).
    private static readonly Regex FrontmatterRegex = new(
        @"\A---\r?\n(.*?)\r?\n---(\r?\n|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IReadOnlyList<LocalSkill> Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var skillsDir = Path.Combine(baseDir, "skills");
        return LoadFromDir(skillsDir);
    }

    /// <summary>
    /// Loads skills from a specific directory. Exposed for testing; production callers
    /// should use <see cref="Load"/>, which resolves to the app's bundled skills folder.
    /// </summary>
    public static IReadOnlyList<LocalSkill> LoadFromDir(string skillsDir)
    {
        if (!Directory.Exists(skillsDir))
        {
            Logger.Info($"[LocalSkillLoader] No skills directory at {skillsDir}");
            return Array.Empty<LocalSkill>();
        }

        var results = new List<LocalSkill>();
        foreach (var dir in Directory.EnumerateDirectories(skillsDir))
        {
            var mdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(mdPath))
            {
                continue;
            }
            try
            {
                var skill = TryParse(mdPath, Path.GetFileName(dir));
                if (skill != null)
                {
                    results.Add(skill);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[LocalSkillLoader] Skipping {mdPath}: {ex.Message}");
            }
        }
        Logger.Info($"[LocalSkillLoader] Loaded {results.Count} local skill(s) from {skillsDir}");
        return results;
    }

    private static LocalSkill? TryParse(string path, string folderName)
    {
        var content = File.ReadAllText(path);
        var match = FrontmatterRegex.Match(content);
        if (!match.Success)
        {
            return null;
        }
        var fm = match.Groups[1].Value;

        var name = ReadFrontmatterScalar(fm, "name") ?? folderName;
        var description = ReadFrontmatterScalar(fm, "description") ?? "";
        var emoji = ReadNestedScalar(fm, "metadata", "openclaw", "emoji") ?? "";
        var osCsv = ReadNestedScalar(fm, "metadata", "openclaw", "os") ?? "";
        var os = ParseInlineList(osCsv);

        return new LocalSkill
        {
            Id = name,
            Name = name,
            Description = description.Trim(),
            Emoji = emoji.Trim().Trim('"', '\''),
            Os = os,
        };
    }

    /// <summary>
    /// Reads a top-level YAML scalar (single-line value) by key.
    /// Handles plain values, single-quoted, and double-quoted forms. Does NOT support YAML
    /// block scalars (|, &gt;) or multi-line folded values — descriptions in this catalog are
    /// always single-line by convention.
    /// </summary>
    private static string? ReadFrontmatterScalar(string frontmatter, string key)
    {
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var value = line.Substring(key.Length + 1).Trim();
            return Unquote(value);
        }
        return null;
    }

    /// <summary>
    /// Reads a nested scalar like <c>metadata.openclaw.emoji</c>. Looks for the parent key at
    /// column 0, then walks descendant keys by indentation. Tolerant of arbitrary whitespace.
    /// </summary>
    private static string? ReadNestedScalar(string frontmatter, params string[] keyPath)
    {
        if (keyPath.Length == 0) return null;
        var lines = frontmatter.Split('\n');

        // Walk top-down. At each level, find the next descendant matching keyPath[depth] whose
        // indentation is strictly greater than the parent's indentation.
        var parentIndent = -1;
        var startLine = 0;
        for (var depth = 0; depth < keyPath.Length; depth++)
        {
            var key = keyPath[depth];
            var found = false;
            for (var i = startLine; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                {
                    continue;
                }
                var indent = CountIndent(line);
                if (indent <= parentIndent && depth > 0)
                {
                    // Left the parent's subtree without finding the key.
                    return null;
                }
                var trimmed = line.TrimStart();
                if (depth == 0 && indent != 0)
                {
                    continue;
                }
                if (trimmed.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    found = true;
                    var rest = trimmed.Substring(key.Length + 1).Trim();
                    if (depth == keyPath.Length - 1)
                    {
                        return Unquote(rest);
                    }
                    parentIndent = indent;
                    startLine = i + 1;
                    break;
                }
            }
            if (!found)
            {
                return null;
            }
        }
        return null;
    }

    private static int CountIndent(string line)
    {
        var n = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') n++;
            else if (ch == '\t') n += 2;
            else break;
        }
        return n;
    }

    private static IReadOnlyList<string> ParseInlineList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var trimmed = value.Trim();
        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }
        var parts = new List<string>();
        foreach (var part in trimmed.Split(','))
        {
            var cleaned = Unquote(part.Trim());
            if (!string.IsNullOrEmpty(cleaned)) parts.Add(cleaned);
        }
        return parts;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"' && value[^1] == '"') ||
                (value[0] == '\'' && value[^1] == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }
        }
        return value;
    }
}

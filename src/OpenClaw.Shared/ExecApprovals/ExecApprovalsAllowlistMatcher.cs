using System;
using System.Collections.Generic;
using System.IO;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Matches a single <see cref="ExecAllowlistEntry"/> pattern against one or more
/// <see cref="ExecCommandResolution"/> segments produced by the normalizer.
/// </summary>
/// <remarks>
/// Pattern semantics (case-insensitive on all comparisons):
/// <list type="bullet">
///   <item>Patterns containing a path separator are compared against the resolved full path.</item>
///   <item>Bare-name patterns (no separator) are compared against the executable basename,
///         both with and without the <c>.exe</c> suffix, to handle Windows conventions.</item>
///   <item><c>*</c> is a wildcard matching any sequence of characters that does not
///         contain a path separator. This handles patterns like <c>python*</c> or <c>npm*</c>.</item>
/// </list>
/// </remarks>
public static class ExecApprovalsAllowlistMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="pattern"/> matches
    /// any resolution segment in <paramref name="resolutions"/>.
    /// An empty or whitespace-only pattern never matches.
    /// </summary>
    public static bool MatchesAny(
        string? pattern,
        IReadOnlyList<ExecCommandResolution> resolutions)
    {
        if (string.IsNullOrWhiteSpace(pattern) || resolutions.Count == 0)
            return false;

        foreach (var resolution in resolutions)
        {
            if (Matches(pattern, resolution))
                return true;
        }
        return false;
    }

    // Visible for testing.
    internal static bool Matches(string pattern, ExecCommandResolution resolution)
    {
        var trimmedPattern = pattern.Trim();
        if (trimmedPattern.Length == 0) return false;

        bool isPathPattern = trimmedPattern.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0;

        if (isPathPattern)
        {
            return resolution.ResolvedPath is not null
                && GlobMatch(trimmedPattern, resolution.ResolvedPath);
        }

        // Bare-name pattern: compare against executable basename.
        // Try both the ExecutableName (may include .exe) and the name without extension.
        var exeName = resolution.ExecutableName;
        if (GlobMatch(trimmedPattern, exeName)) return true;

        var nameNoExt = StripExeExtension(exeName);
        if (!string.Equals(nameNoExt, exeName, StringComparison.OrdinalIgnoreCase)
            && GlobMatch(trimmedPattern, nameNoExt))
            return true;

        // Also compare pattern without .exe against the basename-without-extension
        // so "git.exe" pattern matches a resolution with ExecutableName="git.exe".
        var patternNoExt = StripExeExtension(trimmedPattern);
        if (!string.Equals(patternNoExt, trimmedPattern, StringComparison.OrdinalIgnoreCase))
        {
            if (GlobMatch(patternNoExt, exeName)) return true;
            if (GlobMatch(patternNoExt, nameNoExt)) return true;
        }

        return false;
    }

    // Simple glob: supports * (matches any chars excluding path separators) and literal chars.
    // Case-insensitive. Does not support ? or character classes — keeping it minimal per research doc 04.
    private static bool GlobMatch(string pattern, string value)
    {
        return GlobMatchAt(pattern.AsSpan(), value.AsSpan());
    }

    private static bool GlobMatchAt(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        while (true)
        {
            if (pattern.IsEmpty) return value.IsEmpty;

            if (pattern[0] == '*')
            {
                // Consume consecutive stars.
                var rest = pattern[1..];
                while (!rest.IsEmpty && rest[0] == '*') rest = rest[1..];

                if (rest.IsEmpty) return true; // trailing star(s) match anything

                // Try matching the rest of the pattern at each position in value.
                for (int i = 0; i <= value.Length; i++)
                {
                    // * does not match path separators.
                    if (i > 0 && (value[i - 1] == Path.DirectorySeparatorChar || value[i - 1] == Path.AltDirectorySeparatorChar))
                        break;
                    if (GlobMatchAt(rest, value[i..]))
                        return true;
                }
                return false;
            }

            if (value.IsEmpty) return false;

            if (!char.ToLowerInvariant(pattern[0]).Equals(char.ToLowerInvariant(value[0])))
                return false;

            pattern = pattern[1..];
            value = value[1..];
        }
    }

    // Strip common Windows executable extensions (.exe, .cmd, .bat, .com) for bare-name matching.
    private static string StripExeExtension(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        if (name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        if (name.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) return name[..^4];
        return name;
    }
}

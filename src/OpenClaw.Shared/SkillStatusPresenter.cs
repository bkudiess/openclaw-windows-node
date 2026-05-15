using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Windows;

/// <summary>
/// Parses the raw <c>skills.status</c> gateway response into a list of <see cref="SkillStatusRow"/>
/// and applies the user's filter selection. Pure logic — testable without WinUI.
/// </summary>
public static class SkillStatusPresenter
{
    /// <summary>
    /// Extracts the <c>skills</c> array from a gateway payload (tolerates the several wrapping
    /// shapes the gateway has used historically) and projects each entry into a row view-model.
    /// </summary>
    public static IReadOnlyList<SkillStatusRow> Parse(JsonElement payload)
    {
        if (!TryExtractSkillsArray(payload, out var skillsArray))
        {
            return Array.Empty<SkillStatusRow>();
        }

        var rows = new List<SkillStatusRow>();
        foreach (var item in skillsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            rows.Add(ParseRow(item));
        }
        return rows;
    }

    /// <summary>
    /// Applies the user-facing filter (<c>All / Ready / Needs Setup / Disabled</c>) and sorts
    /// alphabetically by name within Disabled, then by state-then-name for the rest. Mac uses
    /// pure alphabetical; we match that for parity.
    /// </summary>
    public static IReadOnlyList<SkillStatusRow> Filter(
        IReadOnlyList<SkillStatusRow> rows,
        SkillsFilter filter)
    {
        IEnumerable<SkillStatusRow> filtered = filter switch
        {
            SkillsFilter.Ready => rows.Where(r => r.State == SkillRowState.Ready),
            SkillsFilter.NeedsSetup => rows.Where(r =>
                r.State == SkillRowState.NeedsInstall ||
                r.State == SkillRowState.NeedsEnv ||
                r.State == SkillRowState.NeedsSetup),
            SkillsFilter.Disabled => rows.Where(r => r.State == SkillRowState.Disabled),
            _ => rows,
        };

        return filtered
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryExtractSkillsArray(JsonElement data, out JsonElement skills)
    {
        skills = default;
        if (data.ValueKind == JsonValueKind.Array)
        {
            skills = data;
            return true;
        }
        if (data.ValueKind != JsonValueKind.Object) return false;

        if (data.TryGetProperty("skills", out var inner) && inner.ValueKind == JsonValueKind.Array)
        {
            skills = inner;
            return true;
        }
        if (data.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind == JsonValueKind.Array)
            {
                skills = payload;
                return true;
            }
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("skills", out var nested) &&
                nested.ValueKind == JsonValueKind.Array)
            {
                skills = nested;
                return true;
            }
        }
        return false;
    }

    private static SkillStatusRow ParseRow(JsonElement item)
    {
        var name = ReadString(item, "name");
        var id = ReadString(item, "id", fallback: name);
        var skillKey = ReadString(item, "skillKey", fallback: id);

        // The gateway sends either `disabled` (older) or `enabled`/`eligible` (newer). Map both.
        // Disabled means "admin-disabled in config"; Eligible means "all requirements met".
        var disabled = ReadBool(item, "disabled", defaultValue: false);
        if (!item.TryGetProperty("disabled", out _) && item.TryGetProperty("enabled", out var en))
        {
            disabled = en.ValueKind == JsonValueKind.False;
        }
        var eligible = ReadBool(item, "eligible", defaultValue: false);

        var (missingBins, missingEnv, missingConfig) = ReadMissing(item);
        var install = ReadInstallOptions(item);
        var configChecks = ReadConfigChecks(item);

        return new SkillStatusRow
        {
            Id = id,
            SkillKey = skillKey,
            Name = name,
            Emoji = ReadString(item, "emoji"),
            Description = ReadString(item, "description"),
            Source = ReadString(item, "source"),
            Homepage = ReadString(item, "homepage"),
            PrimaryEnv = ReadString(item, "primaryEnv"),
            Disabled = disabled,
            Eligible = eligible,
            Bundled = ReadBool(item, "bundled", defaultValue: false),
            MissingBins = missingBins,
            MissingEnv = missingEnv,
            MissingConfig = missingConfig,
            Install = install,
            ConfigChecks = configChecks,
        };
    }

    private static (IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>) ReadMissing(JsonElement item)
    {
        if (!item.TryGetProperty("missing", out var missing) || missing.ValueKind != JsonValueKind.Object)
        {
            return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
        return (
            ReadStringArray(missing, "bins"),
            ReadStringArray(missing, "env"),
            ReadStringArray(missing, "config"));
    }

    private static IReadOnlyList<SkillInstallOption> ReadInstallOptions(JsonElement item)
    {
        if (!item.TryGetProperty("install", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SkillInstallOption>();
        }
        var list = new List<SkillInstallOption>();
        foreach (var opt in arr.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            list.Add(new SkillInstallOption
            {
                Id = ReadString(opt, "id"),
                Kind = ReadString(opt, "kind"),
                Label = ReadString(opt, "label"),
                Bins = ReadStringArray(opt, "bins"),
            });
        }
        return list;
    }

    private static IReadOnlyList<SkillConfigCheck> ReadConfigChecks(JsonElement item)
    {
        if (!item.TryGetProperty("configChecks", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SkillConfigCheck>();
        }
        var list = new List<SkillConfigCheck>();
        foreach (var check in arr.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object) continue;
            list.Add(new SkillConfigCheck
            {
                Path = ReadString(check, "path"),
                Satisfied = ReadBool(check, "satisfied", defaultValue: false),
                ValueDisplay = ReadConfigValue(check),
            });
        }
        return list;
    }

    private static string ReadConfigValue(JsonElement check)
    {
        if (!check.TryGetProperty("value", out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => "",
        };
    }

    private static string ReadString(JsonElement parent, string key, string fallback = "")
    {
        if (parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() ?? fallback;
        }
        return fallback;
    }

    private static bool ReadBool(JsonElement parent, string key, bool defaultValue)
    {
        if (!parent.TryGetProperty(key, out var el)) return defaultValue;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }
}

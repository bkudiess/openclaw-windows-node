namespace OpenClaw.Shared.ExecApprovals;

internal readonly record struct ExecAllowlistRuleKey(
    string Pattern,
    string? ArgPattern,
    string? Source);

internal static class ExecAllowlistRuleIdentity
{
    internal static IEqualityComparer<ExecAllowlistRuleKey> MatchComparer { get; } =
        new RuleComparer(includeSource: false);

    internal static IEqualityComparer<ExecAllowlistRuleKey> AuthorityComparer { get; } =
        new RuleComparer(includeSource: true);

    internal static ExecAllowlistRuleKey From(ExecAllowlistEntry entry) =>
        new(
            entry.Pattern?.Trim() ?? "",
            NormalizeExactOptional(entry.ArgPattern),
            NormalizeExactOptional(entry.Source));

    internal static bool MatchKeyEquals(
        ExecAllowlistEntry left,
        ExecAllowlistEntry right) =>
        MatchComparer.Equals(From(left), From(right));

    internal static bool AuthorityEquals(
        ExecAllowlistEntry left,
        ExecAllowlistEntry right) =>
        AuthorityComparer.Equals(From(left), From(right));

    private static string? NormalizeExactOptional(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed class RuleComparer(bool includeSource)
        : IEqualityComparer<ExecAllowlistRuleKey>
    {
        public bool Equals(ExecAllowlistRuleKey left, ExecAllowlistRuleKey right) =>
            string.Equals(left.Pattern, right.Pattern, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ArgPattern, right.ArgPattern, StringComparison.Ordinal)
            && (!includeSource
                || string.Equals(left.Source, right.Source, StringComparison.Ordinal));

        public int GetHashCode(ExecAllowlistRuleKey key)
        {
            var hash = new HashCode();
            hash.Add(key.Pattern, StringComparer.OrdinalIgnoreCase);
            hash.Add(key.ArgPattern, StringComparer.Ordinal);
            if (includeSource)
                hash.Add(key.Source, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

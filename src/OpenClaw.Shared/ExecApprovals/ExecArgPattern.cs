using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

internal static class ExecArgPattern
{
    internal const string HashedPrefix = "sha256:argv:";

    internal static string BuildHashed(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var subject = new StringBuilder();
        var argumentCount = Math.Max(0, argv.Count - 1);
        subject.Append(argumentCount.ToString(CultureInfo.InvariantCulture));
        subject.Append('\0');
        for (var index = 1; index < argv.Count; index++)
        {
            var argument = argv[index] ?? "";
            subject.Append(Encoding.UTF8.GetByteCount(argument).ToString(
                CultureInfo.InvariantCulture));
            subject.Append('\0');
            subject.Append(argument);
            subject.Append('\0');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(subject.ToString()));
        return HashedPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    internal static bool Matches(string? argPattern, IReadOnlyList<string>? argv) =>
        !string.IsNullOrEmpty(argPattern)
        && argv is not null
        && argPattern.StartsWith(HashedPrefix, StringComparison.Ordinal)
        && string.Equals(argPattern, BuildHashed(argv), StringComparison.Ordinal);
}

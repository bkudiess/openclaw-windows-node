namespace OpenClaw.Shared;

/// <summary>
/// Raised when a persisted device identity exists but cannot be loaded safely.
/// The identity file is left unchanged so recovery requires an explicit reset.
/// </summary>
public sealed class DeviceIdentityLoadException : Exception
{
    public const string RecoveryMessage =
        "Saved device identity could not be loaded. OpenClaw did not replace it. Check file access or reset pairing explicitly.";

    public DeviceIdentityLoadException(string identityPath, Exception innerException)
        : base(RecoveryMessage, innerException)
    {
        IdentityPath = identityPath;
    }

    public string IdentityPath { get; }
}

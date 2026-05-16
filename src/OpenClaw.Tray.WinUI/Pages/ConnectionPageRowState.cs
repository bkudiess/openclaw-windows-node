using OpenClaw.Connection;

namespace OpenClawTray.Pages;

/// <summary>
/// Pure helpers describing the per-row state mapping on the
/// <see cref="ConnectionPage"/> saved-gateways list. Extracted from the
/// WinUI page so unit tests (which run as pure net10.0) can pin the
/// predicate without touching XAML types.
/// </summary>
internal static class ConnectionPageRowState
{
    /// <summary>
    /// Returns true when the active row's overflow menu should offer
    /// "Disconnect". Tear-down is meaningful while the connection is
    /// live (Connected / Ready / Degraded), in transit
    /// (Connecting / PairingRequired), or stuck in a critical state
    /// (Error). It is NOT meaningful while the teardown itself is in
    /// flight (Disconnecting) — re-entering would race the connection
    /// manager — nor in the no-badge states (Idle), where the caller
    /// renders a [Connect] button instead.
    /// </summary>
    internal static bool CanDisconnectFromBadge(OverallConnectionState state) => state is
        OverallConnectionState.Connected
        or OverallConnectionState.Ready
        or OverallConnectionState.Degraded
        or OverallConnectionState.Connecting
        or OverallConnectionState.PairingRequired
        or OverallConnectionState.Error;

    /// <summary>
    /// Returns true when the active row should render a status badge
    /// (any non-Idle state). Idle returns false → the caller renders a
    /// [Connect] button so the user can re-activate the row.
    /// </summary>
    internal static bool HasActiveRowBadge(OverallConnectionState state) =>
        state != OverallConnectionState.Idle;
}

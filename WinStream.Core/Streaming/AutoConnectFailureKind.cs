using System.Net.Sockets;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Core.Streaming;

/// <summary>
/// How an automatic-reconnect failure should be retried. Kept to three kinds —
/// a fourth, presence-aware "Prompt" kind was drafted and cut (see the
/// reconnect-policy plan's Alternative Approaches): the SETUP-400 pattern is at
/// least as consistent with a digest/pairing protocol bug as an unanswered Mac
/// prompt, so both collapse into <see cref="Silent"/> with identical retry
/// behavior and only the status text differs.
/// </summary>
public enum AutoConnectFailureKind
{
    /// <summary>
    /// Connection refused/timeout, receiver not found, or a SETUP-400
    /// <see cref="RtspStatusException"/>. Retries unbounded on the existing
    /// 15s-cooldown backoff — one bad attempt must never permanently strand
    /// the user.
    /// </summary>
    Silent,

    /// <summary>
    /// A password or network-ACL rejection (401/403/470) that cannot succeed
    /// without the user acting — the one kind that stays bounded and stops
    /// retrying until <see cref="AutoConnectAttemptTracker.Reset"/>.
    /// </summary>
    Config,

    /// <summary>
    /// Anything else (WASAPI <c>COMException</c>, PTP-lock failure, an empty
    /// SETUP body). Not confirmed to correlate with the Mac prompt, so it is
    /// not classified separately from <see cref="Silent"/> — same unbounded
    /// 15s-cooldown retry.
    /// </summary>
    Other
}

/// <summary>
/// Pure, Core-side classifier for automatic-reconnect failures. Mirrors
/// <see cref="ConnectionFailureCopy"/>'s existing 401/403/470 detection instead
/// of re-implementing it, so the two classifiers cannot drift apart on the same
/// status codes.
/// </summary>
public static class AutoConnectFailureKindClassifier
{
    public static AutoConnectFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RtspStatusException rtsp)
        {
            if (IsConfigStatusCode(rtsp.StatusCode))
            {
                return AutoConnectFailureKind.Config;
            }

            return IsSetupBadRequest(rtsp) ? AutoConnectFailureKind.Silent : AutoConnectFailureKind.Other;
        }

        if (exception is ReceiverPasswordRequiredException or AirPlayPasswordRejectedException)
        {
            return AutoConnectFailureKind.Config;
        }

        if (ConnectionFailureCopy.IsWrongPassword(exception.Message) ||
            ConnectionFailureCopy.IsPasswordRequired(exception.Message) ||
            ConnectionFailureCopy.IsNetworkAcl(exception.Message))
        {
            return AutoConnectFailureKind.Config;
        }

        return IsReceiverUnreachable(exception) ? AutoConnectFailureKind.Silent : AutoConnectFailureKind.Other;
    }

    /// <summary>
    /// True for the specific "SETUP session failed with RTSP 400" pattern the
    /// field logs show following a persisted pair-verify success. Classified
    /// <see cref="AutoConnectFailureKind.Silent"/>, not a new "Prompt" kind —
    /// exposed separately so callers can pick distinct retrying copy
    /// ("Waiting on the Mac") without re-deriving the pattern.
    /// </summary>
    public static bool IsSetupBadRequest(Exception exception) =>
        exception is RtspStatusException { StatusCode: 400 } rtsp &&
        string.Equals(rtsp.Method, "SETUP session", StringComparison.Ordinal);

    private static bool IsConfigStatusCode(int statusCode) => statusCode is 401 or 403 or 470;

    private static bool IsReceiverUnreachable(Exception exception) =>
        exception is SocketException or TimeoutException or OperationCanceledException;
}

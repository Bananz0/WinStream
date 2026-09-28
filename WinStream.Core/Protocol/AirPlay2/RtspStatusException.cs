using WinStream.Core.Protocol.Raop;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Typed AirPlay 2 RTSP failure carrying the failing method and status code as
/// queryable properties, so callers (reconnect-policy classification,
/// diagnostics) can distinguish e.g. a password rejection from a SETUP
/// timeout without parsing <see cref="Exception.Message"/>.
/// </summary>
public sealed class RtspStatusException : Exception
{
    public RtspStatusException(string method, int statusCode, string message)
        : base(message)
    {
        Method = method;
        StatusCode = statusCode;
    }

    /// <summary>The RTSP method that failed, e.g. "SETUP session".</summary>
    public string Method { get; }

    /// <summary>The RTSP status code the receiver returned, e.g. 401.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// AirPlay2-only success check for <see cref="RtspResponse"/>.
/// </summary>
/// <remarks>
/// <see cref="RtspResponse.EnsureSuccess"/> is shared with the RAOP path
/// (5 call sites in <c>RaopSession.cs</c>) and cannot be changed to throw a
/// different exception type per caller, so it is left untouched. AirPlay2
/// call sites in <c>EncryptedRtspClient.cs</c> use this extension instead.
/// </remarks>
public static class RtspResponseAirPlayExtensions
{
    public static void EnsureAirPlaySuccess(this RtspResponse response, string method)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Without this note a password-protected receiver reads as a pairing
        // failure, which sends the user back to re-typing a code that was fine.
        var hint = response.IsUnauthorized
            ? " The receiver is asking for its AirPlay password."
            : string.Empty;
        throw new RtspStatusException(
            method,
            response.StatusCode,
            $"{method} failed with RTSP {response.StatusCode} {response.ReasonPhrase}.{hint}");
    }
}

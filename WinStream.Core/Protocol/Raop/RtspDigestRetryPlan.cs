namespace WinStream.Core.Protocol.Raop;

/// <summary>
/// Pure decision logic for an RTSP Digest 401 challenge, extracted from the
/// live socket path so retry/give-up rules are unit-testable without a peer.
/// </summary>
public static class RtspDigestRetryPlan
{
    public readonly record struct Attempt(string Realm, string Nonce, string Username, string Authorization);

    /// <summary>
    /// Decides the Digest attempt to retry a 401 with, or <c>null</c> when there
    /// is nothing left to try: no password to answer with, the caller already
    /// supplied its own <c>Authorization</c>, or the challenge does not parse.
    /// </summary>
    public static Attempt? NextAttempt(
        string? challenge,
        string method,
        string uri,
        string? password,
        bool callerSuppliedAuthorization)
    {
        if (password is null || callerSuppliedAuthorization)
        {
            return null;
        }

        if (!RtspDigestAuth.TryParseChallenge(challenge, out var realm, out var nonce))
        {
            return null;
        }

        var username = RtspDigestAuth.UsernameCandidates(realm)[0];
        var authorization = RtspDigestAuth.BuildAuthorization(realm, nonce, method, uri, password, username);
        return new Attempt(realm, nonce, username, authorization);
    }
}

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// The user dismissed the AirPlay code prompt. Distinct from
/// <see cref="OperationCanceledException"/> so aborting a connect is never
/// mistaken for "pair transiently instead".
/// </summary>
public sealed class PairingPinSkippedException : Exception
{
    public PairingPinSkippedException()
        : base("Persistent pairing skipped — no AirPlay code entered.")
    {
    }
}

namespace WinStream.Core.Protocol.Raop;

public static class NtpTime
{
    private const ulong NtpEpochDeltaSeconds = 2208988800UL;

    public static ulong Now()
    {
        var unixSeconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fraction = (ulong)((DateTimeOffset.UtcNow.Millisecond / 1000.0) * uint.MaxValue);
        return ((unixSeconds + NtpEpochDeltaSeconds) << 32) | (fraction & uint.MaxValue);
    }
}

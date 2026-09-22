using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class AirPlayCapabilityTests
{
    [Fact]
    public void ParseFeatures_reads_hex_low_word()
    {
        var features = AirPlayCapability.ParseFeatures("0x405F8A00,0x1C340");
        Assert.Equal(0x405F8A00, features);
    }

    [Fact]
    public void Mixed_selection_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AirPlayCapability.EnsureHomogeneousSelection(
                [AirPlayProtocolKind.ClassicRaop, AirPlayProtocolKind.AirPlay2]));

        Assert.Contains("Mixed AirPlay 1 and AirPlay 2", error.Message);
    }

    [Fact]
    public void PreferredProtocol_prefers_ap2_when_dual_capable()
    {
        Assert.Equal(
            AirPlayProtocolKind.AirPlay2,
            AirPlayCapability.PreferredProtocol(classic: true, airPlay2: true));
    }

    [Fact]
    public void PreferredProtocol_falls_back_to_classic_when_ap2_unavailable()
    {
        Assert.Equal(
            AirPlayProtocolKind.ClassicRaop,
            AirPlayCapability.PreferredProtocol(classic: true, airPlay2: false));
    }

    [Fact]
    public void PreferredProtocol_returns_ap2_when_classic_unavailable()
    {
        Assert.Equal(
            AirPlayProtocolKind.AirPlay2,
            AirPlayCapability.PreferredProtocol(classic: false, airPlay2: true));
    }

    [Theory]
    [InlineData(true, true, AirPlayProtocolKind.AirPlay2)]
    [InlineData(false, true, AirPlayProtocolKind.AirPlay2)]
    [InlineData(true, false, AirPlayProtocolKind.ClassicRaop)]
    [InlineData(false, false, AirPlayProtocolKind.Unknown)]
    public void PreferredProtocol_truth_table(
        bool classic,
        bool airPlay2,
        AirPlayProtocolKind expected)
    {
        Assert.Equal(expected, AirPlayCapability.PreferredProtocol(classic, airPlay2));
    }

    [Theory]
    [InlineData(true, 0, null, true)]
    [InlineData(false, 1L << 30, null, true)]
    [InlineData(false, 0, "366.0", true)]
    [InlineData(false, 0, "100.0", false)]
    public void SupportsAirPlay2_detects_pairing_features_and_version(
        bool hasPairing,
        long features,
        string? version,
        bool expected)
    {
        Assert.Equal(
            expected,
            AirPlayCapability.SupportsAirPlay2(hasPairing, features, version));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0,1", true)]
    [InlineData("0,1,3,5", true)]
    [InlineData("0,3,5", true)]
    [InlineData("3,5", false)]
    public void SupportsClassicRaop_requires_clear_or_rsa_encryption(
        string? encryptionTypes,
        bool expected)
    {
        Assert.Equal(expected, AirPlayCapability.SupportsClassicRaop(encryptionTypes));
    }

    [Theory]
    [InlineData(null, RaopEncryptionMode.Unsupported)]
    [InlineData("", RaopEncryptionMode.Unsupported)]
    [InlineData("3,5", RaopEncryptionMode.Unsupported)]
    [InlineData("0,3,5", RaopEncryptionMode.None)]
    [InlineData("0", RaopEncryptionMode.None)]
    [InlineData("0,1", RaopEncryptionMode.Rsa)]
    [InlineData("1", RaopEncryptionMode.Rsa)]
    public void ResolveEncryptionMode_prefers_rsa_then_clear(
        string? encryptionTypes,
        RaopEncryptionMode expected)
    {
        Assert.Equal(expected, AirPlayCapability.ResolveEncryptionMode(encryptionTypes));
    }
}

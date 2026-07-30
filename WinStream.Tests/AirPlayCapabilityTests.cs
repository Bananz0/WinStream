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
    public void Preferred_uses_classic_when_ap2_gate_disabled()
    {
        var kind = AirPlayCapability.PreferredProtocol(
            classic: true,
            airPlay2: true,
            airPlay2GateEnabled: false);

        Assert.Equal(AirPlayProtocolKind.ClassicRaop, kind);
    }

    [Fact]
    public void Preferred_uses_ap2_when_gate_enabled()
    {
        var kind = AirPlayCapability.PreferredProtocol(
            classic: true,
            airPlay2: true,
            airPlay2GateEnabled: true);

        Assert.Equal(AirPlayProtocolKind.AirPlay2, kind);
    }
}

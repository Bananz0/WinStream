using WinStream.Core;
using WinStream.Core.Network;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SessionLatencyFramesTests
{
    private static DeviceInfo DummyReceiver() => new()
    {
        DisplayName = "Test",
        Model = "Test",
        IPAddress = "127.0.0.1",
        Port = 7000
    };

    [Fact]
    public void AirPlay2Session_SetEffectiveLatencyFrames_allows_lab_packet_floor()
    {
        var session = new AirPlay2Session(DummyReceiver());
        Assert.Equal(88200u, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(LatencyAutoController.LabPacketFrames);
        Assert.Equal(352u, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(100);
        Assert.Equal(352u, session.EffectiveLatencyFrames);
    }

    [Fact]
    public void RaopSession_SetEffectiveLatencyFrames_allows_lab_packet_floor()
    {
        var session = new RaopSession(DummyReceiver());
        session.SetEffectiveLatencyFrames(LatencyAutoController.VeryLowFrames);
        Assert.Equal(22050u, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(0);
        Assert.Equal(352u, session.EffectiveLatencyFrames);
    }

    [Fact]
    public void Both_protocols_accept_the_shared_auto_step()
    {
        var shared = LatencyAutoController.AutoStartFrames + LatencyAutoController.StepFrames;
        var a = new AirPlay2Session(DummyReceiver());
        var b = new RaopSession(DummyReceiver());
        a.SetEffectiveLatencyFrames(shared);
        b.SetEffectiveLatencyFrames(shared);
        Assert.Equal(shared, a.EffectiveLatencyFrames);
        Assert.Equal(shared, b.EffectiveLatencyFrames);
    }

    [Fact]
    public void MostStable_fixed_frames_match_controller()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        Assert.Equal(88200u, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);
    }
}

/// <summary>Named SETUP latencyMin/Max derivation tests (StreamSetup / LatencyMin).</summary>
public class StreamSetupLatencyMinTests
{
    [Fact]
    public void SetupLatencyMin_for_LabPacket_is_352()
    {
        var l = LatencyAutoController.LabPacketFrames;
        Assert.Equal(352u, LatencyAutoController.SetupLatencyMin(l));
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(l));
    }

    [Fact]
    public void SetupLatencyMin_for_Experimental_or_higher_is_11025()
    {
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(11025));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(22050));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(44100));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(88200));
    }
}

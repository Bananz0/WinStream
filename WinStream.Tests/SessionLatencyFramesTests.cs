using WinStream.Core.Network;
using WinStream.Core.Persistence;
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
    public void AirPlay2Session_SetEffectiveLatencyFrames_clamps_and_reads()
    {
        var session = new AirPlay2Session(DummyReceiver());
        Assert.Equal(88200u, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(LatencyAutoController.AutoStartFrames);
        Assert.Equal(LatencyAutoController.AutoStartFrames, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(100);
        Assert.Equal(LatencyAutoController.LatencyMinFrames, session.EffectiveLatencyFrames);
    }

    [Fact]
    public void RaopSession_SetEffectiveLatencyFrames_clamps_and_reads()
    {
        var session = new RaopSession(DummyReceiver());
        Assert.Equal(88200u, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(LatencyAutoController.LowDelayFrames);
        Assert.Equal(LatencyAutoController.LowDelayFrames, session.EffectiveLatencyFrames);

        session.SetEffectiveLatencyFrames(0);
        Assert.Equal(LatencyAutoController.LatencyMinFrames, session.EffectiveLatencyFrames);
    }

    [Fact]
    public void LateJoin_shares_current_auto_step()
    {
        // Multi-room contract: both sessions receive the shared controller step.
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

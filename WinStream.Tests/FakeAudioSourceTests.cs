using WinStream.Core.Audio;

namespace WinStream.Tests;

public class FakeAudioSourceTests
{
    [Fact]
    public async Task StartAsync_EmitsNonSilentFrames()
    {
        await using var source = new FakeAudioSource(forcedRms: 0.2);
        var frames = new List<AudioFrame>();
        source.FrameAvailable += (_, frame) => frames.Add(frame);

        await source.StartAsync();
        await Task.Delay(80);

        Assert.NotEmpty(frames);
        Assert.False(source.IsSilent);
        Assert.True(source.CurrentRms > RmsCalculator.DefaultSilenceThreshold);
        Assert.Equal(44100, frames[0].Format.SampleRate);
        Assert.Equal(2, frames[0].Format.Channels);
        Assert.True(frames[0].Pcm.Length > 0);

        await source.StopAsync();
    }

    [Fact]
    public async Task StopAsync_EndsCapture()
    {
        await using var source = new FakeAudioSource();
        await source.StartAsync();
        Assert.True(source.IsCapturing);

        await source.StopAsync();
        Assert.False(source.IsCapturing);
        Assert.Equal(0, source.CurrentRms);
    }

    [Fact]
    public async Task SimulateDeviceInvalidation_RaisesEvent()
    {
        await using var source = new FakeAudioSource();
        var raised = false;
        source.DeviceInvalidated += (_, _) => raised = true;

        source.SimulateDeviceInvalidation();

        Assert.True(raised);
    }
}

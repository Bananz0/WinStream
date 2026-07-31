using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class CaptureCallbackMeasurerTests
{
    [Fact]
    public void Returns_zero_until_warmup_is_complete()
    {
        var measurer = new CaptureCallbackMeasurer(
            windowSize: 4,
            warmupSamples: 3,
            frequencyHz: 1000);

        measurer.RecordInterval(2);
        measurer.RecordInterval(2);

        Assert.False(measurer.IsReady);
        Assert.Equal(0, measurer.MeasuredContributionMilliseconds);
    }

    [Fact]
    public void Reports_ceiling_of_rolling_p95()
    {
        var measurer = new CaptureCallbackMeasurer(
            windowSize: 4,
            warmupSamples: 4,
            frequencyHz: 1000);
        foreach (var ticks in new long[] { 2, 2, 3, 4 })
        {
            measurer.RecordInterval(ticks);
        }

        Assert.True(measurer.IsReady);
        Assert.Equal(4, measurer.MeasuredContributionMilliseconds);
        Assert.False(LinkSlaEligibility.IsMeasuredCaptureSlaCapable(
            measurer.MeasuredContributionMilliseconds));
    }

    [Fact]
    public void Rolling_window_evicts_old_pressure()
    {
        var measurer = new CaptureCallbackMeasurer(
            windowSize: 4,
            warmupSamples: 4,
            frequencyHz: 1000);
        measurer.RecordInterval(20);
        for (var i = 0; i < 7; i++)
        {
            measurer.RecordInterval(2);
        }

        Assert.Equal(2, measurer.MeasuredContributionMilliseconds);
    }
}

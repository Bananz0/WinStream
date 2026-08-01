using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class ExtremePressureHysteresisTests
{
    [Fact]
    public void One_pressure_window_does_not_warn()
    {
        var hysteresis = new ExtremePressureHysteresis();

        Assert.False(hysteresis.ObserveWindow(pressureThisWindow: true));
        Assert.False(hysteresis.IsWarningVisible);
    }

    [Fact]
    public void Two_consecutive_pressure_windows_warn()
    {
        var hysteresis = new ExtremePressureHysteresis();
        hysteresis.ObserveWindow(pressureThisWindow: true);

        Assert.True(hysteresis.ObserveWindow(pressureThisWindow: true));
        Assert.True(hysteresis.IsWarningVisible);
    }

    [Fact]
    public void A_clean_window_between_two_hiccups_never_warns()
    {
        var hysteresis = new ExtremePressureHysteresis();

        hysteresis.ObserveWindow(pressureThisWindow: true);
        hysteresis.ObserveWindow(pressureThisWindow: false);

        Assert.False(hysteresis.ObserveWindow(pressureThisWindow: true));
    }

    [Fact]
    public void A_clean_window_keeps_a_visible_warning_readable()
    {
        var hysteresis = new ExtremePressureHysteresis();
        hysteresis.ObserveWindow(pressureThisWindow: true);
        hysteresis.ObserveWindow(pressureThisWindow: true);

        Assert.True(hysteresis.ObserveWindow(pressureThisWindow: false));
        Assert.True(hysteresis.IsWarningVisible);
    }

    [Fact]
    public void Enough_clean_windows_clear_a_visible_warning()
    {
        var hysteresis = new ExtremePressureHysteresis();
        hysteresis.ObserveWindow(pressureThisWindow: true);
        hysteresis.ObserveWindow(pressureThisWindow: true);

        for (var i = 1; i < ExtremePressureHysteresis.ConsecutiveCleanWindowsToClear; i++)
        {
            Assert.True(hysteresis.ObserveWindow(pressureThisWindow: false));
        }

        Assert.False(hysteresis.ObserveWindow(pressureThisWindow: false));
        Assert.False(hysteresis.IsWarningVisible);
    }

    [Fact]
    public void New_pressure_resets_the_clean_window_count()
    {
        var hysteresis = new ExtremePressureHysteresis();
        hysteresis.ObserveWindow(pressureThisWindow: true);
        hysteresis.ObserveWindow(pressureThisWindow: true);

        for (var i = 0; i < ExtremePressureHysteresis.ConsecutiveCleanWindowsToClear - 1; i++)
        {
            hysteresis.ObserveWindow(pressureThisWindow: false);
        }

        hysteresis.ObserveWindow(pressureThisWindow: true);

        Assert.True(hysteresis.ObserveWindow(pressureThisWindow: false));
    }

    [Fact]
    public void Sustained_pressure_keeps_the_warning_visible()
    {
        var hysteresis = new ExtremePressureHysteresis();

        for (var i = 0; i < 20; i++)
        {
            hysteresis.ObserveWindow(pressureThisWindow: true);
        }

        Assert.True(hysteresis.IsWarningVisible);
        Assert.True(hysteresis.ObserveWindow(pressureThisWindow: true));
    }

    [Fact]
    public void Reset_hides_the_warning_and_drops_the_streak()
    {
        var hysteresis = new ExtremePressureHysteresis();
        hysteresis.ObserveWindow(pressureThisWindow: true);
        hysteresis.ObserveWindow(pressureThisWindow: true);

        hysteresis.Reset();

        Assert.False(hysteresis.IsWarningVisible);
        Assert.False(hysteresis.ObserveWindow(pressureThisWindow: true));
    }
}

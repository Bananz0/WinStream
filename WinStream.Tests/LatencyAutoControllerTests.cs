using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class LatencyAutoControllerTests
{
    private static readonly DateTimeOffset T0 =
        DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    [Fact]
    public void ResetForConnect_Auto_starts_at_11025()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);

        Assert.Equal(11025u, controller.EffectiveFrames);
        Assert.Equal(LatencyAutoController.AutoStartFrames, controller.EffectiveFrames);
        Assert.True(controller.IsAutoEnabled);
    }

    [Fact]
    public void ResetForConnect_presets_disable_auto_and_set_fixed_frames()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.VeryLow);
        Assert.Equal(LatencyAutoController.VeryLowFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.Experimental);
        Assert.Equal(LatencyAutoController.ExperimentalFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.Equal(LatencyAutoController.LabPacketFrames, controller.EffectiveFrames);
        Assert.False(controller.IsAutoEnabled);
        Assert.True(controller.IsExtremeRaiseEnabled);

        controller.ResetForConnect(PlaybackResponsiveness.LowDelay);
        Assert.Equal(LatencyAutoController.LowDelayFrames, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.Balanced);
        Assert.Equal(LatencyAutoController.BalancedFrames, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void HasPressure_treats_timeline_reanchors_as_delivery_stress()
    {
        Assert.False(LatencyAutoController.HasPressure(0, 0, 0));
        Assert.True(LatencyAutoController.HasPressure(0, 0, 1));
    }

    [Fact]
    public void HasImmediatePressure_treats_stall_gap_or_reanchor_as_a_free_raise()
    {
        Assert.False(LatencyAutoController.HasImmediatePressure(0, 0, 0));
        Assert.True(LatencyAutoController.HasImmediatePressure(1, 0, 0));
        Assert.True(LatencyAutoController.HasImmediatePressure(0, 1, 0));
        Assert.True(LatencyAutoController.HasImmediatePressure(0, 0, 1));
    }

    [Fact]
    public void ResolveFixedFrames_matches_documented_constants_for_every_mode()
    {
        Assert.Equal(
            LatencyAutoController.AutoStartFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Auto));
        Assert.Equal(
            LatencyAutoController.VeryLowFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.VeryLow));
        Assert.Equal(
            LatencyAutoController.ExperimentalFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Experimental));
        Assert.Equal(
            LatencyAutoController.LabPacketFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.LabPacket));
        Assert.Equal(
            LatencyAutoController.LowDelayFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.LowDelay));
        Assert.Equal(
            LatencyAutoController.BalancedFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.Balanced));
        Assert.Equal(
            LatencyAutoController.MostStableFrames,
            LatencyAutoController.ResolveFixedFrames(PlaybackResponsiveness.MostStable));
        Assert.Equal(2112u, LatencyAutoController.LabPacketFrames);
        Assert.True(LatencyAutoController.LabPacketFrames >= LatencyAutoController.PacketFloorFrames);
        Assert.Equal(3520u, LatencyAutoController.ExtremeMidFrames);
        Assert.Equal(3520u, LatencyAutoController.AutoFloorFrames);
        Assert.Equal(11025u, LatencyAutoController.ExtremeCeilingFrames);
        Assert.Equal(11025u, LatencyAutoController.AutoStartFrames);
    }

    [Fact]
    public void NextRungUp_and_down_mirror_the_extreme_ladder()
    {
        Assert.Equal(3520u, LatencyAutoController.NextRungUp(2112));
        Assert.Equal(11025u, LatencyAutoController.NextRungUp(3520));
        Assert.Equal(22050u, LatencyAutoController.NextRungUp(11025));
        Assert.Equal(88200u, LatencyAutoController.NextRungUp(77175));

        Assert.Equal(2112u, LatencyAutoController.NextRungDown(3520));
        Assert.Equal(3520u, LatencyAutoController.NextRungDown(11025));
        Assert.Equal(11025u, LatencyAutoController.NextRungDown(22050));
        Assert.Equal(77175u, LatencyAutoController.NextRungDown(88200));
    }

    [Fact]
    public void NextAutoRungUp_and_down_walk_the_finer_15x_ladder()
    {
        Assert.Equal(5280u, LatencyAutoController.NextAutoRungUp(3520));
        Assert.Equal(7920u, LatencyAutoController.NextAutoRungUp(5280));
        Assert.Equal(11025u, LatencyAutoController.NextAutoRungUp(7920));
        Assert.Equal(16538u, LatencyAutoController.NextAutoRungUp(11025));
        Assert.Equal(88200u, LatencyAutoController.NextAutoRungUp(88200));
        Assert.Equal(88200u, LatencyAutoController.NextAutoRungUp(90000));

        Assert.Equal(11025u, LatencyAutoController.NextAutoRungDown(16538));
        Assert.Equal(7920u, LatencyAutoController.NextAutoRungDown(11025));
        Assert.Equal(3520u, LatencyAutoController.NextAutoRungDown(5280));
        Assert.Equal(3520u, LatencyAutoController.NextAutoRungDown(3520));
        Assert.Equal(3520u, LatencyAutoController.NextAutoRungDown(0));
    }

    [Fact]
    public void Auto_raises_immediately_on_stall_or_gap_bypassing_cooldown()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        var t = T0.AddSeconds(3);
        Assert.True(controller.TryAdjustAuto(
            0, 0, true, false, t, ptpReseedDeltaInWindow: 1));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // Same instant: immediate raises bypass the raise cooldown entirely.
        Assert.True(controller.TryAdjustAuto(
            0, 0, true, false, t, gapDeltaInWindow: 1));
        Assert.Equal(22050u, controller.EffectiveFrames);

        Assert.True(controller.TryAdjustAuto(
            0, 0, true, false, t, reanchorsInWindow: 1));
        Assert.Equal(33075u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_leading_indicator_raise_respects_the_tightened_cooldown()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        Assert.True(controller.TryAdjustAuto(3, 0, true, false, T0.AddSeconds(3)));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // Within the 4s Auto raise cooldown.
        Assert.False(controller.TryAdjustAuto(3, 0, true, false, T0.AddSeconds(4)));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // 5s after the last raise: cooldown has elapsed.
        Assert.True(controller.TryAdjustAuto(0, 5, true, false, T0.AddSeconds(8)));
        Assert.Equal(22050u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_climbs_the_finer_ladder_to_ceiling_via_repeated_immediate_raises()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        var t = T0.AddSeconds(3);
        for (var i = 0; i < LatencyAutoController.AutoLadderFrames.Length + 1; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t, ptpReseedDeltaInWindow: 1);
        }

        Assert.Equal(LatencyAutoController.CeilingFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_never_lowers_while_audible_no_matter_how_long_the_clean_streak()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(5);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold + 10; i++)
        {
            Assert.False(controller.TryAdjustAuto(0, 0, true, false, t));
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.Equal(16538u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_lowers_one_rung_only_after_a_silence_window_and_long_clean_streak()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(5);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            Assert.False(controller.TryAdjustAuto(0, 0, true, false, t));
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        // Clean streak threshold reached, but only lowers once silence arrives.
        Assert.True(controller.TryAdjustAuto(
            0,
            0,
            true,
            isSilent: true,
            t,
            silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(11025u, controller.EffectiveFrames);

        // At most one rung per silence window.
        t = t.Add(LatencyAutoController.SignalWindow);
        Assert.False(controller.TryAdjustAuto(
            0,
            0,
            true,
            isSilent: true,
            t,
            silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_pressure_mid_streak_resets_the_clean_window_counter()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(5);
        for (var i = 0; i < 10; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        // Pressure mid-streak resets the clean counter.
        controller.TryAdjustAuto(0, 0, true, false, t, ptpReseedDeltaInWindow: 1);
        Assert.Equal(22050u, controller.EffectiveFrames);

        t = t.Add(LatencyAutoController.SignalWindow);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold - 1; i++)
        {
            Assert.False(controller.TryAdjustAuto(
                0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.Equal(22050u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_can_return_to_the_80ms_floor_after_extended_clean_delivery()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(25);
        var guard = 0;
        while (controller.EffectiveFrames > LatencyAutoController.AutoFloorFrames && guard++ < 10_000)
        {
            controller.TryAdjustAuto(
                0,
                0,
                true,
                isSilent: true,
                t,
                silentDurationInWindow: LatencyAutoController.MinSilenceForLower);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.Equal(LatencyAutoController.AutoFloorFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_ratchet_blocks_relowering_a_forced_reraise_rung_until_it_decays()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        // Climb to 16538, then lower back down to 11025 across a long clean+silent streak.
        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(5);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.True(controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(11025u, controller.EffectiveFrames);

        t = t.Add(LatencyAutoController.SignalWindow);

        // Pressure forces a raise straight back into 16538 — the ratchet arms
        // for RatchetBaseMinutes, comfortably longer than the ~120s it takes
        // to accumulate the clean-window streak below.
        Assert.True(controller.TryAdjustAuto(3, 0, true, false, t));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // Repeat the sustained-clean+silent sequence right away: the ratchet must hold.
        t = t.Add(LatencyAutoController.SignalWindow);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.False(controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // Once the decaying cooldown elapses, the same clean+silent streak succeeds.
        t = t.AddMinutes(LatencyAutoController.RatchetBaseMinutes + 1);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.True(controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void Auto_ratchet_lockout_grows_on_repeated_re_raises_but_never_stays_permanent()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        controller.TryAdjustAuto(0, 0, true, false, T0.AddSeconds(3), ptpReseedDeltaInWindow: 1);
        Assert.Equal(16538u, controller.EffectiveFrames);

        var t = T0.AddSeconds(5);

        // First lower/re-raise cycle: locks for RatchetBaseMinutes.
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower);
        Assert.Equal(11025u, controller.EffectiveFrames);
        t = t.Add(LatencyAutoController.SignalWindow);
        controller.TryAdjustAuto(3, 0, true, false, t);
        Assert.Equal(16538u, controller.EffectiveFrames);

        // Second lower/re-raise cycle, after the first lockout decays: the
        // new lockout must be longer (doubled), so the same short wait that
        // worked once is no longer enough.
        t = t.AddMinutes(LatencyAutoController.RatchetBaseMinutes + 1);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower);
        Assert.Equal(11025u, controller.EffectiveFrames);
        t = t.Add(LatencyAutoController.SignalWindow);
        controller.TryAdjustAuto(3, 0, true, false, t);
        Assert.Equal(16538u, controller.EffectiveFrames);

        t = t.AddSeconds(30); // would have cleared the first-time (shorter) lock
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.False(controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(16538u, controller.EffectiveFrames);

        // But it is never permanent: enough elapsed time still recovers it.
        t = t.AddMinutes(LatencyAutoController.RatchetMaxMinutes + 1);
        for (var i = 0; i < LatencyAutoController.LowerCleanWindowsThreshold; i++)
        {
            controller.TryAdjustAuto(0, 0, true, false, t);
            t = t.Add(LatencyAutoController.SignalWindow);
        }

        Assert.True(controller.TryAdjustAuto(
            0, 0, true, isSilent: true, t, silentDurationInWindow: LatencyAutoController.MinSilenceForLower));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void Extreme_starts_at_2112_and_raises_through_3520_then_11025()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        controller.MarkAudioStarted(T0);

        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);

        Assert.True(controller.TryRaise(3, 0, true, false, T0.AddSeconds(6)));
        Assert.Equal(3520u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);

        Assert.False(controller.TryRaise(10, 10, true, false, T0.AddSeconds(10))); // cool-down

        Assert.True(controller.TryRaise(0, 5, true, false, T0.AddSeconds(20)));
        Assert.Equal(11025u, controller.EffectiveFrames);
        Assert.True(controller.IsExtremeLadderExhausted);

        Assert.False(controller.TryRaise(10, 10, true, false, T0.AddSeconds(40)));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void Extreme_ResetForConnect_returns_to_2112_after_a_climb()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        controller.MarkAudioStarted(T0);
        controller.TryRaise(3, 0, true, false, T0.AddSeconds(6));
        controller.TryRaise(3, 0, true, false, T0.AddSeconds(20));
        Assert.Equal(11025u, controller.EffectiveFrames);

        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);
        Assert.Equal(2112u, controller.EffectiveFrames);
        Assert.False(controller.IsExtremeLadderExhausted);
    }

    [Fact]
    public void Extreme_TryRaise_ignores_silence_startup_and_subthreshold()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.LabPacket);

        Assert.False(controller.TryRaise(10, 10, true, false, T0.AddSeconds(10))); // unmarked

        controller.MarkAudioStarted(T0);
        Assert.False(controller.TryRaise(10, 10, true, false, T0.AddSeconds(1))); // grace
        Assert.False(controller.TryRaise(10, 10, true, true, T0.AddSeconds(10))); // silent
        Assert.False(controller.TryRaise(10, 10, false, false, T0.AddSeconds(10))); // not streaming
        Assert.False(controller.TryRaise(2, 4, true, false, T0.AddSeconds(10))); // below thresholds
        Assert.Equal(2112u, controller.EffectiveFrames);
    }

    [Fact]
    public void TryAdjustAuto_ignores_startup_grace_and_non_streaming()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);

        Assert.False(controller.TryAdjustAuto(10, 10, true, false, T0.AddSeconds(10))); // unmarked

        controller.MarkAudioStarted(T0);
        Assert.False(controller.TryAdjustAuto(10, 10, true, false, T0.AddSeconds(1))); // grace
        Assert.False(controller.TryAdjustAuto(10, 10, false, false, T0.AddSeconds(10))); // not streaming
        Assert.False(controller.TryAdjustAuto(2, 4, true, false, T0.AddSeconds(10))); // below thresholds

        controller.ResetForConnect(PlaybackResponsiveness.MostStable);
        controller.MarkAudioStarted(T0);
        Assert.False(controller.TryAdjustAuto(10, 10, true, false, T0.AddSeconds(10)));
        Assert.Equal(LatencyAutoController.MostStableFrames, controller.EffectiveFrames);
    }

    [Fact]
    public void TryAdjustAuto_leading_indicator_pressure_is_suppressed_while_silent()
    {
        var controller = new LatencyAutoController();
        controller.ResetForConnect(PlaybackResponsiveness.Auto);
        controller.MarkAudioStarted(T0);

        // High leading-indicator pressure but isSilent=true and the clean
        // streak hasn't reached the lowering threshold: no change either way.
        Assert.False(controller.TryAdjustAuto(10, 10, true, true, T0.AddSeconds(10)));
        Assert.Equal(11025u, controller.EffectiveFrames);
    }

    [Fact]
    public void SetupLatencyMin_lab_uses_352_otherwise_folklore_11025()
    {
        Assert.Equal(352u, LatencyAutoController.SetupLatencyMin(352));
        Assert.Equal(2112u, LatencyAutoController.SetupLatencyMin(2112));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(11025));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(22050));
        Assert.Equal(11025u, LatencyAutoController.SetupLatencyMin(88200));
    }

    [Fact]
    public void SetupLatencyMax_at_least_88200()
    {
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(352));
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(11025));
        Assert.Equal(88200u, LatencyAutoController.SetupLatencyMax(88200));
    }

    [Fact]
    public void SetupLatencyMax_follows_L_above_the_ceiling()
    {
        var above = 88200u + LatencyAutoController.StepFrames;
        Assert.Equal(above, LatencyAutoController.SetupLatencyMax(above));
    }

    [Fact]
    public void ClampEffectiveFrames_packet_floor()
    {
        Assert.Equal(352u, LatencyAutoController.ClampEffectiveFrames(0));
        Assert.Equal(352u, LatencyAutoController.ClampEffectiveFrames(100));
        Assert.Equal(11025u, LatencyAutoController.ClampEffectiveFrames(11025));
    }
}

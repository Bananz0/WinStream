using System.Buffers.Binary;
using System.Net;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class PtpClockTests
{
    private static readonly IPAddress Peer = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Stranger = IPAddress.Parse("10.0.0.99");

    [Fact]
    public void ClockIdFromDeviceId_inserts_fffe_eui64()
    {
        var id = PtpClock.ClockIdFromDeviceId("AA:BB:CC:DD:EE:FF");
        Assert.Equal(0xAABBCCFFFEDDEEFFUL, id);
    }

    [Fact]
    public void TwoStep_Sync_FollowUp_stays_unlocked_until_three_samples_agree()
    {
        var clock = new PtpClock(0x1111);
        const ulong master = 0x001FF3A0F3B30008UL;
        const long arrivalNs = 1_000_000_000L;

        // First cycle: no lock yet, and the un-vetted clock id is not adopted.
        LockCycle(clock, master, seq: 1, masterNs: 540_000_000_000_000L, arrivalNs);
        Assert.False(clock.IsLocked);
        Assert.Equal(0UL, clock.MasterClockId);

        // Second cycle: still short of the 3-sample requirement.
        LockCycle(clock, master, seq: 2, masterNs: 540_000_000_001_000L, arrivalNs);
        Assert.False(clock.IsLocked);
    }

    [Fact]
    public void TwoStep_Sync_FollowUp_locks_after_three_agreeing_samples_and_advances_NowNanoseconds()
    {
        var clock = new PtpClock(0x1111);
        const ulong master = 0x001FF3A0F3B30008UL;
        const long masterNs = 540_000_000_000_000L;
        const long arrivalNs = 1_000_000_000L;

        LockCycle(clock, master, seq: 1, masterNs, arrivalNs);
        LockCycle(clock, master, seq: 2, masterNs, arrivalNs);
        Assert.False(clock.IsLocked);

        LockCycle(clock, master, seq: 3, masterNs, arrivalNs);
        Assert.True(clock.IsLocked);
        Assert.Equal(master, clock.MasterClockId);
        Assert.Equal(Peer, clock.LockedPeerAddress);
        Assert.True(clock.NowNanoseconds > (ulong)masterNs - 2_000_000_000UL);
    }

    [Fact]
    public void Lock_requires_the_agreeing_samples_to_come_from_the_same_peer()
    {
        var clock = new PtpClock(0x1111);
        const ulong master = 0x001FF3A0F3B30008UL;
        const long masterNs = 540_000_000_000_000L;
        const long arrivalNs = 1_000_000_000L;

        LockCycle(clock, master, seq: 1, masterNs, arrivalNs, address: Peer);
        LockCycle(clock, master, seq: 2, masterNs, arrivalNs, address: Stranger);
        LockCycle(clock, master, seq: 3, masterNs, arrivalNs, address: Peer);

        // Only 2 of the 3 samples came from the same address, so no lock yet.
        Assert.False(clock.IsLocked);
    }

    [Fact]
    public void FollowUp_with_mismatched_sequence_is_ignored()
    {
        var clock = new PtpClock(1);
        clock.HandleIncomingForTests(
            PtpClock.BuildTwoStepSyncForTests(9, sequence: 1),
            100,
            Peer);
        clock.HandleIncomingForTests(
            PtpClock.BuildFollowUpForTests(9, sequence: 2, timestampNanoseconds: 999),
            100,
            Peer);
        Assert.False(clock.IsLocked);
    }

    [Fact]
    public void SetOffsetForTests_exposes_NowNanoseconds()
    {
        var clock = new PtpClock(1);
        clock.SetOffsetForTests(offsetNs: 5_000_000_000L, masterClockId: 42);
        Assert.True(clock.IsLocked);
        Assert.Equal(42UL, clock.MasterClockId);
        Assert.True(clock.NowNanoseconds >= 5_000_000_000UL);
    }

    [Fact]
    public void ApplyOffset_rejects_spike_keeps_ema()
    {
        var clock = new PtpClock(1);
        clock.SetOffsetForTests(offsetNs: 1_000_000_000L, masterClockId: 7, peerAddress: Peer);

        // Steady sample near the EMA — accepted (α=0.2 → +2 ms toward +10 ms).
        clock.ApplyOffsetForTests(masterNs: 1_010_000_000L, localNs: 0, sourceAddress: Peer);
        Assert.Equal(0, clock.SpikesRejectedForTests);
        Assert.Equal(1_002_000_000L, clock.OffsetNanosecondsForTests);

        var before = clock.NowNanoseconds;

        // 200 ms jump vs EMA — rejected.
        clock.ApplyOffsetForTests(masterNs: 1_200_000_000L, localNs: 0, sourceAddress: Peer);
        Assert.Equal(1, clock.SpikesRejectedForTests);
        Assert.Equal(1_002_000_000L, clock.OffsetNanosecondsForTests);

        // Offset must not jump by ~200 ms.
        var after = clock.NowNanoseconds;
        var driftMs = Math.Abs((long)after - (long)before) / 1_000_000.0;
        Assert.True(driftMs < 50, $"Unexpected drift {driftMs} ms after rejected spike");
    }

    [Fact]
    public void Locked_clock_ignores_samples_from_an_unrecognized_peer()
    {
        var clock = new PtpClock(1);
        clock.SetOffsetForTests(offsetNs: 1_000_000_000L, masterClockId: 7, peerAddress: Peer);

        // A stray device on the LAN sends a plausible-looking two-step Sync/Follow_Up
        // for a different grandmaster; it must never reach the offset smoother.
        clock.HandleIncomingForTests(
            PtpClock.BuildTwoStepSyncForTests(masterClockId: 999, sequence: 1),
            arrivalNs: 0,
            sourceAddress: Stranger);
        clock.HandleIncomingForTests(
            PtpClock.BuildFollowUpForTests(masterClockId: 999, sequence: 1, timestampNanoseconds: 5_000_000_000L),
            arrivalNs: 0,
            sourceAddress: Stranger);

        Assert.Equal(0, clock.SpikesRejectedForTests);
        Assert.Equal(1_000_000_000L, clock.OffsetNanosecondsForTests);
        Assert.Equal(7UL, clock.MasterClockId);
    }

    [Fact]
    public void Reseeds_after_eight_consistent_rejections_from_the_locked_peer_and_raises_event()
    {
        var clock = new PtpClock(1);
        clock.SetOffsetForTests(offsetNs: 0L, masterClockId: 7, peerAddress: Peer);

        PtpReseedEventArgs? raised = null;
        clock.Reseeded += (_, args) => raised = args;

        // The locked peer's own timeline jumped by 500 ms; every sample disagrees
        // with the old EMA by the same amount, so they mutually agree with each other.
        const long newOffset = 500_000_000L;
        for (var i = 0; i < 7; i++)
        {
            clock.ApplyOffsetForTests(masterNs: newOffset, localNs: 0, sourceAddress: Peer, masterClockId: 7);
            Assert.Null(raised);
            Assert.Equal(0L, clock.OffsetNanosecondsForTests);
        }

        clock.ApplyOffsetForTests(masterNs: newOffset, localNs: 0, sourceAddress: Peer, masterClockId: 7);

        Assert.NotNull(raised);
        Assert.Equal(0L, raised!.PreviousOffsetNanoseconds);
        Assert.Equal(newOffset, raised.NewOffsetNanoseconds);
        Assert.Equal(7UL, raised.MasterClockId);
        Assert.Equal(newOffset, clock.OffsetNanosecondsForTests);
    }

    [Fact]
    public void ReadTimestamp_decodes_seconds_and_nanos()
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(6), 345);
        Assert.Equal(12_000_000_345L, PtpClock.ReadTimestamp(buffer));
    }

    private static void LockCycle(
        PtpClock clock,
        ulong master,
        ushort seq,
        long masterNs,
        long arrivalNs,
        IPAddress? address = null)
    {
        address ??= Peer;
        clock.HandleIncomingForTests(
            PtpClock.BuildTwoStepSyncForTests(master, seq),
            arrivalNs,
            address);
        clock.HandleIncomingForTests(
            PtpClock.BuildFollowUpForTests(master, seq, masterNs),
            arrivalNs,
            address);
    }
}

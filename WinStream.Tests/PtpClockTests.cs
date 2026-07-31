using System.Buffers.Binary;
using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class PtpClockTests
{
    [Fact]
    public void ClockIdFromDeviceId_inserts_fffe_eui64()
    {
        var id = PtpClock.ClockIdFromDeviceId("AA:BB:CC:DD:EE:FF");
        Assert.Equal(0xAABBCCFFFEDDEEFFUL, id);
    }

    [Fact]
    public void TwoStep_Sync_FollowUp_locks_and_advances_NowNanoseconds()
    {
        var clock = new PtpClock(0x1111);
        const ulong master = 0x001FF3A0F3B30008UL;
        const ushort seq = 7;
        const long masterNs = 540_000_000_000_000L;
        const long arrivalNs = 1_000_000_000L;

        clock.HandleIncomingForTests(
            PtpClock.BuildTwoStepSyncForTests(master, seq),
            arrivalNs);
        Assert.False(clock.IsLocked);
        Assert.Equal(master, clock.MasterClockId);

        clock.HandleIncomingForTests(
            PtpClock.BuildFollowUpForTests(master, seq, masterNs),
            arrivalNs);
        Assert.True(clock.IsLocked);
        Assert.True(clock.NowNanoseconds > (ulong)masterNs - 2_000_000_000UL);
    }

    [Fact]
    public void FollowUp_with_mismatched_sequence_is_ignored()
    {
        var clock = new PtpClock(1);
        clock.HandleIncomingForTests(
            PtpClock.BuildTwoStepSyncForTests(9, sequence: 1),
            100);
        clock.HandleIncomingForTests(
            PtpClock.BuildFollowUpForTests(9, sequence: 2, timestampNanoseconds: 999),
            100);
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
    public void ReadTimestamp_decodes_seconds_and_nanos()
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(6), 345);
        Assert.Equal(12_000_000_345L, PtpClock.ReadTimestamp(buffer));
    }
}

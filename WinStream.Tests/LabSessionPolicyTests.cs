using WinStream.Core;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class LabSessionPolicyTests
{
    [Fact]
    public void Blocks_second_receiver_only_in_Lab()
    {
        Assert.False(LabSessionPolicy.BlocksAdditionalReceiver(
            PlaybackResponsiveness.LabPacket,
            isFirstSession: true));
        Assert.True(LabSessionPolicy.BlocksAdditionalReceiver(
            PlaybackResponsiveness.LabPacket,
            isFirstSession: false));
        Assert.False(LabSessionPolicy.BlocksAdditionalReceiver(
            PlaybackResponsiveness.Experimental,
            isFirstSession: false));
        Assert.False(LabSessionPolicy.BlocksAdditionalReceiver(
            PlaybackResponsiveness.Auto,
            isFirstSession: false));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Blocks_live_switch_to_Lab_only_for_multi_room(int sessionCount, bool blocked) =>
        Assert.Equal(
            blocked,
            LabSessionPolicy.BlocksQualityApply(PlaybackResponsiveness.LabPacket, sessionCount));

    [Fact]
    public void Refusal_message_uses_the_name_the_dropdown_shows()
    {
        // The user picked "Extreme (~8 ms)"; telling them "Lab latency mode" was
        // refused names a preset that is not in the list.
        Assert.Contains("Extreme", LabSessionPolicy.MultiRoomBlockedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Lab", LabSessionPolicy.MultiRoomBlockedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Allows_live_switch_to_non_Lab_presets_with_many_sessions()
    {
        Assert.False(LabSessionPolicy.BlocksQualityApply(PlaybackResponsiveness.Auto, 3));
        Assert.False(LabSessionPolicy.BlocksQualityApply(PlaybackResponsiveness.Experimental, 3));
        Assert.False(LabSessionPolicy.BlocksQualityApply(PlaybackResponsiveness.MostStable, 3));
    }
}

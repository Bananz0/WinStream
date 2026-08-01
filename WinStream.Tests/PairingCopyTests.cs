using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Pairing copy is the only place the app explains why a receiver keeps asking for
/// approval, so the honesty has to survive edits.
/// </summary>
public class PairingCopyTests
{
    [Fact]
    public void The_prompt_names_both_secrets_the_receiver_may_ask_for()
    {
        Assert.Contains("AirPlay code", PairingCopy.PromptBody);
        Assert.Contains("password", PairingCopy.PromptBody);
    }

    [Fact]
    public void The_prompt_states_the_cost_of_skipping()
    {
        Assert.Contains(PairingCopy.SkipButton, PairingCopy.PromptBody);
        Assert.Contains("approve each session", PairingCopy.PromptBody);
    }

    [Fact]
    public void The_buttons_say_which_one_trusts_the_PC()
    {
        Assert.Contains("Trust", PairingCopy.TrustButton);
        Assert.Contains("every time", PairingCopy.SkipButton);
    }

    [Fact]
    public void The_transient_warning_explains_the_state_and_the_way_out()
    {
        Assert.Contains("temporary pairing", PairingCopy.TransientBody);
        Assert.Contains("approve each session", PairingCopy.TransientBody);
        Assert.Contains("AirPlay code", PairingCopy.TransientBody);
    }

    [Fact]
    public void The_transient_status_stays_short_enough_for_a_device_row()
    {
        Assert.Equal("Temporary pairing", PairingCopy.TransientStatus);
        Assert.False(string.IsNullOrWhiteSpace(PairingCopy.TransientTitle));
    }
}

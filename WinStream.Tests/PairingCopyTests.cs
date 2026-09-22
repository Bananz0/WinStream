using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Pairing copy is the only place the app explains why a receiver keeps asking for
/// approval, so the honesty has to survive edits.
/// </summary>
public class PairingCopyTests
{
    [Fact]
    public void The_code_prompt_names_the_code_and_not_the_password()
    {
        Assert.Contains("AirPlay code", PairingCopy.PromptBody);
        Assert.DoesNotContain("password", PairingCopy.PromptBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_password_prompt_names_system_settings_and_storage()
    {
        Assert.Equal("Enter AirPlay password", PairingCopy.PasswordPromptTitle);
        Assert.Contains("password", PairingCopy.PasswordPromptBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System Settings", PairingCopy.PasswordPromptBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("encrypted", PairingCopy.PasswordPromptBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Connect", PairingCopy.PasswordButton);
        Assert.Equal("Cancel", PairingCopy.PasswordCancelButton);
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

    [Fact]
    public void Forget_copy_names_the_action_and_the_re_prompt()
    {
        Assert.Equal("Forget pairing", PairingCopy.ForgetButton);
        Assert.Contains("AirPlay code or password", PairingCopy.ForgetDoneBody);
        Assert.Contains("No saved pairing", PairingCopy.ForgetNothingBody);
    }
}

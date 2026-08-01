using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SessionEndIntentTests
{
    [Fact]
    public void User_disconnect_that_empties_the_map_is_user_requested()
    {
        Assert.True(SessionEndIntent.UserRequested(
            userDisconnectApi: true,
            sessionsRemain: false));
    }

    [Fact]
    public void Partial_user_disconnect_that_leaves_rooms_is_not_user_requested()
    {
        Assert.False(SessionEndIntent.UserRequested(
            userDisconnectApi: true,
            sessionsRemain: true));
    }

    [Fact]
    public void Non_user_empty_map_is_not_user_requested()
    {
        Assert.False(SessionEndIntent.UserRequested(
            userDisconnectApi: false,
            sessionsRemain: false));
    }

    [Fact]
    public void Non_user_with_sessions_remaining_is_not_user_requested()
    {
        Assert.False(SessionEndIntent.UserRequested(
            userDisconnectApi: false,
            sessionsRemain: true));
    }
}

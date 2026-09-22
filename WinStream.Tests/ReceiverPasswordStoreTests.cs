using WinStream.Core.Persistence;

namespace WinStream.Tests;

public class ReceiverPasswordStoreTests
{
    [Fact]
    public void Round_trips_a_password_under_the_same_key()
    {
        using var directory = new TempDirectory();
        new ReceiverPasswordStore(directory.Path).Save("receiver-a", "hunter2");

        Assert.True(new ReceiverPasswordStore(directory.Path).TryGet("receiver-a", out var password));
        Assert.Equal("hunter2", password);
    }

    [Fact]
    public void Remove_clears_only_that_receiver()
    {
        using var directory = new TempDirectory();
        var store = new ReceiverPasswordStore(directory.Path);
        store.Save("receiver-a", "a");
        store.Save("receiver-b", "b");

        store.Remove("receiver-a");

        Assert.False(store.TryGet("receiver-a", out _));
        Assert.True(store.TryGet("receiver-b", out var kept));
        Assert.Equal("b", kept);
    }

    [Fact]
    public void Missing_keys_return_false()
    {
        using var directory = new TempDirectory();
        Assert.False(new ReceiverPasswordStore(directory.Path).TryGet("missing", out var password));
        Assert.Equal(string.Empty, password);
    }
}

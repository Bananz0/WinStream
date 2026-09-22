using WinStream.Core.Persistence;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class ReceiverPasswordResolverTests
{
    [Fact]
    public async Task Open_receiver_neither_reads_store_nor_prompts()
    {
        var store = new FakeReceiverPasswordStore();
        var prompted = false;

        var result = await ReceiverPasswordResolver.ResolveAsync(
            store,
            "receiver-a",
            requiresPassword: false,
            (_, _) =>
            {
                prompted = true;
                return Task.FromResult<string?>("unused");
            });

        Assert.Null(result);
        Assert.False(prompted);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Stored_password_wins_without_prompting()
    {
        var store = new FakeReceiverPasswordStore();
        store.Save("receiver-a", "stored");
        var prompted = false;

        var result = await ReceiverPasswordResolver.ResolveAsync(
            store,
            "receiver-a",
            requiresPassword: true,
            (_, _) =>
            {
                prompted = true;
                return Task.FromResult<string?>("prompted");
            });

        Assert.Equal("stored", result);
        Assert.False(prompted);
    }

    [Fact]
    public async Task Prompted_password_is_trimmed()
    {
        var result = await ReceiverPasswordResolver.ResolveAsync(
            new FakeReceiverPasswordStore(),
            "receiver-a",
            requiresPassword: true,
            (_, _) => Task.FromResult<string?>("  prompted  "));

        Assert.Equal("prompted", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Missing_password_throws_typed_failure(string? prompted)
    {
        var exception = await Assert.ThrowsAsync<ReceiverPasswordRequiredException>(() =>
            ReceiverPasswordResolver.ResolveAsync(
                new FakeReceiverPasswordStore(),
                "receiver-a",
                requiresPassword: true,
                (_, _) => Task.FromResult(prompted)));

        Assert.Equal(
            ConnectionFailureCopy.PasswordRequiredRow,
            ConnectionFailureCopy.DeviceRow(exception));
        Assert.Equal(
            ConnectionFailureCopy.PasswordRequiredDetail,
            ConnectionFailureCopy.Detail(exception));
    }

    [Fact]
    public async Task A_required_password_with_no_wired_prompt_throws_typed_failure()
    {
        await Assert.ThrowsAsync<ReceiverPasswordRequiredException>(() =>
            ReceiverPasswordResolver.ResolveAsync(
                new FakeReceiverPasswordStore(),
                "receiver-a",
                requiresPassword: true,
                promptAsync: null));
    }

    [Fact]
    public async Task Cancelling_the_connect_stops_waiting_even_if_the_prompt_task_never_completes()
    {
        var neverCompletes = new TaskCompletionSource<string?>();
        using var cancellation = new CancellationTokenSource();

        var resolve = ReceiverPasswordResolver.ResolveAsync(
            new FakeReceiverPasswordStore(),
            "receiver-a",
            requiresPassword: true,
            (_, _) => neverCompletes.Task,
            cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve);
    }

    [Fact]
    public void Stored_only_lookup_never_prompts()
    {
        var store = new FakeReceiverPasswordStore();
        store.Save("receiver-a", "stored");

        Assert.Equal(
            "stored",
            ReceiverPasswordResolver.StoredOrNull(store, "receiver-a", requiresPassword: true));
        Assert.Null(
            ReceiverPasswordResolver.StoredOrNull(store, "receiver-a", requiresPassword: false));
        Assert.Null(
            ReceiverPasswordResolver.StoredOrNull(store, "missing", requiresPassword: true));
    }

    private sealed class FakeReceiverPasswordStore : IReceiverPasswordStore
    {
        private readonly Dictionary<string, string> _passwords =
            new(StringComparer.OrdinalIgnoreCase);

        public int ReadCount { get; private set; }

        public bool TryGet(string receiverKey, out string password)
        {
            ReadCount++;
            return _passwords.TryGetValue(receiverKey, out password!);
        }

        public void Save(string receiverKey, string password) =>
            _passwords[receiverKey] = password;

        public void Remove(string receiverKey) => _passwords.Remove(receiverKey);
    }
}

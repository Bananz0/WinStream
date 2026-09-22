using WinStream.Core;
using WinStream.Core.Audio;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SessionRebuildTests
{
    [Fact]
    public async Task Disposes_the_old_session_before_building_the_replacement()
    {
        // AirPlay 2 binds PTP 319/320 process-wide. Building first would double-bind.
        var order = new List<string>();
        var retired = new RecordingSession("old", order);

        await SessionRebuild.ReplaceAsync(
            retired,
            () =>
            {
                order.Add("build");
                return new RecordingSession("new", order);
            },
            volumeDb: -10f);

        Assert.Equal(
            new[] { "old:dispose", "build", "new:volume", "new:connect" },
            order);
    }

    [Fact]
    public async Task Seeds_volume_before_connect_so_setup_carries_the_user_level()
    {
        var order = new List<string>();
        var replacement = new RecordingSession("new", order);

        await SessionRebuild.ReplaceAsync(
            new RecordingSession("old", order),
            () => replacement,
            volumeDb: -6.5f);

        Assert.Equal(-6.5f, replacement.VolumeDb);
        Assert.True(
            order.IndexOf("new:volume") < order.IndexOf("new:connect"),
            "volume must be applied before connect");
    }

    [Fact]
    public async Task Returns_the_replacement_the_factory_produced()
    {
        var order = new List<string>();
        var replacement = new RecordingSession("new", order);

        var result = await SessionRebuild.ReplaceAsync(
            new RecordingSession("old", order),
            () => replacement,
            volumeDb: 0f);

        Assert.Same(replacement, result);
    }

    [Fact]
    public async Task A_failing_connect_propagates_so_the_caller_can_drop_the_entry()
    {
        var order = new List<string>();
        var replacement = new RecordingSession("new", order) { ConnectFailure = new IOException("refused") };

        await Assert.ThrowsAsync<IOException>(() => SessionRebuild.ReplaceAsync(
            new RecordingSession("old", order),
            () => replacement,
            volumeDb: 0f));

        // The old session is already gone by then — the caller must not keep the entry.
        Assert.Contains("old:dispose", order);
    }

    [Fact]
    public async Task A_failing_factory_still_leaves_the_old_session_disposed()
    {
        var order = new List<string>();
        var retired = new RecordingSession("old", order);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionRebuild.ReplaceAsync(
            retired,
            () => throw new InvalidOperationException("no protocol"),
            volumeDb: 0f));

        Assert.True(retired.Disposed);
    }

    [Fact]
    public async Task Rejects_missing_arguments()
    {
        var order = new List<string>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionRebuild.ReplaceAsync(null!, () => new RecordingSession("new", order), 0f));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SessionRebuild.ReplaceAsync(new RecordingSession("old", order), null!, 0f));
    }

    [Fact]
    public async Task Passes_the_cancellation_token_through_to_the_replacement()
    {
        using var cts = new CancellationTokenSource();
        var order = new List<string>();
        var replacement = new RecordingSession("new", order);

        await SessionRebuild.ReplaceAsync(
            new RecordingSession("old", order),
            () => replacement,
            volumeDb: 0f,
            cts.Token);

        Assert.Equal(cts.Token, replacement.ConnectToken);
    }

    private sealed class RecordingSession(string name, List<string> order) : IAirPlaySession
    {
        public event EventHandler<SessionStateChanged>? StateChanged
        {
            add { }
            remove { }
        }

        public string ReceiverId => name;

        public SessionState State => SessionState.Disconnected;

        public uint EffectiveLatencyFrames => 0;

        public bool Disposed { get; private set; }

        public float VolumeDb { get; private set; }

        public CancellationToken ConnectToken { get; private set; }

        public Exception? ConnectFailure { get; init; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            order.Add($"{name}:connect");
            ConnectToken = cancellationToken;
            return ConnectFailure is null ? Task.CompletedTask : Task.FromException(ConnectFailure);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            order.Add($"{name}:disconnect");
            return Task.CompletedTask;
        }

        public void SetEffectiveLatencyFrames(uint frames) => order.Add($"{name}:latency");

        public void SetAudioFidelity(AudioFidelity fidelity) => order.Add($"{name}:fidelity");

        public void SubmitPcm(
            ReadOnlyMemory<byte> pcm,
            AudioFormat format,
            uint? sharedMediaTimestamp = null)
        {
        }

        public Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default)
        {
            order.Add($"{name}:volume");
            VolumeDb = volumeDb;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            order.Add($"{name}:dispose");
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

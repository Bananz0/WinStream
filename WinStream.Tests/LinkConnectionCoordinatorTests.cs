using WinStream.Core.Audio;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkConnectionCoordinatorTests
{
    [Fact]
    public async Task Blank_pin_is_rejected_before_touching_the_network()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("192.168.1.50", "   ");

        Assert.Equal(LinkConnectStatus.MissingPin, result.Status);
        Assert.Empty(harness.Handshakes);
        Assert.Empty(harness.Credentials.Saved);
    }

    [Fact]
    public async Task Unparseable_host_is_rejected_before_touching_the_network()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("host:abc", "1234");

        Assert.Equal(LinkConnectStatus.InvalidTarget, result.Status);
        Assert.Empty(harness.Handshakes);
    }

    [Fact]
    public async Task Successful_connect_handshakes_on_the_derived_control_port()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("192.168.1.50:50000", " 4321 ");

        Assert.True(result.IsConnected);
        var handshake = Assert.Single(harness.Handshakes);
        Assert.Equal(("192.168.1.50", 50001, "4321"), handshake);
        Assert.Equal("192.168.1.50:50000", coordinator.ConnectedTarget!.Key);
        Assert.Equal(LinkSessionState.Streaming, coordinator.State);
    }

    [Fact]
    public async Task Successful_connect_saves_the_trimmed_pin_under_the_target_key()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        await coordinator.ConnectAsync("192.168.1.50", " 4321 ");

        Assert.Equal(
            new[] { ($"192.168.1.50:{Wsl1Constants.DefaultMediaPort}", "4321") },
            harness.Credentials.Saved);
    }

    [Fact]
    public async Task Rejected_pin_never_starts_capture_or_persists_the_pin()
    {
        var harness = new Harness { HandshakeResult = false };
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkConnectStatus.PinRejected, result.Status);
        Assert.Empty(harness.Credentials.Saved);
        Assert.Empty(harness.Captures);
        Assert.Empty(harness.Sessions);
        Assert.Equal(LinkSessionState.Disconnected, coordinator.State);
    }

    [Fact]
    public async Task Unreachable_companion_reports_the_transport_failure()
    {
        var harness = new Harness
        {
            HandshakeThrows = new IOException("connection refused")
        };
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkConnectStatus.TransportFailed, result.Status);
        Assert.Equal("connection refused", result.Detail);
        Assert.Empty(harness.Captures);
    }

    [Fact]
    public async Task Capture_that_refuses_to_start_tears_down_the_session_and_control_channel()
    {
        var harness = new Harness { CaptureStartThrows = true };
        await using var coordinator = harness.Create();

        var result = await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkConnectStatus.CaptureFailed, result.Status);
        Assert.True(Assert.Single(harness.Captures).Disposed);
        Assert.True(Assert.Single(harness.Sessions).Disposed);
        Assert.True(Assert.Single(harness.Channels).Disposed);
        Assert.Equal(LinkSessionState.Disconnected, coordinator.State);
        Assert.Null(coordinator.ConnectedTarget);
    }

    [Fact]
    public async Task Streaming_is_announced_to_the_companion_over_the_control_channel()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        await coordinator.ConnectAsync("192.168.1.50:50000", "0000");

        Assert.Equal(new[] { (50000, 48000) }, harness.Channels[0].Starts);
    }

    [Fact]
    public async Task Disconnect_sends_stop_before_closing_the_control_channel()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        await coordinator.DisconnectAsync();

        Assert.Equal(1, harness.Channels[0].Stops);
        Assert.True(harness.Channels[0].Disposed);
    }

    [Fact]
    public async Task Receiver_telemetry_is_available_while_streaming()
    {
        var expected = new LinkReceiverTelemetry(0, 3, 4, 5000);
        var harness = new Harness { Telemetry = expected };
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(expected, await coordinator.QueryTelemetryAsync());
    }

    [Fact]
    public async Task Telemetry_is_null_when_no_companion_is_connected()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();

        Assert.Null(await coordinator.QueryTelemetryAsync());
    }

    [Fact]
    public async Task Captured_frames_are_pumped_into_the_session()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        harness.Captures[0].RaiseFrame(new byte[8]);

        Assert.Equal(1, harness.Sessions[0].SubmittedFrames);
        Assert.Equal(1, coordinator.PacketsSent);
    }

    [Fact]
    public async Task Reconnect_disposes_the_previous_capture_and_session_first()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        await coordinator.ConnectAsync("192.168.1.51", "0000");

        Assert.Equal(2, harness.Captures.Count);
        Assert.True(harness.Captures[0].Disposed);
        Assert.True(harness.Sessions[0].Disposed);
        Assert.True(harness.Channels[0].Disposed);
        Assert.False(harness.Captures[1].Disposed);
        Assert.False(harness.Channels[1].Disposed);
        Assert.Equal("192.168.1.51", coordinator.ConnectedTarget!.Host);
    }

    [Fact]
    public async Task Frames_from_an_orphaned_capture_never_reach_the_new_session()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");
        var orphaned = harness.Captures[0];

        await coordinator.ConnectAsync("192.168.1.51", "0000");
        orphaned.RaiseFrame(new byte[8]);

        Assert.Equal(0, harness.Sessions[1].SubmittedFrames);
    }

    [Fact]
    public async Task Disconnect_releases_both_halves_and_clears_the_target()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        await coordinator.DisconnectAsync();

        Assert.True(harness.Captures[0].Disposed);
        Assert.True(harness.Sessions[0].Disposed);
        Assert.Null(coordinator.ConnectedTarget);
        Assert.Equal(LinkSessionState.Disconnected, coordinator.State);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_stops_the_stream()
    {
        var harness = new Harness();
        var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.True(harness.Captures[0].Disposed);
        Assert.Equal(1, harness.Sessions[0].DisposeCount);
    }

    [Fact]
    public async Task Connect_after_dispose_throws_instead_of_leaking_capture()
    {
        var harness = new Harness();
        var coordinator = harness.Create();
        await coordinator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.ConnectAsync("192.168.1.50", "0000"));
    }

    [Fact]
    public async Task Capture_fault_after_start_is_recorded_for_the_ui()
    {
        var harness = new Harness();
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        harness.Captures[0].RaiseFailure(new InvalidOperationException("device lost"));

        Assert.Equal("device lost", coordinator.CaptureFault!.Message);
    }

    [Fact]
    public async Task Default_loopback_capture_can_never_report_a_vad_quality()
    {
        var harness = new Harness { CaptureIsOwnedVad = false, MeasuredContributionMs = 2 };
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkCaptureQuality.LegacyLoopback, coordinator.CaptureQuality);
        Assert.False(coordinator.IsSlaCaptureCapable);
    }

    [Fact]
    public async Task Owned_vad_reports_measuring_until_callbacks_are_observed()
    {
        var harness = new Harness { CaptureIsOwnedVad = true, MeasuredContributionMs = 0 };
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkCaptureQuality.VadMeasuring, coordinator.CaptureQuality);
    }

    [Fact]
    public async Task Owned_vad_over_budget_is_reported_separately_from_within_budget()
    {
        var harness = new Harness { CaptureIsOwnedVad = true, MeasuredContributionMs = 7 };
        await using var coordinator = harness.Create();
        await coordinator.ConnectAsync("192.168.1.50", "0000");

        Assert.Equal(LinkCaptureQuality.VadOverBudget, coordinator.CaptureQuality);
        Assert.False(coordinator.IsSlaCaptureCapable);
    }

    private sealed class Harness
    {
        public List<FakeLinkCaptureSource> Captures { get; } = new();

        public List<FakeLinkSession> Sessions { get; } = new();

        public List<(string Host, int ControlPort, string Pin)> Handshakes { get; } = new();

        public List<FakeLinkControlChannel> Channels { get; } = new();

        public FakeLinkCredentialStore Credentials { get; } = new();

        public bool HandshakeResult { get; init; } = true;

        public Exception? HandshakeThrows { get; init; }

        public bool CaptureStartThrows { get; init; }

        public bool CaptureIsOwnedVad { get; init; }

        public int MeasuredContributionMs { get; init; }

        public LinkReceiverTelemetry Telemetry { get; init; }

        public LinkConnectionCoordinator Create() => new(
            _ =>
            {
                var capture = new FakeLinkCaptureSource
                {
                    StartThrows = CaptureStartThrows,
                    IsOwnedWinStreamEndpoint = CaptureIsOwnedVad,
                    MeasuredCaptureContributionMilliseconds = MeasuredContributionMs
                };
                Captures.Add(capture);
                return capture;
            },
            () =>
            {
                var session = new FakeLinkSession();
                Sessions.Add(session);
                return session;
            },
            (target, pin, _) =>
            {
                Handshakes.Add((target.Host, target.ControlPort, pin));
                if (HandshakeThrows is not null)
                {
                    return Task.FromException<ILinkControlChannel?>(HandshakeThrows);
                }

                if (!HandshakeResult)
                {
                    return Task.FromResult<ILinkControlChannel?>(null);
                }

                var channel = new FakeLinkControlChannel { Telemetry = Telemetry };
                Channels.Add(channel);
                return Task.FromResult<ILinkControlChannel?>(channel);
            },
            Credentials);
    }

    private sealed class FakeLinkControlChannel : ILinkControlChannel
    {
        public List<(int MediaPort, int SampleRate)> Starts { get; } = new();

        public int Stops { get; private set; }

        public bool Disposed { get; private set; }

        public LinkReceiverTelemetry Telemetry { get; init; }

        public Task StartAsync(
            int mediaPort,
            AudioFormat format,
            CancellationToken cancellationToken = default)
        {
            Starts.Add((mediaPort, format.SampleRate));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Task<LinkReceiverTelemetry?> QueryTelemetryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LinkReceiverTelemetry?>(Telemetry);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLinkCaptureSource : ILinkCaptureSource
    {
        private static readonly AudioFormat Pcm48k = new(48000, 2, 16);

        public event EventHandler<AudioFrame>? FrameAvailable;

        public event EventHandler<Exception>? CaptureFailed;

        public event EventHandler? DeviceInvalidated;

        public bool StartThrows { get; init; }

        public bool Disposed { get; private set; }

        public bool IsCapturing { get; private set; }

        public AudioFormat? Format => Pcm48k;

        public string? EndpointId => "fake-endpoint";

        public double CurrentRms => 0;

        public bool IsSilent => true;

        public int EffectiveBufferMilliseconds => 3;

        public bool IsOwnedWinStreamEndpoint { get; init; }

        public int MeasuredCaptureContributionMilliseconds { get; init; }

        public bool IsSlaCaptureCapable =>
            IsOwnedWinStreamEndpoint &&
            LinkSlaEligibility.IsMeasuredCaptureSlaCapable(MeasuredCaptureContributionMilliseconds);

        public void RaiseFrame(byte[] pcm) =>
            FrameAvailable?.Invoke(this, new AudioFrame(pcm, Pcm48k, 1));

        public void RaiseFailure(Exception error) => CaptureFailed?.Invoke(this, error);

        public void RaiseDeviceInvalidated() => DeviceInvalidated?.Invoke(this, EventArgs.Empty);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (StartThrows)
            {
                throw new InvalidOperationException("capture rejected");
            }

            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLinkSession : ILinkSession
    {
        public event EventHandler<LinkSessionStateChanged>? StateChanged;

        public LinkSessionState State { get; private set; } = LinkSessionState.Disconnected;

        public string RemoteHost { get; private set; } = string.Empty;

        public int MediaPort { get; private set; }

        public long PacketsSent => SubmittedFrames;

        public int SubmittedFrames { get; private set; }

        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public Task ConnectAsync(
            string host,
            int mediaPort = Wsl1Constants.DefaultMediaPort,
            CancellationToken cancellationToken = default)
        {
            RemoteHost = host;
            MediaPort = mediaPort;
            var previous = State;
            State = LinkSessionState.Streaming;
            StateChanged?.Invoke(this, new LinkSessionStateChanged(previous, State, null));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = LinkSessionState.Disconnected;
            return Task.CompletedTask;
        }

        public void SubmitPcm(ReadOnlyMemory<byte> pcm, AudioFormat format, long timestampTicks) =>
            SubmittedFrames++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            State = LinkSessionState.Disconnected;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLinkCredentialStore : ILinkCredentialStore
    {
        public List<(string Key, string Pin)> Saved { get; } = new();

        public bool TryGetPin(string receiverKey, out string pin)
        {
            foreach (var entry in Saved)
            {
                if (entry.Key == receiverKey)
                {
                    pin = entry.Pin;
                    return true;
                }
            }

            pin = string.Empty;
            return false;
        }

        public void SavePin(string receiverKey, string pin) => Saved.Add((receiverKey, pin));

        public void Remove(string receiverKey) => Saved.RemoveAll(entry => entry.Key == receiverKey);
    }
}

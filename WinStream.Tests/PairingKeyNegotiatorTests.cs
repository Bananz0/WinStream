using System.Security.Cryptography;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

public class PairingKeyNegotiatorTests
{
    private static AirPlayControlKeys Keys(byte marker)
    {
        var buffer = new byte[32];
        Array.Fill(buffer, marker);
        return new AirPlayControlKeys(buffer, buffer, buffer, buffer, buffer);
    }

    private static PairingCredentials Complete(string id = "CLIENT") => new()
    {
        ClientPairingId = id,
        ClientSeedHex = new string('A', 64),
        AccessoryPairingId = "ACCESSORY",
        AccessoryPublicKeyHex = new string('C', 64)
    };

    private static Task<string?> Pin(CancellationToken _) => Task.FromResult<string?>("1234");

    private sealed class Recorder
    {
        public List<string> Steps { get; } = new();
        public PairingCredentials? Paired { get; private set; }
        public int Rejections { get; private set; }
        public int Resets { get; private set; }

        public void Reset() => Resets++;

        public PairingOptions Options(
            PairingCredentials? stored,
            Func<CancellationToken, Task<string?>>? requestPin) => new()
        {
            StoredCredentials = stored,
            RequestPinAsync = requestPin,
            OnPaired = credentials =>
            {
                Steps.Add("saved");
                Paired = credentials;
            },
            OnStoredCredentialsRejected = () =>
            {
                Steps.Add("rejected");
                Rejections++;
            }
        };
    }

    [Fact]
    public async Task Stored_credentials_verify_without_a_pin_prompt()
    {
        var recorder = new Recorder();
        var promptCalls = 0;
        var negotiator = new PairingKeyNegotiator(
            recorder.Options(Complete(), _ =>
            {
                promptCalls++;
                return Pin(default);
            }),
            recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (_, _) =>
            {
                recorder.Steps.Add("verify");
                return Task.FromResult(Keys(1));
            },
            (_, _) => throw new InvalidOperationException("setup must not run"),
            _ => throw new InvalidOperationException("transient must not run"),
            CancellationToken.None);

        Assert.Equal(["verify"], recorder.Steps);
        Assert.Equal(0, promptCalls);
        Assert.Equal(0, recorder.Resets);
    }

    [Fact]
    public async Task Incomplete_stored_credentials_go_straight_to_pair_setup()
    {
        var recorder = new Recorder();
        var verifyCalls = 0;
        var negotiator = new PairingKeyNegotiator(
            recorder.Options(new PairingCredentials { ClientPairingId = "partial" }, Pin),
            recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (_, _) =>
            {
                verifyCalls++;
                return Task.FromResult(Keys(2));
            },
            (_, _) =>
            {
                recorder.Steps.Add("setup");
                return Task.FromResult(Complete());
            },
            _ => throw new InvalidOperationException("transient must not run"),
            CancellationToken.None);

        Assert.Equal(["setup", "saved"], recorder.Steps);
        Assert.Equal(1, verifyCalls);
        Assert.Equal(0, recorder.Rejections);
    }

    [Fact]
    public async Task Rejected_stored_credentials_are_cleared_then_re_paired()
    {
        var recorder = new Recorder();
        var verifyCalls = 0;
        var negotiator = new PairingKeyNegotiator(
            recorder.Options(Complete("STALE"), Pin),
            recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (credentials, _) =>
            {
                verifyCalls++;
                return credentials.ClientPairingId == "STALE"
                    ? throw new CryptographicException("identity changed")
                    : Task.FromResult(Keys(3));
            },
            (_, _) => Task.FromResult(Complete("FRESH")),
            _ => throw new InvalidOperationException("transient must not run"),
            CancellationToken.None);

        Assert.Equal(["rejected", "saved"], recorder.Steps);
        Assert.Equal(2, verifyCalls);
        Assert.Equal("FRESH", recorder.Paired!.ClientPairingId);
        Assert.Equal(1, recorder.Resets);
    }

    [Fact]
    public async Task Verify_uses_the_credentials_setup_returned_without_a_reload()
    {
        var recorder = new Recorder();
        PairingCredentials? verified = null;
        var negotiator = new PairingKeyNegotiator(recorder.Options(null, Pin), recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (credentials, _) =>
            {
                verified = credentials;
                return Task.FromResult(Keys(4));
            },
            (_, _) => Task.FromResult(Complete("IN-MEMORY")),
            _ => throw new InvalidOperationException("transient must not run"),
            CancellationToken.None);

        Assert.Equal("IN-MEMORY", verified!.ClientPairingId);
        Assert.Same(recorder.Paired, verified);
    }

    [Fact]
    public async Task Skipping_the_pin_falls_back_to_transient()
    {
        var recorder = new Recorder();
        var negotiator = new PairingKeyNegotiator(recorder.Options(null, Pin), recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (_, _) => throw new InvalidOperationException("verify must not run"),
            (_, _) => throw new PairingPinSkippedException(),
            _ =>
            {
                recorder.Steps.Add("transient");
                return Task.FromResult(Keys(5));
            },
            CancellationToken.None);

        Assert.Equal(["transient"], recorder.Steps);
        Assert.Equal(1, recorder.Resets);
        Assert.Equal(0, recorder.Rejections);
    }

    [Fact]
    public async Task Cancelling_the_connect_aborts_instead_of_pairing_transiently()
    {
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var negotiator = new PairingKeyNegotiator(recorder.Options(null, Pin), recorder.Reset);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            negotiator.NegotiateAsync(
                (_, _) => throw new InvalidOperationException("verify must not run"),
                (_, _) => throw new OperationCanceledException(cts.Token),
                _ => throw new InvalidOperationException("transient must not run"),
                cts.Token));

        Assert.Equal(0, recorder.Resets);
    }

    [Fact]
    public async Task Cancelling_during_stored_verify_aborts_instead_of_re_pairing()
    {
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var negotiator = new PairingKeyNegotiator(
            recorder.Options(Complete(), Pin),
            recorder.Reset);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            negotiator.NegotiateAsync(
                (_, _) => throw new OperationCanceledException(cts.Token),
                (_, _) => throw new InvalidOperationException("setup must not run"),
                _ => throw new InvalidOperationException("transient must not run"),
                cts.Token));

        Assert.Equal(0, recorder.Rejections);
    }

    [Fact]
    public async Task A_failed_pair_setup_uses_transient_without_clearing_anything()
    {
        var recorder = new Recorder();
        var negotiator = new PairingKeyNegotiator(recorder.Options(null, Pin), recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (_, _) => throw new InvalidOperationException("verify must not run"),
            (_, _) => throw new CryptographicException("wrong AirPlay code"),
            _ =>
            {
                recorder.Steps.Add("transient");
                return Task.FromResult(Keys(6));
            },
            CancellationToken.None);

        // Setup never handed an identity to the store, so there is nothing stale to
        // clear — signalling a rejection here would drop an earlier good pairing.
        Assert.Equal(["transient"], recorder.Steps);
        Assert.Equal(0, recorder.Rejections);
        Assert.Equal(1, recorder.Resets);
    }

    [Fact]
    public async Task No_options_pairs_transiently()
    {
        var negotiator = new PairingKeyNegotiator(null, () => { });

        using var keys = await negotiator.NegotiateAsync(
            (_, _) => throw new InvalidOperationException("verify must not run"),
            (_, _) => throw new InvalidOperationException("setup must not run"),
            _ => Task.FromResult(Keys(7)),
            CancellationToken.None);

        Assert.Equal(32, keys.AudioSharedKey().Length);
    }

    [Fact]
    public async Task Without_a_pin_prompt_pair_setup_is_never_attempted()
    {
        var recorder = new Recorder();
        var negotiator = new PairingKeyNegotiator(
            recorder.Options(stored: null, requestPin: null),
            recorder.Reset);

        using var keys = await negotiator.NegotiateAsync(
            (_, _) => throw new InvalidOperationException("verify must not run"),
            (_, _) => throw new InvalidOperationException("setup must not run"),
            _ =>
            {
                recorder.Steps.Add("transient");
                return Task.FromResult(Keys(8));
            },
            CancellationToken.None);

        Assert.Equal(["transient"], recorder.Steps);
    }
}

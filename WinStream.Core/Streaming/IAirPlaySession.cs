using WinStream.Core.Audio;
using WinStream.Core.Network;

namespace WinStream.Core.Streaming;

public interface IAirPlaySession : IAsyncDisposable
{
    event EventHandler<SessionStateChanged>? StateChanged;

    string ReceiverId { get; }

    SessionState State { get; }

    /// <summary>Effective sync/announce playout offset in 44.1 kHz frames.</summary>
    uint EffectiveLatencyFrames { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the sync/announce latency offset. Mid-session Auto raises update announce
    /// only; SETUP latencyMin/Max are set at connect from SetupLatencyMin/Max(effective L).
    /// </summary>
    void SetEffectiveLatencyFrames(uint frames);

    /// <summary>PCM conversion policy for packetization (v1: HighFidelity ≡ Auto).</summary>
    void SetAudioFidelity(AudioFidelity fidelity);

    void SubmitPcm(
        ReadOnlyMemory<byte> pcm,
        AudioFormat format,
        uint? sharedMediaTimestamp = null);

    Task SetVolumeAsync(float volumeDb, CancellationToken cancellationToken = default);
}

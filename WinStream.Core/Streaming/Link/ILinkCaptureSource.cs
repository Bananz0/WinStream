using WinStream.Core.Audio;

namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Capture source for the Link path only. Adds the measurements the SLA policy needs
/// on top of <see cref="IAudioSource"/>; AirPlay capture never implements this.
/// </summary>
public interface ILinkCaptureSource : IAudioSource
{
    /// <summary>Client buffer the driver actually accepted, not the one requested.</summary>
    int EffectiveBufferMilliseconds { get; }

    /// <summary>True only for a WinStream-owned VAD endpoint.</summary>
    bool IsOwnedWinStreamEndpoint { get; }

    /// <summary>Rolling p95 of observed callback intervals; 0 until warmed up.</summary>
    int MeasuredCaptureContributionMilliseconds { get; }

    bool IsSlaCaptureCapable { get; }
}

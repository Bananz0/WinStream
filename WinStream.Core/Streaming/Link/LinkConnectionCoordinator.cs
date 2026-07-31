using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.Link;

namespace WinStream.Core.Streaming.Link;

public enum LinkConnectStatus
{
    Connected,
    MissingPin,
    InvalidTarget,
    PinRejected,
    TransportFailed,
    CaptureFailed
}

public sealed record LinkConnectResult(LinkConnectStatus Status, LinkTarget? Target, string? Detail = null)
{
    public bool IsConnected => Status == LinkConnectStatus.Connected;
}

/// <summary>
/// Owns the whole Link connect sequence — target parsing, PIN handshake, credential
/// persistence, capture/session/control lifetime, and frame pumping — so the window
/// only prompts and renders. Every collaborator is injected to keep this testable
/// without audio hardware or a companion on the LAN.
/// </summary>
public sealed class LinkConnectionCoordinator : IAsyncDisposable
{
    /// <summary>Returns null when the companion rejects the PIN; throws when unreachable.</summary>
    public delegate Task<ILinkControlChannel?> LinkControlConnect(
        LinkTarget target,
        string pin,
        CancellationToken cancellationToken);

    private static readonly AudioFormat DefaultFormat = new(
        Wsl1Constants.DefaultSampleRate,
        Wsl1Constants.DefaultChannels,
        16);

    private readonly Func<string?, ILinkCaptureSource> _captureFactory;
    private readonly Func<ILinkSession> _sessionFactory;
    private readonly LinkControlConnect _controlConnect;
    private readonly ILinkCredentialStore _credentials;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private ILinkCaptureSource? _capture;
    private ILinkSession? _session;
    private ILinkControlChannel? _control;
    private bool _disposed;

    public LinkConnectionCoordinator(
        Func<string?, ILinkCaptureSource> captureFactory,
        Func<ILinkSession> sessionFactory,
        LinkControlConnect controlConnect,
        ILinkCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(captureFactory);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(controlConnect);
        ArgumentNullException.ThrowIfNull(credentials);
        _captureFactory = captureFactory;
        _sessionFactory = sessionFactory;
        _controlConnect = controlConnect;
        _credentials = credentials;
    }

    public LinkSessionState State => _session?.State ?? LinkSessionState.Disconnected;

    public LinkTarget? ConnectedTarget { get; private set; }

    public long PacketsSent => _session?.PacketsSent ?? 0;

    public int CaptureBufferMilliseconds => _capture?.EffectiveBufferMilliseconds ?? 0;

    public bool IsOwnedWinStreamEndpoint => _capture?.IsOwnedWinStreamEndpoint ?? false;

    public int MeasuredCaptureContributionMilliseconds =>
        _capture?.MeasuredCaptureContributionMilliseconds ?? 0;

    public bool IsSlaCaptureCapable => _capture?.IsSlaCaptureCapable ?? false;

    /// <summary>Last capture fault after a successful start; cleared on reconnect.</summary>
    public Exception? CaptureFault { get; private set; }

    public LinkCaptureQuality CaptureQuality => LinkCaptureQualityPolicy.Evaluate(
        State == LinkSessionState.Streaming,
        IsOwnedWinStreamEndpoint,
        MeasuredCaptureContributionMilliseconds);

    public async Task<LinkConnectResult> ConnectAsync(
        string? hostText,
        string? pin,
        string? captureEndpointId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var trimmedPin = pin?.Trim();
        if (string.IsNullOrEmpty(trimmedPin))
        {
            return new LinkConnectResult(LinkConnectStatus.MissingPin, null);
        }

        if (!LinkTarget.TryParse(hostText, out var target))
        {
            return new LinkConnectResult(LinkConnectStatus.InvalidTarget, null);
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            ILinkControlChannel? control;
            try
            {
                control = await _controlConnect(target, trimmedPin, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new LinkConnectResult(LinkConnectStatus.TransportFailed, target, ex.Message);
            }

            if (control is null)
            {
                return new LinkConnectResult(LinkConnectStatus.PinRejected, target);
            }

            _credentials.SavePin(target.Key, trimmedPin);

            var session = _sessionFactory();
            try
            {
                await session.ConnectAsync(target.Host, target.MediaPort, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                await control.DisposeAsync().ConfigureAwait(false);
                return new LinkConnectResult(LinkConnectStatus.TransportFailed, target, ex.Message);
            }

            var capture = _captureFactory(captureEndpointId);
            capture.FrameAvailable += OnFrameAvailable;
            capture.CaptureFailed += OnCaptureFailed;
            try
            {
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);
                await control.StartAsync(
                        target.MediaPort,
                        capture.Format ?? DefaultFormat,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                capture.FrameAvailable -= OnFrameAvailable;
                capture.CaptureFailed -= OnCaptureFailed;
                await capture.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                await control.DisposeAsync().ConfigureAwait(false);
                return new LinkConnectResult(LinkConnectStatus.CaptureFailed, target, ex.Message);
            }

            _capture = capture;
            _session = session;
            _control = control;
            ConnectedTarget = target;
            CaptureFault = null;
            AppLog.Info(
                "link",
                $"Link streaming → {target.Key} endpoint={capture.EndpointId} " +
                $"ownedVad={capture.IsOwnedWinStreamEndpoint}");
            return new LinkConnectResult(LinkConnectStatus.Connected, target);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Receiver-reported health, or null when not streaming or the reply is unusable.</summary>
    public async Task<LinkReceiverTelemetry?> QueryTelemetryAsync(
        CancellationToken cancellationToken = default)
    {
        var control = _control;
        if (control is null)
        {
            return null;
        }

        try
        {
            return await control.QueryTelemetryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info("link", $"Link telemetry unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (_capture is not null)
        {
            _capture.FrameAvailable -= OnFrameAvailable;
            _capture.CaptureFailed -= OnCaptureFailed;
            await _capture.DisposeAsync().ConfigureAwait(false);
            _capture = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }

        if (_control is not null)
        {
            try
            {
                await _control.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A companion that already vanished must not block local teardown.
                AppLog.Info("link", $"Link STOP not delivered: {ex.GetType().Name}");
            }

            await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
        }

        ConnectedTarget = null;
    }

    private void OnFrameAvailable(object? sender, AudioFrame frame)
    {
        try
        {
            _session?.SubmitPcm(frame.Pcm, frame.Format, frame.TimestampTicks);
        }
        catch (Exception ex)
        {
            AppLog.Error("link", $"SubmitPcm failed: {ex.Message}");
        }
    }

    private void OnCaptureFailed(object? sender, Exception ex)
    {
        CaptureFault = ex;
        AppLog.Error("link", $"Link capture failed: {ex.Message}");
    }
}

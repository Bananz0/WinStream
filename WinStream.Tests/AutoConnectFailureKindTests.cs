using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

/// <summary>
/// Covers <see cref="AutoConnectFailureKindClassifier.Classify"/> — the retry
/// policy depends on this mapping never drifting from
/// <see cref="ConnectionFailureCopy"/>'s own 401/403/470 detection.
/// </summary>
public class AutoConnectFailureKindTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(470)]
    public void A_password_or_ACL_status_classifies_as_Config(int statusCode)
    {
        var exception = new RtspStatusException("SETUP session", statusCode, "rejected");

        Assert.Equal(AutoConnectFailureKind.Config, AutoConnectFailureKindClassifier.Classify(exception));
    }

    [Fact]
    public void A_SETUP_400_classifies_as_Silent_not_a_new_Prompt_kind()
    {
        var exception = new RtspStatusException(
            "SETUP session",
            400,
            "SETUP session failed with RTSP 400 Bad Request.");

        Assert.Equal(AutoConnectFailureKind.Silent, AutoConnectFailureKindClassifier.Classify(exception));
        Assert.True(AutoConnectFailureKindClassifier.IsSetupBadRequest(exception));
    }

    [Fact]
    public void A_400_from_a_different_method_is_not_the_SETUP_pattern()
    {
        var exception = new RtspStatusException("ANNOUNCE", 400, "failed");

        Assert.Equal(AutoConnectFailureKind.Other, AutoConnectFailureKindClassifier.Classify(exception));
        Assert.False(AutoConnectFailureKindClassifier.IsSetupBadRequest(exception));
    }

    [Fact]
    public void An_unrecognized_RtspStatusException_status_classifies_as_Other()
    {
        var exception = new RtspStatusException("SETUP session", 500, "server error");

        Assert.Equal(AutoConnectFailureKind.Other, AutoConnectFailureKindClassifier.Classify(exception));
    }

    [Fact]
    public void A_wrong_password_exception_type_classifies_as_Config()
    {
        var exception = new AirPlayPasswordRejectedException("rejected");

        Assert.Equal(AutoConnectFailureKind.Config, AutoConnectFailureKindClassifier.Classify(exception));
    }

    [Fact]
    public void A_password_shaped_message_classifies_as_Config_via_ConnectionFailureCopy_detection()
    {
        var exception = new InvalidOperationException(
            "Pair-setup error: authentication failed — the receiver rejected the AirPlay code or password.");

        Assert.Equal(AutoConnectFailureKind.Config, AutoConnectFailureKindClassifier.Classify(exception));
    }

    [Fact]
    public void A_network_ACL_message_classifies_as_Config()
    {
        var exception = new InvalidOperationException(
            "Receiver returned 403 Forbidden. Confirm AirPlay Receiver is enabled for Everyone.");

        Assert.Equal(AutoConnectFailureKind.Config, AutoConnectFailureKindClassifier.Classify(exception));
    }

    [Fact]
    public void A_connection_timeout_classifies_as_Silent()
    {
        Assert.Equal(
            AutoConnectFailureKind.Silent,
            AutoConnectFailureKindClassifier.Classify(new TimeoutException("dial timed out")));
    }

    [Fact]
    public void A_socket_exception_classifies_as_Silent()
    {
        // The single most common real-world "receiver unreachable" exception on
        // Windows (connection refused, host unreachable, etc.) — must retry
        // unbounded like any other Silent failure, not fall through to Other.
        Assert.Equal(
            AutoConnectFailureKind.Silent,
            AutoConnectFailureKindClassifier.Classify(
                new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.ConnectionRefused)));
    }

    [Fact]
    public void A_cancelled_connect_attempt_classifies_as_Silent()
    {
        Assert.Equal(
            AutoConnectFailureKind.Silent,
            AutoConnectFailureKindClassifier.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void A_WASAPI_style_failure_classifies_as_Other()
    {
        Assert.Equal(
            AutoConnectFailureKind.Other,
            AutoConnectFailureKindClassifier.Classify(new InvalidOperationException("PTP lock failed")));
    }

    [Fact]
    public void Classify_throws_for_a_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => AutoConnectFailureKindClassifier.Classify(null!));
    }
}

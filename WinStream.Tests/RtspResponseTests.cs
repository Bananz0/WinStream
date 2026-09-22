using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RtspResponseTests
{
    [Fact]
    public void Parse_ExtractsSessionAndTransport()
    {
        const string raw =
            "RTSP/1.0 200 OK\r\n" +
            "CSeq: 3\r\n" +
            "Session: 12345678;timeout=60\r\n" +
            "Transport: RTP/AVP/UDP;server_port=6000;control_port=6001;timing_port=6002\r\n\r\n";

        var response = RtspResponse.Parse(raw);
        var transport = RaopTransportInfo.Parse(response.Transport);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("12345678", response.SessionId);
        Assert.Equal(6000, transport.ServerPort);
        Assert.Equal(6001, transport.ControlPort);
        Assert.Equal(6002, transport.TimingPort);
    }

    [Fact]
    public void EnsureSuccess_ThrowsForFailure()
    {
        var response = RtspResponse.Parse(
            "RTSP/1.0 453 Not Enough Bandwidth\r\nCSeq: 4\r\n\r\n");

        var error = Assert.Throws<InvalidOperationException>(() =>
            response.EnsureSuccess("RECORD"));

        Assert.Contains("453", error.Message);
        Assert.Contains("RECORD", error.Message);
    }

    [Fact]
    public void A_401_names_the_password_instead_of_the_pairing()
    {
        var response = RtspResponse.Parse(
            "RTSP/1.0 401 Unauthorized\r\n" +
            "CSeq: 5\r\n" +
            "WWW-Authenticate: Digest realm=\"AirPlay\", nonce=\"abc123\"\r\n\r\n");

        Assert.True(response.IsUnauthorized);
        Assert.Equal("Digest realm=\"AirPlay\", nonce=\"abc123\"", response.AuthenticationChallenge);

        var error = Assert.Throws<InvalidOperationException>(() =>
            response.EnsureSuccess("SETUP session"));

        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failure_without_a_challenge_reports_no_challenge()
    {
        var response = RtspResponse.Parse("RTSP/1.0 403 Forbidden\r\nCSeq: 6\r\n\r\n");

        Assert.False(response.IsUnauthorized);
        Assert.Null(response.AuthenticationChallenge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n")]
    [InlineData("not a response")]
    public void Parse_RejectsMalformedStatusLine(string raw)
    {
        Assert.ThrowsAny<Exception>(() => RtspResponse.Parse(raw));
    }
}

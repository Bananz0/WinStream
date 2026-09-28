using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RtspStatusExceptionTests
{
    [Fact]
    public void EnsureAirPlaySuccess_does_not_throw_for_2xx()
    {
        var response = RtspResponse.Parse("RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n");

        var exception = Record.Exception(() => response.EnsureAirPlaySuccess("GET /info"));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureAirPlaySuccess_throws_RtspStatusException_with_method_and_status_code()
    {
        var response = RtspResponse.Parse("RTSP/1.0 400 Bad Request\r\nCSeq: 1\r\n\r\n");

        var exception = Assert.Throws<RtspStatusException>(
            () => response.EnsureAirPlaySuccess("SETUP session"));

        Assert.Equal("SETUP session", exception.Method);
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("SETUP session", exception.Message);
        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public void EnsureAirPlaySuccess_notes_the_password_hint_on_401()
    {
        var response = RtspResponse.Parse("RTSP/1.0 401 Unauthorized\r\nCSeq: 1\r\n\r\n");

        var exception = Assert.Throws<RtspStatusException>(
            () => response.EnsureAirPlaySuccess("SETUP stream"));

        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("AirPlay password", exception.Message);
    }

    [Fact]
    public void EnsureAirPlaySuccess_does_not_change_RtspResponse_EnsureSuccess_behavior()
    {
        // RtspResponse.EnsureSuccess is shared with the RAOP path and must
        // keep throwing its own generic InvalidOperationException.
        var response = RtspResponse.Parse("RTSP/1.0 400 Bad Request\r\nCSeq: 1\r\n\r\n");

        Assert.Throws<InvalidOperationException>(() => response.EnsureSuccess("SETUP"));
    }
}

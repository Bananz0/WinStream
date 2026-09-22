using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RtspDigestAuthTests
{
    [Fact]
    public void Parses_realm_and_nonce_from_a_digest_challenge()
    {
        Assert.True(RtspDigestAuth.TryParseChallenge(
            "Digest realm=\"airplay\", nonce=\"abc123\"",
            out var realm,
            out var nonce));

        Assert.Equal("airplay", realm);
        Assert.Equal("abc123", nonce);
    }

    [Fact]
    public void Rejects_a_non_digest_challenge()
    {
        Assert.False(RtspDigestAuth.TryParseChallenge(
            "Basic realm=\"AirPlay\"",
            out _,
            out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Digest")]
    [InlineData("Digest nonce=\"abc123\"")]
    [InlineData("Digest realm=\"AirPlay\"")]
    [InlineData("Digest realm=\"\", nonce=\"abc123\"")]
    [InlineData("Digest realm=\"AirPlay\", nonce=\"\"")]
    public void Rejects_malformed_or_incomplete_challenges(string? challenge)
    {
        Assert.False(RtspDigestAuth.TryParseChallenge(challenge, out _, out _));
    }

    [Fact]
    public void Builds_the_owntone_style_empty_username_response()
    {
        // HA1 = MD5(":AirPlay:secret")
        // HA2 = MD5("SETUP:rtsp://192.168.1.10/SESSION")
        // response = MD5(HA1:n1:HA2)
        var authorization = RtspDigestAuth.BuildAuthorization(
            realm: "AirPlay",
            nonce: "n1",
            method: "SETUP",
            uri: "rtsp://192.168.1.10/SESSION",
            password: "secret",
            username: "");

        Assert.Equal(
            "Digest username=\"\", realm=\"AirPlay\", nonce=\"n1\", " +
            "uri=\"rtsp://192.168.1.10/SESSION\", response=\"7381cffa1eb3e417c84b1c30e7ea844f\"",
            authorization);
        Assert.DoesNotContain("secret", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void Classic_raop_realm_uses_the_itunes_username()
    {
        // HA1 = MD5("iTunes:raop:secret")
        // HA2 = MD5("ANNOUNCE:rtsp://speaker/1")
        // response = MD5(HA1:n1:HA2)
        var authorization = RtspDigestAuth.BuildAuthorization(
            realm: "raop",
            nonce: "n1",
            method: "ANNOUNCE",
            uri: "rtsp://speaker/1",
            password: "secret",
            username: "iTunes");

        Assert.Equal(
            "Digest username=\"iTunes\", realm=\"raop\", nonce=\"n1\", " +
            "uri=\"rtsp://speaker/1\", response=\"2ef5cf6d4f4fba3d5c8fe54c64948d48\"",
            authorization);
    }

    [Fact]
    public void AirPlay_realms_use_only_the_empty_username()
    {
        Assert.Equal([""], RtspDigestAuth.UsernameCandidates("airplay"));
        Assert.Equal([""], RtspDigestAuth.UsernameCandidates("AirPlay"));
    }

    [Fact]
    public void Raop_realm_uses_only_the_itunes_username()
    {
        Assert.Equal(["iTunes"], RtspDigestAuth.UsernameCandidates("raop"));
        Assert.Equal(["iTunes"], RtspDigestAuth.UsernameCandidates("RAOP"));
    }

    [Fact]
    public void TryBuild_returns_null_without_a_usable_challenge()
    {
        Assert.Null(RtspDigestAuth.TryBuildAuthorization(
            challenge: null,
            method: "SETUP",
            uri: "rtsp://host/s",
            password: "x"));
    }

    [Fact]
    public void Digest_header_values_escape_backslashes_and_quotes()
    {
        var authorization = RtspDigestAuth.BuildAuthorization(
            realm: "Air\"Play\\test",
            nonce: "n\"1",
            method: "SETUP",
            uri: "rtsp://host/\"path\"",
            password: "secret",
            username: "i\"T");

        Assert.Contains("realm=\"Air\\\"Play\\\\test\"", authorization, StringComparison.Ordinal);
        Assert.Contains("nonce=\"n\\\"1\"", authorization, StringComparison.Ordinal);
        Assert.Contains("uri=\"rtsp://host/\\\"path\\\"\"", authorization, StringComparison.Ordinal);
        Assert.Contains("username=\"i\\\"T\"", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_applies_the_realm_username_policy_from_a_live_challenge()
    {
        var authorization = RtspDigestAuth.TryBuildAuthorization(
            challenge: "Digest realm=\"AirPlay\", nonce=\"n1\"",
            method: "SETUP",
            uri: "rtsp://192.168.1.10/SESSION",
            password: "secret");

        Assert.Equal(
            RtspDigestAuth.BuildAuthorization(
                "AirPlay", "n1", "SETUP", "rtsp://192.168.1.10/SESSION", "secret", ""),
            authorization);
    }
}

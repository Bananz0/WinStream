using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RtspDigestRetryPlanTests
{
    [Fact]
    public void No_password_never_produces_an_attempt()
    {
        var plan = RtspDigestRetryPlan.NextAttempt(
            "Digest realm=\"AirPlay\", nonce=\"n1\"",
            "SETUP",
            "rtsp://host/s",
            password: null,
            callerSuppliedAuthorization: false);

        Assert.Null(plan);
    }

    [Fact]
    public void A_caller_supplied_Authorization_header_is_not_retried()
    {
        var plan = RtspDigestRetryPlan.NextAttempt(
            "Digest realm=\"AirPlay\", nonce=\"n1\"",
            "SETUP",
            "rtsp://host/s",
            password: "secret",
            callerSuppliedAuthorization: true);

        Assert.Null(plan);
    }

    [Fact]
    public void An_unparsable_challenge_produces_no_attempt()
    {
        var plan = RtspDigestRetryPlan.NextAttempt(
            challenge: null,
            "SETUP",
            "rtsp://host/s",
            password: "secret",
            callerSuppliedAuthorization: false);

        Assert.Null(plan);
    }

    [Fact]
    public void A_parsable_challenge_uses_the_realm_username_policy()
    {
        var plan = RtspDigestRetryPlan.NextAttempt(
            "Digest realm=\"AirPlay\", nonce=\"n1\"",
            "SETUP",
            "rtsp://192.168.1.10/SESSION",
            password: "secret",
            callerSuppliedAuthorization: false);

        Assert.NotNull(plan);
        Assert.Equal("AirPlay", plan!.Value.Realm);
        Assert.Equal("n1", plan.Value.Nonce);
        Assert.Equal(string.Empty, plan.Value.Username);
        Assert.Equal(
            RtspDigestAuth.BuildAuthorization("AirPlay", "n1", "SETUP", "rtsp://192.168.1.10/SESSION", "secret", ""),
            plan.Value.Authorization);
    }

    [Fact]
    public void A_second_call_with_a_rotated_nonce_produces_a_fresh_attempt()
    {
        var first = RtspDigestRetryPlan.NextAttempt(
            "Digest realm=\"AirPlay\", nonce=\"n1\"", "SETUP", "rtsp://host/s", "secret", false);
        var second = RtspDigestRetryPlan.NextAttempt(
            "Digest realm=\"AirPlay\", nonce=\"n2\"", "RECORD", "rtsp://host/s", "secret", false);

        Assert.Equal("n1", first!.Value.Nonce);
        Assert.Equal("n2", second!.Value.Nonce);
    }
}

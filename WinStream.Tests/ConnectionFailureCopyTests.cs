using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class ConnectionFailureCopyTests
{
    [Fact]
    public void A_403_names_the_network_ACL_on_the_row()
    {
        const string message =
            "Receiver returned 403 Forbidden. Confirm AirPlay Receiver is enabled and " +
            "allowed for Everyone / same network.";

        Assert.Equal(ConnectionFailureCopy.NotAvailableRow, ConnectionFailureCopy.DeviceRow(message));
        Assert.Contains("Everyone", ConnectionFailureCopy.Detail(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_auth_failure_names_the_wrong_password()
    {
        const string message =
            "Pair-setup error: authentication failed — the receiver rejected the AirPlay code or password.";

        Assert.Equal(ConnectionFailureCopy.WrongPasswordRow, ConnectionFailureCopy.DeviceRow(message));
        Assert.Contains("password", ConnectionFailureCopy.Detail(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_401_password_hint_names_the_required_password()
    {
        const string message =
            "SETUP session failed with RTSP 401 Unauthorized. The receiver is asking for its AirPlay password.";

        Assert.Equal(ConnectionFailureCopy.PasswordRequiredRow, ConnectionFailureCopy.DeviceRow(message));
    }

    [Fact]
    public void A_rejected_digest_password_names_the_wrong_password_not_required()
    {
        var exception = new AirPlayPasswordRejectedException(
            "SETUP rejected the AirPlay password (RTSP 401 after Digest response).");

        Assert.Equal(ConnectionFailureCopy.WrongPasswordRow, ConnectionFailureCopy.DeviceRow(exception));
        Assert.Equal(ConnectionFailureCopy.WrongPasswordDetail, ConnectionFailureCopy.Detail(exception));
    }

    [Fact]
    public void An_unknown_failure_stays_generic_on_the_row()
    {
        Assert.Equal(ConnectionFailureCopy.GenericRow, ConnectionFailureCopy.DeviceRow("socket closed"));
        Assert.Equal("socket closed", ConnectionFailureCopy.Detail("socket closed"));
    }
}


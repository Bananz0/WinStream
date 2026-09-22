using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Core.Streaming;

/// <summary>
/// Honest connect-failure copy for the device row and InfoBar (testable without WinUI).
/// </summary>
public static class ConnectionFailureCopy
{
    public const string GenericRow = "Couldn't connect.";

    public const string PasswordRequiredRow = "AirPlay password required.";

    public const string WrongPasswordRow = "Wrong AirPlay password.";

    public const string NotAvailableRow = "Not available on this network.";

    public const string PasswordRequiredDetail =
        "This receiver has an AirPlay password. Enter it when WinStream asks, " +
        "or turn the password off under System Settings → General → AirDrop & Handoff → " +
        "AirPlay Receiver.";

    public const string WrongPasswordDetail =
        "The password was rejected. Check System Settings → General → AirDrop & Handoff → " +
        "AirPlay Receiver, or turn the password off while testing.";

    public const string NotAvailableDetail =
        "On the Mac, set AirPlay Receiver to \"Everyone\" or \"Anyone on the same network\".";

    public static string DeviceRow(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ReceiverPasswordRequiredException)
        {
            return PasswordRequiredRow;
        }

        if (exception is AirPlayPasswordRejectedException)
        {
            return WrongPasswordRow;
        }

        return DeviceRow(exception.Message);
    }

    public static string DeviceRow(string? message)
    {
        if (IsWrongPassword(message))
        {
            return WrongPasswordRow;
        }

        if (IsPasswordRequired(message))
        {
            return PasswordRequiredRow;
        }

        if (IsNetworkAcl(message))
        {
            return NotAvailableRow;
        }

        return GenericRow;
    }

    public static string Detail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ReceiverPasswordRequiredException)
        {
            return PasswordRequiredDetail;
        }

        if (exception is AirPlayPasswordRejectedException)
        {
            return WrongPasswordDetail;
        }

        return Detail(exception.Message);
    }

    public static string Detail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unknown error.";
        }

        if (IsWrongPassword(message))
        {
            return WrongPasswordDetail;
        }

        if (IsPasswordRequired(message))
        {
            return PasswordRequiredDetail;
        }

        if (IsNetworkAcl(message))
        {
            // Protocol messages already name Everyone / same network; keep them.
            if (message.Contains("Everyone", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("same network", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }

            return message + " " + NotAvailableDetail;
        }

        return message;
    }

    public static bool IsWrongPassword(string? message) =>
        Contains(message, "authentication failed") ||
        Contains(message, "wrong AirPlay code") ||
        Contains(message, "server proof mismatch");

    public static bool IsPasswordRequired(string? message) =>
        Contains(message, "asking for its AirPlay password") ||
        (Contains(message, "401") && Contains(message, "password"));

    public static bool IsNetworkAcl(string? message) =>
        Contains(message, "470") ||
        Contains(message, "403") ||
        Contains(message, "Everyone") ||
        Contains(message, "same network") ||
        Contains(message, "Pairing refused");

    private static bool Contains(string? message, string fragment) =>
        !string.IsNullOrEmpty(message) &&
        message.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

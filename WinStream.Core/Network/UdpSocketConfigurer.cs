#nullable enable

using System;
using System.Net.Sockets;
using WinStream.Core.Logging;

namespace WinStream.Core.Network;

public static class UdpSocketConfigurer
{
    // Windows: disable ICMP port-unreachable → SocketException on UDP receive.
    private const int SioUdpConnreset = -1744830452;

    public static void SuppressUdpConnReset(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            socket.IOControl(SioUdpConnreset, [0], null);
        }
        catch (Exception ex)
        {
            AppLog.Warn("udp", $"SIO_UDP_CONNRESET unavailable: {ex.GetType().Name}");
        }
    }

    public static void SuppressUdpConnReset(UdpClient client) =>
        SuppressUdpConnReset(client.Client);
}

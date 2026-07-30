namespace WinStream.Core.Protocol.Raop;

public sealed record RaopTransportInfo(
    int? ServerPort,
    int? ControlPort,
    int? TimingPort)
{
    public static RaopTransportInfo Parse(string? transport)
    {
        if (string.IsNullOrWhiteSpace(transport))
        {
            return new RaopTransportInfo(null, null, null);
        }

        int? serverPort = null;
        int? controlPort = null;
        int? timingPort = null;
        foreach (var segment in transport.Split(';', StringSplitOptions.TrimEntries))
        {
            var pair = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            var firstPort = pair[1].Split('-', 2)[0];
            if (!int.TryParse(firstPort, out var port))
            {
                continue;
            }

            switch (pair[0].ToLowerInvariant())
            {
                case "server_port":
                    serverPort = port;
                    break;
                case "control_port":
                    controlPort = port;
                    break;
                case "timing_port":
                    timingPort = port;
                    break;
            }
        }

        return new RaopTransportInfo(serverPort, controlPort, timingPort);
    }
}

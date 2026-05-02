using System;
using System.Collections.Generic;
using System.Text;

namespace WinStream.Network
{
    public sealed class RtspResponse
    {
        public string StatusLine { get; init; } = string.Empty;
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] BodyBytes { get; init; } = Array.Empty<byte>();

        public int StatusCode
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StatusLine))
                {
                    return 0;
                }

                var parts = StatusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
            }
        }

        public string AsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(StatusLine);
            foreach (var header in Headers)
            {
                sb.AppendLine($"{header.Key}: {header.Value}");
            }

            if (BodyBytes.Length > 0)
            {
                sb.AppendLine();
                sb.Append(Encoding.UTF8.GetString(BodyBytes));
            }

            return sb.ToString();
        }
    }
}

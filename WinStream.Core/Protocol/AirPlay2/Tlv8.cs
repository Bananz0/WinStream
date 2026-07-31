namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>HomeKit / AirPlay pairing TLV8 (type, length, value; values may fragment).</summary>
public static class Tlv8
{
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte State = 0x06;
    public const byte Error = 0x07;
    public const byte Flags = 0x13;

    public static byte[] Encode(IReadOnlyList<(byte Type, byte[] Value)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new MemoryStream();
        foreach (var (type, value) in entries)
        {
            ArgumentNullException.ThrowIfNull(value);
            var offset = 0;
            while (offset < value.Length || value.Length == 0)
            {
                var chunk = Math.Min(255, value.Length - offset);
                stream.WriteByte(type);
                stream.WriteByte((byte)chunk);
                if (chunk > 0)
                {
                    stream.Write(value, offset, chunk);
                    offset += chunk;
                }

                if (value.Length == 0)
                {
                    break;
                }
            }
        }

        return stream.ToArray();
    }

    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> buffer)
    {
        var result = new Dictionary<byte, List<byte>>();
        var i = 0;
        while (i + 1 < buffer.Length)
        {
            var type = buffer[i++];
            var length = buffer[i++];
            if (i + length > buffer.Length)
            {
                throw new FormatException("TLV8 value overruns buffer.");
            }

            if (!result.TryGetValue(type, out var list))
            {
                list = new List<byte>();
                result[type] = list;
            }

            for (var n = 0; n < length; n++)
            {
                list.Add(buffer[i++]);
            }
        }

        if (i != buffer.Length)
        {
            throw new FormatException("TLV8 trailing byte without type/length.");
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
    }

    public static byte[] Require(IReadOnlyDictionary<byte, byte[]> map, byte type, string name)
    {
        if (!map.TryGetValue(type, out var value) || value.Length == 0)
        {
            throw new InvalidOperationException($"Pairing response missing TLV {name}.");
        }

        return value;
    }
}

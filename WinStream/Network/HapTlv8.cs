using System;
using System.Collections.Generic;

namespace WinStream.Network
{
    public enum HapTlvType : byte
    {
        Method = 0x00,
        Identifier = 0x01,
        Salt = 0x02,
        PublicKey = 0x03,
        Proof = 0x04,
        EncryptedData = 0x05,
        State = 0x06,
        Error = 0x07,
        BackOff = 0x08,
        Certificate = 0x09,
        Signature = 0x0A,
        Name = 0x11,
        Flags = 0x13,
    }

    public static class HapTlv8
    {
        public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> data)
        {
            var result = new Dictionary<byte, List<byte>>();
            var index = 0;
            while (index + 1 < data.Length)
            {
                var type = data[index++];
                var length = data[index++];
                if (index + length > data.Length)
                {
                    break;
                }

                if (!result.TryGetValue(type, out var current))
                {
                    current = new List<byte>(length);
                    result[type] = current;
                }

                for (var i = 0; i < length; i++)
                {
                    current.Add(data[index + i]);
                }

                index += length;
            }

            var merged = new Dictionary<byte, byte[]>();
            foreach (var entry in result)
            {
                merged[entry.Key] = entry.Value.ToArray();
            }

            return merged;
        }

        public static byte[] Encode(params (byte Type, byte[] Value)[] entries)
        {
            var output = new List<byte>(256);
            foreach (var (type, value) in entries)
            {
                var data = value ?? Array.Empty<byte>();
                if (data.Length == 0)
                {
                    output.Add(type);
                    output.Add(0);
                    continue;
                }

                var offset = 0;
                while (offset < data.Length)
                {
                    var chunkSize = Math.Min(255, data.Length - offset);
                    output.Add(type);
                    output.Add((byte)chunkSize);
                    for (var i = 0; i < chunkSize; i++)
                    {
                        output.Add(data[offset + i]);
                    }

                    offset += chunkSize;
                }
            }

            return output.ToArray();
        }
    }
}

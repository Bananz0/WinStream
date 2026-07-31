using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace WinStream.Core.Network;

internal enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Srv = 33,
    Any = 255
}

internal readonly record struct DnsQuestion(string Name, DnsRecordType Type, bool UnicastResponse);

/// <summary>
/// Just enough DNS wire format to answer mDNS queries for one service. Responses are
/// written without name compression, which is legal and keeps the writer obvious.
/// </summary>
internal static class MdnsWire
{
    public const int MaxMessageBytes = 9000;
    private const int HeaderBytes = 12;
    private const ushort ClassIn = 1;
    private const ushort CacheFlush = 0x8000;
    private const byte PointerMask = 0xC0;

    /// <summary>Returns false for anything that is not a well-formed query we can answer.</summary>
    public static bool TryReadQuestions(ReadOnlySpan<byte> message, out List<DnsQuestion> questions)
    {
        questions = new List<DnsQuestion>();
        if (message.Length < HeaderBytes)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var isResponse = (flags & 0x8000) != 0;
        if (isResponse)
        {
            return false;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var offset = HeaderBytes;
        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, out var name) ||
                offset + 4 > message.Length)
            {
                return false;
            }

            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var rawClass = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
            offset += 4;
            questions.Add(new DnsQuestion(name, type, (rawClass & 0x8000) != 0));
        }

        return true;
    }

    public static int WriteResponse(
        Span<byte> destination,
        IReadOnlyList<DnsResourceRecord> answers,
        IReadOnlyList<DnsResourceRecord> additional)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0x8400); // response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)additional.Count);

        var offset = HeaderBytes;
        foreach (var record in answers)
        {
            offset += WriteRecord(destination[offset..], record);
        }

        foreach (var record in additional)
        {
            offset += WriteRecord(destination[offset..], record);
        }

        return offset;
    }

    public static byte[] EncodeName(string name)
    {
        var buffer = new byte[name.Length + 2];
        var written = WriteName(buffer, name);
        return buffer[..written];
    }

    /// <summary>Compares DNS names case-insensitively with or without the trailing dot.</summary>
    public static bool NameEquals(string left, string right) =>
        string.Equals(
            left.TrimEnd('.'),
            right.TrimEnd('.'),
            StringComparison.OrdinalIgnoreCase);

    private static int WriteRecord(Span<byte> destination, DnsResourceRecord record)
    {
        var offset = WriteName(destination, record.Name);
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)record.Type);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(offset + 2)..],
            (ushort)(ClassIn | (record.CacheFlush ? CacheFlush : 0)));
        BinaryPrimitives.WriteUInt32BigEndian(destination[(offset + 4)..], record.TimeToLiveSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(destination[(offset + 8)..], (ushort)record.Data.Length);
        offset += 10;
        record.Data.CopyTo(destination[offset..]);
        return offset + record.Data.Length;
    }

    private static int WriteName(Span<byte> destination, string name)
    {
        var offset = 0;
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' exceeds 63 bytes.", nameof(name));
            }

            destination[offset++] = (byte)bytes.Length;
            bytes.CopyTo(destination[offset..]);
            offset += bytes.Length;
        }

        destination[offset++] = 0;
        return offset;
    }

    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        var builder = new StringBuilder();
        var cursor = offset;
        var followedPointer = false;
        var hops = 0;

        while (true)
        {
            if (cursor >= message.Length)
            {
                name = string.Empty;
                return false;
            }

            var length = message[cursor];
            if ((length & PointerMask) == PointerMask)
            {
                if (cursor + 1 >= message.Length || ++hops > 16)
                {
                    name = string.Empty;
                    return false;
                }

                var pointer = BinaryPrimitives.ReadUInt16BigEndian(message[cursor..]) & 0x3FFF;
                if (!followedPointer)
                {
                    offset = cursor + 2;
                    followedPointer = true;
                }

                cursor = pointer;
                continue;
            }

            if (length == 0)
            {
                if (!followedPointer)
                {
                    offset = cursor + 1;
                }

                name = builder.ToString();
                return true;
            }

            if (cursor + 1 + length > message.Length)
            {
                name = string.Empty;
                return false;
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(cursor + 1, length)));
            builder.Append('.');
            cursor += 1 + length;
        }
    }
}

internal readonly record struct DnsResourceRecord(
    string Name,
    DnsRecordType Type,
    uint TimeToLiveSeconds,
    byte[] Data,
    bool CacheFlush = true)
{
    public static DnsResourceRecord Ptr(string serviceName, string instanceName, uint ttl) =>
        // Shared records must not carry the cache-flush bit: several instances coexist.
        new(serviceName, DnsRecordType.Ptr, ttl, MdnsWire.EncodeName(instanceName), CacheFlush: false);

    public static DnsResourceRecord Srv(string instanceName, string hostName, ushort port, uint ttl)
    {
        var target = MdnsWire.EncodeName(hostName);
        var data = new byte[6 + target.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, 0); // priority
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0); // weight
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        target.CopyTo(data, 6);
        return new DnsResourceRecord(instanceName, DnsRecordType.Srv, ttl, data);
    }

    public static DnsResourceRecord Txt(
        string instanceName,
        IReadOnlyList<KeyValuePair<string, string>> entries,
        uint ttl)
    {
        var chunks = new List<byte[]>(entries.Count);
        var total = 0;
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes($"{entry.Key}={entry.Value}");
            if (bytes.Length > 255)
            {
                throw new ArgumentException($"TXT entry '{entry.Key}' exceeds 255 bytes.", nameof(entries));
            }

            chunks.Add(bytes);
            total += bytes.Length + 1;
        }

        var data = new byte[Math.Max(total, 1)];
        if (total == 0)
        {
            return new DnsResourceRecord(instanceName, DnsRecordType.Txt, ttl, data);
        }

        var offset = 0;
        foreach (var chunk in chunks)
        {
            data[offset++] = (byte)chunk.Length;
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }

        return new DnsResourceRecord(instanceName, DnsRecordType.Txt, ttl, data);
    }

    public static DnsResourceRecord A(string hostName, IPAddress address, uint ttl) =>
        new(hostName, DnsRecordType.A, ttl, address.GetAddressBytes());
}

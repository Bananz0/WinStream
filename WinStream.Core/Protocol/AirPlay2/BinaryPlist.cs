using System.Buffers.Binary;
using System.Text;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>Minimal bplist00 writer/reader for AirPlay SETUP and /info payloads.</summary>
public static class BinaryPlist
{
    public static byte[] Write(object root)
    {
        var objects = new List<object>();
        var children = new Dictionary<int, int[]>();
        Collect(Normalize(root), objects, children);

        var encoded = new byte[objects.Count][];
        for (var i = 0; i < objects.Count; i++)
        {
            encoded[i] = EncodeObject(objects[i], children.GetValueOrDefault(i));
        }

        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new int[encoded.Length];
        for (var i = 0; i < encoded.Length; i++)
        {
            offsets[i] = (int)stream.Position;
            stream.Write(encoded[i]);
        }

        var offsetTableOffset = (int)stream.Position;
        var offsetSize = offsets.Length > 0 && offsets[^1] > 0xFFFF ? 4 : 2;
        foreach (var offset in offsets)
        {
            WriteSizedInt(stream, offset, offsetSize);
        }

        stream.Write(new byte[6]);
        stream.WriteByte((byte)offsetSize);
        stream.WriteByte(1);
        WriteUInt64Be(stream, (ulong)objects.Count);
        WriteUInt64Be(stream, 0);
        WriteUInt64Be(stream, (ulong)offsetTableOffset);
        return stream.ToArray();
    }

    public static byte[] WriteDictionary(IReadOnlyDictionary<string, object> values) =>
        Write(values);

    public static object Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data[..8].SequenceEqual("bplist00"u8))
        {
            throw new FormatException("Not a bplist00 document.");
        }

        var trailer = data[^32..];
        var offsetSize = trailer[6];
        var refSize = trailer[7];
        var objectCount = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..16]);
        var topObject = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..24]);
        var offsetTableOffset = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..32]);

        var offsets = new int[objectCount];
        for (var i = 0; i < objectCount; i++)
        {
            var start = offsetTableOffset + (i * offsetSize);
            offsets[i] = (int)ReadSizedInt(data.Slice(start, offsetSize), offsetSize);
        }

        return ParseObject(data, offsets, topObject, refSize, new Dictionary<int, object?>());
    }

    public static bool TryGetStreamPorts(object root, out int dataPort, out int controlPort)
    {
        dataPort = 0;
        controlPort = 0;
        if (root is not Dictionary<string, object?> dict ||
            !dict.TryGetValue("streams", out var streamsObj) ||
            streamsObj is not object[] streams ||
            streams.Length == 0 ||
            streams[0] is not Dictionary<string, object?> stream)
        {
            return false;
        }

        if (!TryGetInteger(stream, "dataPort", out var data) ||
            !TryGetInteger(stream, "controlPort", out var control))
        {
            return false;
        }

        if (data is <= 0 or > ushort.MaxValue || control is <= 0 or > ushort.MaxValue)
        {
            return false;
        }

        dataPort = (int)data;
        controlPort = (int)control;
        return true;
    }

    public static bool TryGetInteger(object root, string key, out long value)
    {
        value = 0;
        if (root is not Dictionary<string, object?> dict ||
            !dict.TryGetValue(key, out var raw) ||
            raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            case ulong u when u <= long.MaxValue:
                value = (long)u;
                return true;
            default:
                return false;
        }
    }

    private static void Collect(
        object value,
        List<object> objects,
        Dictionary<int, int[]> children)
    {
        var index = objects.Count;
        objects.Add(value);
        switch (value)
        {
            case IReadOnlyDictionary<string, object> dict:
            {
                var pairs = dict.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
                var refs = new int[pairs.Count * 2];
                for (var i = 0; i < pairs.Count; i++)
                {
                    refs[i] = objects.Count;
                    Collect(pairs[i].Key, objects, children);
                    refs[pairs.Count + i] = objects.Count;
                    Collect(Normalize(pairs[i].Value), objects, children);
                }

                children[index] = refs;
                break;
            }
            case IReadOnlyList<object> list:
            {
                var refs = new int[list.Count];
                for (var i = 0; i < list.Count; i++)
                {
                    refs[i] = objects.Count;
                    Collect(Normalize(list[i]), objects, children);
                }

                children[index] = refs;
                break;
            }
        }
    }

    private static object Normalize(object value) =>
        value switch
        {
            int i => (long)i,
            uint u => (long)u,
            string or bool or byte[] or long => value,
            Dictionary<string, object> dict => dict,
            List<object> list => list,
            object[] arr => arr,
            IReadOnlyDictionary<string, object> dict => dict,
            IReadOnlyList<object> list => list,
            _ => throw new NotSupportedException($"Unsupported plist type {value.GetType().Name}")
        };

    private static byte[] EncodeObject(object value, int[]? childRefs) =>
        value switch
        {
            string s => EncodeString(s),
            bool b => [(byte)(b ? 0x09 : 0x08)],
            byte[] data => EncodeData(data),
            long l => EncodeInteger(l),
            IReadOnlyDictionary<string, object> => EncodeRefs(0xD0, childRefs ?? []),
            IReadOnlyList<object> => EncodeRefs(0xA0, childRefs ?? []),
            _ => throw new NotSupportedException($"Unsupported plist type {value.GetType().Name}")
        };

    private static byte[] EncodeRefs(byte baseMarker, int[] refs)
    {
        using var stream = new MemoryStream();
        var count = baseMarker == 0xD0 ? refs.Length / 2 : refs.Length;
        WriteCountMarker(stream, baseMarker, count);
        foreach (var r in refs)
        {
            stream.WriteByte((byte)r);
        }

        return stream.ToArray();
    }

    private static byte[] EncodeString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream();
        WriteCountMarker(stream, 0x50, utf8.Length);
        stream.Write(utf8);
        return stream.ToArray();
    }

    private static byte[] EncodeData(byte[] data)
    {
        using var stream = new MemoryStream();
        WriteCountMarker(stream, 0x40, data.Length);
        stream.Write(data);
        return stream.ToArray();
    }

    private static byte[] EncodeInteger(long value)
    {
        if (value >= 0 && value <= 0xFF)
        {
            return [0x10, (byte)value];
        }

        if (value >= 0 && value <= 0xFFFF)
        {
            var bytes = new byte[3];
            bytes[0] = 0x11;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), (ushort)value);
            return bytes;
        }

        if (value >= int.MinValue && value <= int.MaxValue)
        {
            var bytes = new byte[5];
            bytes[0] = 0x12;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), (int)value);
            return bytes;
        }

        var wide = new byte[9];
        wide[0] = 0x13;
        BinaryPrimitives.WriteInt64BigEndian(wide.AsSpan(1), value);
        return wide;
    }

    private static void WriteCountMarker(Stream stream, byte baseMarker, int count)
    {
        if (count < 15)
        {
            stream.WriteByte((byte)(baseMarker | count));
            return;
        }

        stream.WriteByte((byte)(baseMarker | 0x0F));
        if (count <= 0xFF)
        {
            stream.WriteByte(0x10);
            stream.WriteByte((byte)count);
        }
        else
        {
            stream.WriteByte(0x11);
            Span<byte> be = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(be, (ushort)count);
            stream.Write(be);
        }
    }

    private static void WriteSizedInt(Stream stream, int value, int size)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (size == 1)
        {
            stream.WriteByte((byte)value);
        }
        else if (size == 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)value);
            stream.Write(buffer[..2]);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)value);
            stream.Write(buffer);
        }
    }

    private static void WriteUInt64Be(Stream stream, ulong value)
    {
        Span<byte> be = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(be, value);
        stream.Write(be);
    }

    private static ulong ReadSizedInt(ReadOnlySpan<byte> data, int size) =>
        size switch
        {
            1 => data[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(data),
            4 => BinaryPrimitives.ReadUInt32BigEndian(data),
            8 => BinaryPrimitives.ReadUInt64BigEndian(data),
            _ => throw new FormatException($"Unsupported int size {size}.")
        };

    private static object ParseObject(
        ReadOnlySpan<byte> data,
        int[] offsets,
        int index,
        int refSize,
        Dictionary<int, object?> cache)
    {
        if (cache.TryGetValue(index, out var cached) && cached is not null)
        {
            return cached;
        }

        var offset = offsets[index];
        var marker = data[offset];
        var type = marker & 0xF0;
        var info = marker & 0x0F;

        object result = type switch
        {
            0x00 => info switch
            {
                0x08 => false,
                0x09 => true,
                _ => throw new FormatException($"Unsupported null/bool marker 0x{marker:X2}.")
            },
            0x10 => ReadInteger(data, offset, info),
            0x40 => ReadData(data, offset, info),
            0x50 => Encoding.UTF8.GetString(ReadData(data, offset, info)),
            0x60 => Encoding.BigEndianUnicode.GetString(ReadData(data, offset, info)),
            0xA0 => ReadArray(data, offsets, offset, info, refSize, cache),
            0xD0 => ReadDictionary(data, offsets, offset, info, refSize, cache),
            _ => throw new FormatException($"Unsupported plist marker 0x{marker:X2}.")
        };

        cache[index] = result;
        return result;
    }

    private static long ReadInteger(ReadOnlySpan<byte> data, int offset, int info)
    {
        var size = 1 << info;
        var value = ReadSizedInt(data.Slice(offset + 1, size), size);
        return unchecked((long)value);
    }

    private static byte[] ReadData(ReadOnlySpan<byte> data, int offset, int info)
    {
        var (count, payloadOffset) = ReadCount(data, offset, info);
        return data.Slice(payloadOffset, count).ToArray();
    }

    private static (int Count, int PayloadOffset) ReadCount(
        ReadOnlySpan<byte> data,
        int offset,
        int info)
    {
        if (info < 15)
        {
            return (info, offset + 1);
        }

        var intMarker = data[offset + 1];
        var intInfo = intMarker & 0x0F;
        var intSize = 1 << intInfo;
        var count = (int)ReadSizedInt(data.Slice(offset + 2, intSize), intSize);
        return (count, offset + 2 + intSize);
    }

    private static object[] ReadArray(
        ReadOnlySpan<byte> data,
        int[] offsets,
        int offset,
        int info,
        int refSize,
        Dictionary<int, object?> cache)
    {
        var (count, refsOffset) = ReadCount(data, offset, info);
        var items = new object[count];
        for (var i = 0; i < count; i++)
        {
            var refIndex = (int)ReadSizedInt(data.Slice(refsOffset + (i * refSize), refSize), refSize);
            items[i] = ParseObject(data, offsets, refIndex, refSize, cache);
        }

        return items;
    }

    private static Dictionary<string, object?> ReadDictionary(
        ReadOnlySpan<byte> data,
        int[] offsets,
        int offset,
        int info,
        int refSize,
        Dictionary<int, object?> cache)
    {
        var (count, refsOffset) = ReadCount(data, offset, info);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var keyRef = (int)ReadSizedInt(data.Slice(refsOffset + (i * refSize), refSize), refSize);
            var valueRef = (int)ReadSizedInt(
                data.Slice(refsOffset + ((count + i) * refSize), refSize),
                refSize);
            var keyObj = ParseObject(data, offsets, keyRef, refSize, cache);
            if (keyObj is not string key)
            {
                throw new FormatException("Dictionary key must be a string.");
            }

            dict[key] = ParseObject(data, offsets, valueRef, refSize, cache);
        }

        return dict;
    }
}

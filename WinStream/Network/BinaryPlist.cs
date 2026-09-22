using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinStream.Network
{
    internal static class BinaryPlist
    {
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("bplist00");

        public static byte[] Write(object value)
        {
            var writer = new Writer();
            return writer.Write(value);
        }

        public static object Read(byte[] data)
        {
            if (data == null || data.Length < Header.Length + 32 ||
                !Header.SequenceEqual(data.Take(Header.Length)))
            {
                throw new InvalidDataException("Invalid binary plist header.");
            }

            return new Reader(data).Read();
        }

        public static bool TryGetUInt(object root, string key, out ulong value)
        {
            value = 0;
            if (root is not Dictionary<string, object> dict ||
                !dict.TryGetValue(key, out var item))
            {
                return false;
            }

            return TryAsUInt(item, out value);
        }

        public static bool TryGetUInt(object root, string arrayKey, int index, string key, out ulong value)
        {
            value = 0;
            if (root is not Dictionary<string, object> dict ||
                !dict.TryGetValue(arrayKey, out var arrayItem) ||
                arrayItem is not List<object> array ||
                index < 0 ||
                index >= array.Count ||
                array[index] is not Dictionary<string, object> itemDict ||
                !itemDict.TryGetValue(key, out var item))
            {
                return false;
            }

            return TryAsUInt(item, out value);
        }

        private static bool TryAsUInt(object item, out ulong value)
        {
            switch (item)
            {
                case byte b:
                    value = b;
                    return true;
                case ushort s:
                    value = s;
                    return true;
                case uint i:
                    value = i;
                    return true;
                case ulong l:
                    value = l;
                    return true;
                case long signed when signed >= 0:
                    value = (ulong)signed;
                    return true;
                case int signed when signed >= 0:
                    value = (ulong)signed;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        private sealed class Writer
        {
            private readonly List<object> _objects = new();

            public byte[] Write(object value)
            {
                _objects.Clear();
                var topObject = AddObject(value);

                var objectRefSize = IntSize((ulong)Math.Max(1, _objects.Count - 1));
                using var output = new MemoryStream();
                output.Write(Header, 0, Header.Length);

                var offsets = new List<ulong>(_objects.Count);
                foreach (var item in _objects)
                {
                    offsets.Add((ulong)output.Position);
                    WriteObject(output, item, objectRefSize);
                }

                var offsetTableOffset = (ulong)output.Position;
                var offsetIntSize = IntSize(offsets.Max());
                foreach (var offset in offsets)
                {
                    WriteBigEndian(output, offset, offsetIntSize);
                }

                output.Write(new byte[6], 0, 6);
                output.WriteByte((byte)offsetIntSize);
                output.WriteByte((byte)objectRefSize);
                WriteBigEndian(output, (ulong)_objects.Count, 8);
                WriteBigEndian(output, (ulong)topObject, 8);
                WriteBigEndian(output, offsetTableOffset, 8);
                return output.ToArray();
            }

            private int AddObject(object value)
            {
                var index = _objects.Count;
                _objects.Add(value);

                if (value is Dictionary<string, object> dict)
                {
                    foreach (var entry in dict)
                    {
                        AddObject(entry.Key);
                    }

                    foreach (var entry in dict)
                    {
                        AddObject(entry.Value);
                    }
                }
                else if (value is IEnumerable<object> array && value is not byte[])
                {
                    var list = value as List<object> ?? array.ToList();
                    _objects[index] = list;
                    foreach (var item in list)
                    {
                        AddObject(item);
                    }
                }

                return index;
            }

            private void WriteObject(Stream output, object value, int objectRefSize)
            {
                switch (value)
                {
                    case bool b:
                        output.WriteByte((byte)(b ? 0x09 : 0x08));
                        break;
                    case byte[] data:
                        WriteLengthMarker(output, 0x40, data.Length);
                        output.Write(data, 0, data.Length);
                        break;
                    case string s:
                        var stringBytes = Encoding.ASCII.GetBytes(s);
                        WriteLengthMarker(output, 0x50, stringBytes.Length);
                        output.Write(stringBytes, 0, stringBytes.Length);
                        break;
                    case int i:
                        WriteInteger(output, checked((ulong)i));
                        break;
                    case long l:
                        if (l < 0) throw new NotSupportedException("Negative plist integers are not supported.");
                        WriteInteger(output, (ulong)l);
                        break;
                    case uint ui:
                        WriteInteger(output, ui);
                        break;
                    case ulong ul:
                        WriteInteger(output, ul);
                        break;
                    case Dictionary<string, object> dict:
                        WriteDictionary(output, dict, objectRefSize);
                        break;
                    case IEnumerable<object> array:
                        WriteArray(output, value, array is IReadOnlyList<object> list ? list : array.ToList(), objectRefSize);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported plist object type: {value?.GetType().FullName ?? "null"}");
                }
            }

            private void WriteArray(Stream output, object arrayObject, IReadOnlyList<object> array, int objectRefSize)
            {
                WriteLengthMarker(output, 0xA0, array.Count);
                var start = _objects.IndexOf(arrayObject);
                if (start < 0)
                {
                    throw new InvalidDataException("Array object was not registered.");
                }

                for (var i = 0; i < array.Count; i++)
                {
                    WriteBigEndian(output, (ulong)(start + 1 + i), objectRefSize);
                }
            }

            private void WriteDictionary(Stream output, Dictionary<string, object> dict, int objectRefSize)
            {
                WriteLengthMarker(output, 0xD0, dict.Count);
                var start = _objects.IndexOf(dict);
                if (start < 0)
                {
                    throw new InvalidDataException("Dictionary object was not registered.");
                }

                var keyStart = start + 1;
                var valueStart = keyStart + dict.Count;
                for (var i = 0; i < dict.Count; i++)
                {
                    WriteBigEndian(output, (ulong)(keyStart + i), objectRefSize);
                }

                for (var i = 0; i < dict.Count; i++)
                {
                    WriteBigEndian(output, (ulong)(valueStart + i), objectRefSize);
                }
            }

            private static void WriteLengthMarker(Stream output, int markerBase, int length)
            {
                if (length < 15)
                {
                    output.WriteByte((byte)(markerBase | length));
                    return;
                }

                output.WriteByte((byte)(markerBase | 0x0F));
                WriteInteger(output, (ulong)length);
            }

            private static void WriteInteger(Stream output, ulong value)
            {
                var size = IntSize(value);
                var power = size switch
                {
                    1 => 0,
                    2 => 1,
                    4 => 2,
                    8 => 3,
                    _ => throw new InvalidDataException("Invalid integer size.")
                };

                output.WriteByte((byte)(0x10 | power));
                WriteBigEndian(output, value, size);
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private readonly int _offsetIntSize;
            private readonly int _objectRefSize;
            private readonly ulong _numObjects;
            private readonly ulong _topObject;
            private readonly ulong _offsetTableOffset;
            private readonly ulong[] _offsets;
            private readonly Dictionary<ulong, object> _cache = new();

            public Reader(byte[] data)
            {
                _data = data;
                var trailer = data.Length - 32;
                _offsetIntSize = data[trailer + 6];
                _objectRefSize = data[trailer + 7];
                _numObjects = ReadBigEndian(data, trailer + 8, 8);
                _topObject = ReadBigEndian(data, trailer + 16, 8);
                _offsetTableOffset = ReadBigEndian(data, trailer + 24, 8);
                _offsets = new ulong[_numObjects];

                for (ulong i = 0; i < _numObjects; i++)
                {
                    _offsets[i] = ReadBigEndian(data, checked((int)(_offsetTableOffset + i * (ulong)_offsetIntSize)), _offsetIntSize);
                }
            }

            public object Read()
            {
                return ReadObject(_topObject);
            }

            private object ReadObject(ulong objectRef)
            {
                if (_cache.TryGetValue(objectRef, out var cached))
                {
                    return cached;
                }

                var offset = checked((int)_offsets[objectRef]);
                var marker = _data[offset++];
                var type = marker & 0xF0;
                var info = marker & 0x0F;
                object value = type switch
                {
                    0x00 => info switch
                    {
                        0x08 => false,
                        0x09 => true,
                        _ => throw new InvalidDataException($"Unsupported plist singleton marker 0x{marker:X2}.")
                    },
                    0x10 => ReadIntegerObject(offset, info),
                    0x40 => ReadDataObject(ref offset, info),
                    0x50 => ReadAsciiString(ref offset, info),
                    0xA0 => ReadArray(ref offset, info),
                    0xD0 => ReadDictionary(ref offset, info),
                    _ => throw new InvalidDataException($"Unsupported plist marker 0x{marker:X2}.")
                };

                _cache[objectRef] = value;
                return value;
            }

            private ulong ReadIntegerObject(int offset, int info)
            {
                return ReadBigEndian(_data, offset, 1 << info);
            }

            private byte[] ReadDataObject(ref int offset, int info)
            {
                var length = ReadLength(ref offset, info);
                var result = new byte[length];
                Buffer.BlockCopy(_data, offset, result, 0, length);
                offset += length;
                return result;
            }

            private string ReadAsciiString(ref int offset, int info)
            {
                var length = ReadLength(ref offset, info);
                var result = Encoding.ASCII.GetString(_data, offset, length);
                offset += length;
                return result;
            }

            private List<object> ReadArray(ref int offset, int info)
            {
                var count = ReadLength(ref offset, info);
                var result = new List<object>(count);
                for (var i = 0; i < count; i++)
                {
                    var childRef = ReadBigEndian(_data, offset, _objectRefSize);
                    offset += _objectRefSize;
                    result.Add(ReadObject(childRef));
                }

                return result;
            }

            private Dictionary<string, object> ReadDictionary(ref int offset, int info)
            {
                var count = ReadLength(ref offset, info);
                var keyRefs = new ulong[count];
                var valueRefs = new ulong[count];
                for (var i = 0; i < count; i++)
                {
                    keyRefs[i] = ReadBigEndian(_data, offset, _objectRefSize);
                    offset += _objectRefSize;
                }

                for (var i = 0; i < count; i++)
                {
                    valueRefs[i] = ReadBigEndian(_data, offset, _objectRefSize);
                    offset += _objectRefSize;
                }

                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    if (ReadObject(keyRefs[i]) is not string key)
                    {
                        throw new InvalidDataException("Dictionary key was not a string.");
                    }

                    result[key] = ReadObject(valueRefs[i]);
                }

                return result;
            }

            private int ReadLength(ref int offset, int info)
            {
                if (info < 15)
                {
                    return info;
                }

                var intMarker = _data[offset++];
                if ((intMarker & 0xF0) != 0x10)
                {
                    throw new InvalidDataException("Extended plist length was not encoded as an integer.");
                }

                var byteCount = 1 << (intMarker & 0x0F);
                var length = checked((int)ReadBigEndian(_data, offset, byteCount));
                offset += byteCount;
                return length;
            }
        }

        private static int IntSize(ulong value)
        {
            if (value <= byte.MaxValue) return 1;
            if (value <= ushort.MaxValue) return 2;
            if (value <= uint.MaxValue) return 4;
            return 8;
        }

        private static void WriteBigEndian(Stream output, ulong value, int byteCount)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            output.Write(buffer.Slice(8 - byteCount, byteCount));
        }

        private static ulong ReadBigEndian(byte[] data, int offset, int byteCount)
        {
            ulong value = 0;
            for (var i = 0; i < byteCount; i++)
            {
                value = (value << 8) | data[offset + i];
            }

            return value;
        }
    }
}

namespace WinStream.Core.Protocol.Raop;

/// <summary>
/// Uncompressed ALAC frame encoder used by classic RAOP senders (owntone-style).
/// Input must be little-endian interleaved PCM16 stereo.
/// </summary>
public static class AlacEncoder
{
    public const int FramesPerPacket = 352;
    public const int BytesPerFrame = 4;
    public const int PcmBytesPerPacket = FramesPerPacket * BytesPerFrame;

    public static int GetMaxEncodedLength(int pcmByteCount) =>
        3 + pcmByteCount; // 23-bit header + sample bytes (+ possible pad)

    public static int Encode(ReadOnlySpan<byte> pcmLittleEndianStereo16, Span<byte> destination)
    {
        if (pcmLittleEndianStereo16.Length % BytesPerFrame != 0)
        {
            throw new ArgumentException(
                "PCM buffer must contain an integer number of stereo frames.",
                nameof(pcmLittleEndianStereo16));
        }

        if (destination.Length < GetMaxEncodedLength(pcmLittleEndianStereo16.Length))
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        var writer = new BitWriter(destination);
        writer.WriteBits(1, 3); // channel=1, stereo
        writer.WriteBits(0, 4);
        writer.WriteBits(0, 8);
        writer.WriteBits(0, 4);
        writer.WriteBits(0, 1); // hassize
        writer.WriteBits(0, 2); // unused
        writer.WriteBits(1, 1); // is-not-compressed

        for (var i = 0; i + 3 < pcmLittleEndianStereo16.Length; i += 4)
        {
            // Byteswap each sample to big endian.
            writer.WriteBits(pcmLittleEndianStereo16[i + 1], 8);
            writer.WriteBits(pcmLittleEndianStereo16[i], 8);
            writer.WriteBits(pcmLittleEndianStereo16[i + 3], 8);
            writer.WriteBits(pcmLittleEndianStereo16[i + 2], 8);
        }

        return writer.BytesWritten;
    }

    private ref struct BitWriter
    {
        private readonly Span<byte> _buffer;
        private int _byteIndex;
        private int _bitPosition;

        public BitWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _byteIndex = 0;
            _bitPosition = 0;
            if (_buffer.Length > 0)
            {
                _buffer[0] = 0;
            }
        }

        public int BytesWritten => _bitPosition == 0 ? _byteIndex : _byteIndex + 1;

        public void WriteBits(int value, int bitCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bitCount, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, 8);

            var remainingInByte = 8 - _bitPosition;
            var overflow = remainingInByte - bitCount;
            if (overflow >= 0)
            {
                var shifted = (byte)((value & ((1 << bitCount) - 1)) << overflow);
                if (_bitPosition == 0)
                {
                    _buffer[_byteIndex] = shifted;
                }
                else
                {
                    _buffer[_byteIndex] |= shifted;
                }

                if (overflow == 0)
                {
                    _byteIndex++;
                    _bitPosition = 0;
                    if (_byteIndex < _buffer.Length)
                    {
                        _buffer[_byteIndex] = 0;
                    }
                }
                else
                {
                    _bitPosition += bitCount;
                }
            }
            else
            {
                var high = (byte)((value & ((1 << bitCount) - 1)) >> -overflow);
                _buffer[_byteIndex] |= high;
                _byteIndex++;
                if (_byteIndex >= _buffer.Length)
                {
                    throw new InvalidOperationException("ALAC bit writer overflowed destination.");
                }

                _buffer[_byteIndex] = (byte)((value & ((1 << bitCount) - 1)) << (8 + overflow));
                _bitPosition = -overflow;
            }
        }
    }
}

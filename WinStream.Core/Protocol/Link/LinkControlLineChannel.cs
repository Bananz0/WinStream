using System.Text;

namespace WinStream.Core.Protocol.Link;

/// <summary>
/// Newline framing for the control plane with a hard line ceiling — this listener is
/// reachable by anything on the LAN, so an unterminated line must not grow forever.
/// </summary>
internal sealed class LinkControlLineChannel
{
    public const int MaxLineBytes = 256;

    private readonly Stream _stream;
    private readonly byte[] _line = new byte[MaxLineBytes];
    private readonly byte[] _read = new byte[MaxLineBytes];
    private int _readOffset;
    private int _readLength;
    private int _lineLength;

    public LinkControlLineChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>Null at end of stream; throws when a peer exceeds the line ceiling.</summary>
    public async Task<LinkControlMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_readOffset == _readLength)
            {
                _readLength = await _stream.ReadAsync(_read, cancellationToken).ConfigureAwait(false);
                _readOffset = 0;
                if (_readLength == 0)
                {
                    return _lineLength > 0 ? TakeLine() : null;
                }
            }

            var next = _read[_readOffset++];
            if (next == (byte)'\n')
            {
                return TakeLine();
            }

            if (_lineLength == MaxLineBytes)
            {
                throw new InvalidDataException(
                    $"Link control line exceeded {MaxLineBytes} bytes.");
            }

            _line[_lineLength++] = next;
        }
    }

    public async Task WriteAsync(LinkControlMessage message, CancellationToken cancellationToken)
    {
        var text = message.ToString() + "\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private LinkControlMessage TakeLine()
    {
        var text = Encoding.UTF8.GetString(_line, 0, _lineLength).TrimEnd('\r');
        _lineLength = 0;
        return LinkControlMessage.Parse(text);
    }
}

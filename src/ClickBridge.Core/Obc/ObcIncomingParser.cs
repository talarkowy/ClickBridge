using ClickBridge.Core.Zwift;
using System.Text;

namespace ClickBridge.Core.Obc;

public abstract record ObcIncoming;

/// <summary>0x04 App Information: who connected and which button IDs it understands (empty = all).</summary>
public sealed record ObcAppInfo(byte Version, string AppId, string AppVersion, IReadOnlyList<byte> SupportedButtons) : ObcIncoming
{
    public override string ToString()
    {
        var buttons = SupportedButtons.Count == 0
            ? "all"
            : string.Join(',', SupportedButtons.Select(ObcButton.Name));
        return $"app='{AppId}' version='{AppVersion}' buttons={buttons}";
    }
}

/// <summary>
/// A lone 0x00 byte. Not in the spec, but MyWhoosh sends one every ~4 s, apparently as a keep-alive.
/// </summary>
public sealed record ObcHeartbeat : ObcIncoming;

/// <summary>0x03 Haptic feedback command.</summary>
public sealed record ObcHaptic(byte Pattern, byte Duration, byte Intensity) : ObcIncoming;

/// <summary>Bytes we could not interpret (unknown message type).</summary>
public sealed record ObcUnknown(byte[] Data) : ObcIncoming
{
    public override string ToString() => Hex.Format(Data);
}

/// <summary>
/// Stream parser for app → device messages. The protocol has no framing: the first byte is the type and
/// the length follows from the type, so partial messages are buffered until complete.
/// </summary>
public sealed class ObcIncomingParser
{
    private const int MaxBufferedBytes = 1024;
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<ObcIncoming> Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var results = new List<ObcIncoming>();

        while (_buffer.Count > 0)
        {
            var consumed = TryParse(_buffer.ToArray(), out var message);
            if (consumed == 0)
            {
                break;
            }

            results.Add(message!);
            _buffer.RemoveRange(0, consumed);
        }

        if (_buffer.Count > MaxBufferedBytes)
        {
            results.Add(new ObcUnknown([.. _buffer]));
            _buffer.Clear();
        }

        return results;
    }

    /// <summary>Returns the number of bytes consumed, or 0 when more data is needed.</summary>
    private static int TryParse(byte[] b, out ObcIncoming? message)
    {
        message = null;
        switch (b[0])
        {
            case 0x00:
                message = new ObcHeartbeat();
                return 1;

            case ObcMessageType.Haptic:
                if (b.Length < 4)
                {
                    return 0;
                }

                message = new ObcHaptic(b[1], b[2], b[3]);
                return 4;

            case ObcMessageType.AppInfo:
                return TryParseAppInfo(b, out message);

            default:
                // Without framing we cannot know where an unknown message ends: report what we have.
                message = new ObcUnknown(b);
                return b.Length;
        }
    }

    private static int TryParseAppInfo(byte[] b, out ObcIncoming? message)
    {
        message = null;

        // [0x04][version][idLen][id...][verLen][ver...][count][ids...]
        var pos = 2;
        if (b.Length < pos + 1)
        {
            return 0;
        }

        var idLength = b[pos++];
        if (b.Length < pos + idLength + 1)
        {
            return 0;
        }

        var appId = ReadText(b, pos, idLength);
        pos += idLength;

        var versionLength = b[pos++];
        if (b.Length < pos + versionLength + 1)
        {
            return 0;
        }

        var appVersion = ReadText(b, pos, versionLength);
        pos += versionLength;

        var count = b[pos++];
        if (b.Length < pos + count)
        {
            return 0;
        }

        message = new ObcAppInfo(b[1], appId, appVersion, b.AsSpan(pos, count).ToArray());
        return pos + count;
    }

    /// <summary>
    /// Text from the network ends up in the log and on the console, so control characters (new lines,
    /// terminal escape sequences) are replaced.
    /// </summary>
    private static string ReadText(byte[] b, int index, int count) =>
        string.Create(count == 0 ? 0 : Encoding.UTF8.GetCharCount(b, index, count), (b, index, count), static (span, state) =>
        {
            Encoding.UTF8.GetChars(state.b.AsSpan(state.index, state.count), span);
            for (var i = 0; i < span.Length; i++)
            {
                if (char.IsControl(span[i]))
                {
                    span[i] = '?';
                }
            }
        });
}

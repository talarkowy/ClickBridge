using System.Globalization;
using System.Text;

namespace ClickBridge.Core.Zwift;

/// <summary>
/// Schema-less protobuf pretty-printer for reverse engineering unknown messages, e.g.
/// <c>{1=3, 2={2={1=6, 3=600}}}</c>. Falls back to hex when the bytes are not valid protobuf.
/// </summary>
public static class ProtoDump
{
    private const int MaxDepth = 5;
    private const int MaxFieldNumber = 128;

    public static string Format(ReadOnlySpan<byte> data) =>
        TryFormat(data, 0, out var result) ? result : Hex.Format(data);

    private static bool TryFormat(ReadOnlySpan<byte> data, int depth, out string result)
    {
        result = string.Empty;
        var parts = new List<string>();
        var reader = new ProtoReader(data);
        try
        {
            while (reader.TryReadTag(out var field, out var wireType))
            {
                if (field > MaxFieldNumber)
                {
                    return false;
                }

                var value = wireType switch
                {
                    0 => reader.ReadVarint().ToString(CultureInfo.InvariantCulture),
                    1 => $"0x{reader.ReadFixed(8):X16}",
                    2 => FormatBytes(reader.ReadLengthDelimited(), depth),
                    5 => $"0x{reader.ReadFixed(4):X8}",
                    _ => null,
                };
                if (value is null)
                {
                    return false;
                }

                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{field}={value}"));
            }
        }
        catch (FormatException)
        {
            return false;
        }

        result = "{" + string.Join(", ", parts) + "}";
        return true;
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes, int depth)
    {
        if (!bytes.IsEmpty && IsPrintable(bytes))
        {
            return "\"" + Encoding.ASCII.GetString(bytes) + "\"";
        }

        if (!bytes.IsEmpty && depth < MaxDepth && TryFormat(bytes, depth + 1, out var nested))
        {
            return nested;
        }

        return $"[{bytes.Length}B {Hex.Format(bytes)}]";
    }

    private static bool IsPrintable(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}

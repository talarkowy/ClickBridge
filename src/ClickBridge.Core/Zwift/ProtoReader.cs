namespace ClickBridge.Core.Zwift;

/// <summary>Minimal protobuf wire-format reader — just enough for the few Zwift messages we need.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly bool AtEnd => _pos >= _data.Length;

    public bool TryReadTag(out int field, out int wireType)
    {
        if (AtEnd)
        {
            field = 0;
            wireType = 0;
            return false;
        }

        var tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        if (field == 0)
        {
            throw new FormatException("Invalid protobuf field number 0.");
        }

        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_pos >= _data.Length)
            {
                throw new FormatException("Truncated varint.");
            }

            var b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new FormatException("Varint too long.");
    }

    public static int DecodeZigZag32(ulong value) => (int)((uint)value >> 1) ^ -(int)((uint)value & 1);

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        // Compared as unsigned 64-bit: a huge length must not wrap around when cast or added to the position.
        var declared = ReadVarint();
        if (declared > (ulong)(_data.Length - _pos))
        {
            throw new FormatException("Truncated length-delimited field.");
        }

        var length = (int)declared;

        var slice = _data.Slice(_pos, length);
        _pos += length;
        return slice;
    }

    public ulong ReadFixed(int size)
    {
        if (_pos + size > _data.Length)
        {
            throw new FormatException("Truncated fixed-width field.");
        }

        ulong value = 0;
        for (var i = 0; i < size; i++)
        {
            value |= (ulong)_data[_pos + i] << (8 * i);
        }

        _pos += size;
        return value;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                ReadLengthDelimited();
                break;
            case 5:
                Advance(4);
                break;
            default:
                throw new FormatException($"Unsupported wire type {wireType}.");
        }
    }

    private void Advance(int count)
    {
        if (_pos + count > _data.Length)
        {
            throw new FormatException("Truncated fixed-width field.");
        }

        _pos += count;
    }
}

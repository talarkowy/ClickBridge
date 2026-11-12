namespace ClickBridge.Core.Zwift;

public static class ZwiftMessageParser
{
    private static readonly byte[] StoppedVariant1 = [0xFF, 0x05, 0x00, 0xEA, 0x05];
    private static readonly byte[] StoppedVariant2 = [0xFF, 0x05, 0x00, 0xFA, 0x05];

    public static ZwiftMessage Parse(ReadOnlySpan<byte> frame)
    {
        var raw = frame.ToArray();
        if (frame.IsEmpty)
        {
            return new MalformedMessage(raw, "empty frame");
        }

        try
        {
            if (frame.StartsWith(ZwiftConstants.RideOn))
            {
                var rest = frame[ZwiftConstants.RideOn.Length..];
                var header = rest[..Math.Min(2, rest.Length)].ToArray();
                var payload = rest.Length > 2 ? rest[2..].ToArray() : [];
                return new RideOnResponse(raw, header, payload);
            }

            if (frame.Length > ZwiftConstants.UnlockChallengeHeader.Length && frame.StartsWith(ZwiftConstants.UnlockChallengeHeader))
            {
                return new UnlockChallenge(raw);
            }

            var body = frame[1..];
            return frame[0] switch
            {
                ZwiftOpcodes.ControllerNotification => ParseController(raw, body),
                ZwiftOpcodes.BatteryNotification => new BatteryMessage(raw, ReadUInt32Field(body, 2) is { } p ? (int)p : -1),
                ZwiftOpcodes.BatteryStatus => new BatteryMessage(raw, ReadUInt32Field(body, 2) is { } p2 ? (int)p2 : -1),
                ZwiftOpcodes.Idle => new IdleMessage(raw),
                ZwiftOpcodes.VendorMessage => new VendorMessage(
                    raw, frame.StartsWith(StoppedVariant1) || frame.StartsWith(StoppedVariant2)),
                _ => new OtherMessage(raw, frame[0]),
            };
        }
        catch (FormatException ex)
        {
            return new MalformedMessage(raw, ex.Message);
        }
    }

    private static ControllerNotification ParseController(byte[] raw, ReadOnlySpan<byte> body)
    {
        var reader = new ProtoReader(body);
        uint? map = null;
        var paddles = new List<AnalogPaddle>();

        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0):
                    map = (uint)reader.ReadVarint();
                    break;
                case (3, 2):
                    paddles.Add(ParsePaddle(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        // Inverted bitmap: a cleared bit means the button is held down.
        var pressed = map is { } m ? (ZwiftButtons)(~m & (uint)ZwiftButtons.AllKnown) : ZwiftButtons.None;
        return new ControllerNotification(raw, map.HasValue, map ?? 0, pressed, paddles);
    }

    private static AnalogPaddle ParsePaddle(ReadOnlySpan<byte> data)
    {
        var reader = new ProtoReader(data);
        int location = 0, value = 0;
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0):
                    location = (int)reader.ReadVarint();
                    break;
                case (2, 0):
                    value = ProtoReader.DecodeZigZag32(reader.ReadVarint());
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new AnalogPaddle(location, value);
    }

    private static uint? ReadUInt32Field(ReadOnlySpan<byte> body, int wantedField)
    {
        var reader = new ProtoReader(body);
        uint? result = null;
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == wantedField && wireType == 0)
            {
                result = (uint)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return result;
    }
}

namespace ClickBridge.Core.Zwift;

public abstract record ZwiftMessage(byte[] Raw)
{
    public abstract string Describe();
}

/// <summary>Device echo of the handshake: "RideOn" + 2 header bytes + optional payload (public key on older firmware).</summary>
public sealed record RideOnResponse(byte[] Raw, byte[] Header, byte[] Payload) : ZwiftMessage(Raw)
{
    public override string Describe() =>
        $"RideOn response header={Hex.Format(Header)} payload={Payload.Length}B";
}

public readonly record struct AnalogPaddle(int Location, int Value);

/// <summary>Opcode 0x23: button state of the controller.</summary>
public sealed record ControllerNotification(
    byte[] Raw,
    bool HasButtonMap,
    uint ButtonMap,
    ZwiftButtons Pressed,
    IReadOnlyList<AnalogPaddle> Paddles) : ZwiftMessage(Raw)
{
    public override string Describe()
    {
        var paddles = Paddles.Count == 0
            ? string.Empty
            : " paddles=" + string.Join(',', Paddles.Select(p => $"L{p.Location}:{p.Value}"));
        var map = HasButtonMap ? $"0x{ButtonMap:X8}" : "absent";
        return $"Controller map={map} pressed={Pressed.Describe()}{paddles}";
    }
}

public sealed record BatteryMessage(byte[] Raw, int Percent) : ZwiftMessage(Raw)
{
    public override string Describe() => $"Battery {Percent}%";
}

public sealed record IdleMessage(byte[] Raw) : ZwiftMessage(Raw)
{
    public override string Describe() => "Idle";
}

/// <summary>
/// Opcode 0xFF. BikeControl reports two specific vendor frames from a Click v2 that stops reporting buttons
/// (<c>FF 05 00 EA 05</c> / <c>FF 05 00 FA 05</c>).
/// </summary>
public sealed record VendorMessage(byte[] Raw, bool LooksLikeStopped) : ZwiftMessage(Raw)
{
    public override string Describe() => LooksLikeStopped ? "Vendor message (STOPPED pattern!)" : "Vendor message";
}

public sealed record UnlockChallenge(byte[] Raw) : ZwiftMessage(Raw)
{
    public ReadOnlyMemory<byte> Payload => Raw.AsMemory(ZwiftConstants.UnlockChallengeHeader.Length);

    public override string Describe() => $"Click v2 unlock challenge ({Payload.Length}B)";
}

public sealed record OtherMessage(byte[] Raw, byte Opcode) : ZwiftMessage(Raw)
{
    public override string Describe() => ZwiftOpcodes.Name(Opcode);
}

public sealed record MalformedMessage(byte[] Raw, string Error) : ZwiftMessage(Raw)
{
    public override string Describe() => $"Malformed: {Error}";
}

public static class Hex
{
    /// <summary>Formats bytes as space-separated uppercase hex, e.g. "23 08 FF".</summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}

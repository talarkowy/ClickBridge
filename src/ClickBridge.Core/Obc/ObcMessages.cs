using System.Text;

namespace ClickBridge.Core.Obc;

public static class ObcMessageType
{
    public const byte ButtonState = 0x01;
    public const byte DeviceStatus = 0x02;
    public const byte Haptic = 0x03;
    public const byte AppInfo = 0x04;
}

public readonly record struct ObcButtonState(byte Id, byte State)
{
    public const byte Released = 0x00;
    public const byte Pressed = 0x01;

    public static ObcButtonState Press(byte id) => new(id, Pressed);

    public static ObcButtonState Release(byte id) => new(id, Released);

    public override string ToString() =>
        $"{ObcButton.Name(Id)} {(State == Released ? "released" : State == Pressed ? "pressed" : $"={State}")}";
}

/// <summary>Encoders for messages the device (us) sends to the training app.</summary>
public static class ObcMessages
{
    /// <summary><c>[0x01][id][state]...</c> — sent only when a button changes.</summary>
    public static byte[] ButtonState(IReadOnlyCollection<ObcButtonState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count == 0)
        {
            throw new ArgumentException("At least one button state is required.", nameof(states));
        }

        var message = new byte[1 + (2 * states.Count)];
        message[0] = ObcMessageType.ButtonState;
        var i = 1;
        foreach (var s in states)
        {
            message[i++] = s.Id;
            message[i++] = s.State;
        }

        return message;
    }

    /// <summary><c>[0x02][battery 0-100 | 0xFF][connected 0|1]</c>.</summary>
    public static byte[] DeviceStatus(int batteryPercent, bool connected) =>
    [
        ObcMessageType.DeviceStatus,
        batteryPercent is >= 0 and <= 100 ? (byte)batteryPercent : (byte)0xFF,
        connected ? (byte)0x01 : (byte)0x00,
    ];

    /// <summary>App → device message, encoded here for tests and simulators.</summary>
    public static byte[] AppInfo(string appId, string appVersion, IReadOnlyCollection<byte> supportedButtons)
    {
        var id = Encoding.UTF8.GetBytes(appId);
        var version = Encoding.UTF8.GetBytes(appVersion);
        return
        [
            ObcMessageType.AppInfo, 0x01,
            (byte)id.Length, .. id,
            (byte)version.Length, .. version,
            (byte)supportedButtons.Count, .. supportedButtons,
        ];
    }
}

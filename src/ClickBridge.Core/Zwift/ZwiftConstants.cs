namespace ClickBridge.Core.Zwift;

/// <summary>GATT identifiers used by Zwift controllers (Click, Play, Ride).</summary>
public static class ZwiftUuids
{
    /// <summary>Zwift custom service (older firmware and Click v1/v2).</summary>
    public static readonly Guid CustomService = new("00000001-19ca-4651-86e5-fa29dcdd09d1");

    /// <summary>Zwift custom service announced as a 16-bit UUID (0xFC82) by newer firmware.</summary>
    public static readonly Guid RideCustomService = new("0000fc82-0000-1000-8000-00805f9b34fb");

    /// <summary>Notify: unsolicited messages (button state, battery, ...).</summary>
    public static readonly Guid Async = new("00000002-19ca-4651-86e5-fa29dcdd09d1");

    /// <summary>Write (without response): commands from the app to the controller.</summary>
    public static readonly Guid SyncRx = new("00000003-19ca-4651-86e5-fa29dcdd09d1");

    /// <summary>Indicate: responses to commands.</summary>
    public static readonly Guid SyncTx = new("00000004-19ca-4651-86e5-fa29dcdd09d1");

    public static readonly Guid DeviceInformationService = new("0000180a-0000-1000-8000-00805f9b34fb");
    public static readonly Guid FirmwareRevision = new("00002a26-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryLevel = new("00002a19-0000-1000-8000-00805f9b34fb");

    public static bool IsZwiftCustomService(Guid uuid) => uuid == CustomService || uuid == RideCustomService;
}

public static class ZwiftConstants
{
    /// <summary>Bluetooth SIG company identifier of Zwift, Inc.</summary>
    public const ushort ManufacturerId = 0x094A;

    /// <summary>ASCII "RideOn" — handshake written to <see cref="ZwiftUuids.SyncRx"/> and echoed back by the device.</summary>
    public static ReadOnlySpan<byte> RideOn => "RideOn"u8;

    /// <summary>Handshake for the Click v2: "RideOn" followed by 0x02 0x03.</summary>
    public static byte[] ClickV2StartCommand => [.. RideOn, 0x02, 0x03];
}

/// <summary>Message opcodes (first byte of every frame except the "RideOn" handshake response).</summary>
public static class ZwiftOpcodes
{
    public const byte Get = 0x00;
    public const byte Idle = 0x15; // legacy "empty" message, sent by Click v1 when nothing happens
    public const byte Reset = 0x18;
    public const byte BatteryNotification = 0x19;
    public const byte BatteryStatus = 0x1A;
    public const byte ControllerNotification = 0x23;
    public const byte LogData = 0x2A;
    public const byte LegacyClickNotification = 0x37;
    public const byte GetResponse = 0x3C;
    public const byte StatusResponse = 0x3E;
    public const byte RideOn = 0x52; // 'R'
    public const byte LostControl = 0xFE;
    public const byte VendorMessage = 0xFF;

    public static string Name(byte opcode) => opcode switch
    {
        Get => "GET",
        0x01 => "DEV_INFO_STATUS",
        Idle => "IDLE",
        Reset => "RESET",
        BatteryNotification => "BATTERY_NOTIF",
        BatteryStatus => "BATTERY_STATUS",
        ControllerNotification => "CONTROLLER_NOTIF",
        LogData => "LOG_DATA",
        LegacyClickNotification => "CLICK_NOTIF(v1)",
        GetResponse => "GET_RESPONSE",
        StatusResponse => "STATUS_RESPONSE",
        0x3F => "SET",
        0x40 => "SET_RESPONSE",
        0x42 => "DATA_CHANGE_NOTIF",
        0x43 => "GAME_STATE_NOTIF",
        RideOn => "RIDE_ON",
        LostControl => "LOST_CONTROL",
        VendorMessage => "VENDOR_MESSAGE",
        _ => $"0x{opcode:X2}",
    };
}

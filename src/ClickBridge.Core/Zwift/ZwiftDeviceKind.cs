namespace ClickBridge.Core.Zwift;

public enum ZwiftDeviceKind
{
    Unknown,
    ClickV1,
    ClickV2Left,
    ClickV2Right,
    PlayLeft,
    PlayRight,
    PlayFw2,
    RideLeft,
    RideRight,
}

public static class ZwiftAdvertisement
{
    /// <summary>
    /// Classifies a controller from the payload of its Zwift manufacturer-specific data
    /// (company id already stripped, as Windows reports it).
    /// </summary>
    public static ZwiftDeviceKind Classify(ReadOnlySpan<byte> manufacturerPayload)
    {
        if (manufacturerPayload.IsEmpty)
        {
            return ZwiftDeviceKind.Unknown;
        }

        return manufacturerPayload[0] switch
        {
            0x02 => ZwiftDeviceKind.PlayRight,
            0x03 => ZwiftDeviceKind.PlayLeft,
            0x07 => ZwiftDeviceKind.RideRight,
            0x08 => ZwiftDeviceKind.RideLeft,
            0x09 => ZwiftDeviceKind.ClickV1,
            0x0A => ZwiftDeviceKind.ClickV2Right,
            0x0B => ZwiftDeviceKind.ClickV2Left,
            0x0E => ZwiftDeviceKind.PlayFw2,
            _ => ZwiftDeviceKind.Unknown,
        };
    }

    public static bool IsClickV2(this ZwiftDeviceKind kind) =>
        kind is ZwiftDeviceKind.ClickV2Left or ZwiftDeviceKind.ClickV2Right;

    public static string ShortName(this ZwiftDeviceKind kind) => kind switch
    {
        ZwiftDeviceKind.ClickV2Left => "L",
        ZwiftDeviceKind.ClickV2Right => "R",
        ZwiftDeviceKind.ClickV1 => "C1",
        ZwiftDeviceKind.PlayLeft => "PL",
        ZwiftDeviceKind.PlayRight => "PR",
        ZwiftDeviceKind.PlayFw2 => "P2",
        ZwiftDeviceKind.RideLeft => "RL",
        ZwiftDeviceKind.RideRight => "RR",
        _ => "?",
    };
}

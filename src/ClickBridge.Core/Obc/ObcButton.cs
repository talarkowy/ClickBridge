using System.Globalization;

namespace ClickBridge.Core.Obc;

/// <summary>Standard OpenBikeControl button IDs (https://openbikecontrol.org/protocol).</summary>
public static class ObcButton
{
    public const byte ShiftUp = 0x01;
    public const byte ShiftDown = 0x02;
    public const byte GearSet = 0x03;
    public const byte ChainringSet = 0x04;
    public const byte CassetteSet = 0x05;
    public const byte Up = 0x10;
    public const byte Down = 0x11;
    public const byte Left = 0x12;
    public const byte Right = 0x13;
    public const byte Select = 0x14;
    public const byte Back = 0x15;
    public const byte Menu = 0x16;
    public const byte Home = 0x17;
    public const byte SteerLeft = 0x18;
    public const byte SteerRight = 0x19;
    public const byte Brake = 0x1A;
    public const byte SteeringAngle = 0x1B;
    public const byte Emote = 0x20;
    public const byte PushToTalk = 0x21;
    public const byte Screenshot = 0x24;
    public const byte IncreaseDifficulty = 0x30;
    public const byte DecreaseDifficulty = 0x31;
    public const byte SkipInterval = 0x32;
    public const byte Pause = 0x33;
    public const byte Resume = 0x34;
    public const byte Lap = 0x35;
    public const byte PreviousInterval = 0x36;
    public const byte UTurn = 0x37;
    public const byte ChangeMode = 0x38;
    public const byte TakeABreak = 0x39;
    public const byte JoinRider = 0x3A;
    public const byte ChangeRoute = 0x3B;
    public const byte CruiseControl = 0x3C;
    public const byte CameraView = 0x40;
    public const byte HudToggle = 0x44;
    public const byte MapToggle = 0x45;
    public const byte SpectateRider = 0x46;
    public const byte PowerUp1 = 0x50;
    public const byte PowerUp2 = 0x51;
    public const byte PowerUp3 = 0x52;

    private static readonly Dictionary<byte, string> NamesById = new()
    {
        [ShiftUp] = nameof(ShiftUp),
        [ShiftDown] = nameof(ShiftDown),
        [GearSet] = nameof(GearSet),
        [ChainringSet] = nameof(ChainringSet),
        [CassetteSet] = nameof(CassetteSet),
        [Up] = nameof(Up),
        [Down] = nameof(Down),
        [Left] = nameof(Left),
        [Right] = nameof(Right),
        [Select] = nameof(Select),
        [Back] = nameof(Back),
        [Menu] = nameof(Menu),
        [Home] = nameof(Home),
        [SteerLeft] = nameof(SteerLeft),
        [SteerRight] = nameof(SteerRight),
        [Brake] = nameof(Brake),
        [SteeringAngle] = nameof(SteeringAngle),
        [Emote] = nameof(Emote),
        [PushToTalk] = nameof(PushToTalk),
        [Screenshot] = nameof(Screenshot),
        [IncreaseDifficulty] = nameof(IncreaseDifficulty),
        [DecreaseDifficulty] = nameof(DecreaseDifficulty),
        [SkipInterval] = nameof(SkipInterval),
        [Pause] = nameof(Pause),
        [Resume] = nameof(Resume),
        [Lap] = nameof(Lap),
        [PreviousInterval] = nameof(PreviousInterval),
        [UTurn] = nameof(UTurn),
        [ChangeMode] = nameof(ChangeMode),
        [TakeABreak] = nameof(TakeABreak),
        [JoinRider] = nameof(JoinRider),
        [ChangeRoute] = nameof(ChangeRoute),
        [CruiseControl] = nameof(CruiseControl),
        [CameraView] = nameof(CameraView),
        [HudToggle] = nameof(HudToggle),
        [MapToggle] = nameof(MapToggle),
        [SpectateRider] = nameof(SpectateRider),
        [PowerUp1] = nameof(PowerUp1),
        [PowerUp2] = nameof(PowerUp2),
        [PowerUp3] = nameof(PowerUp3),
    };

    private static readonly Dictionary<string, byte> IdsByName =
        NamesById.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string Name(byte id) =>
        NamesById.TryGetValue(id, out var name) ? name : $"0x{id:X2}";

    /// <summary>Parses a standard name ("ShiftUp", case-insensitive), a hex ("0x14") or a decimal id.</summary>
    public static bool TryParse(string? text, out byte id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (IdsByName.TryGetValue(text, out id))
        {
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
        }

        return byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}

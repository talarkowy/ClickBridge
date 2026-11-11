namespace ClickBridge.Core.Zwift;

/// <summary>
/// Buttons as encoded in the <c>ButtonMap</c> of a controller notification.
/// On the wire the bitmap is inverted: a cleared bit means "pressed".
/// </summary>
[Flags]
public enum ZwiftButtons : uint
{
    None = 0,

    // left controller
    Left = 0x0001,
    Up = 0x0002,
    Right = 0x0004,
    Down = 0x0008,
    ShiftUpLeft = 0x0100, // Click v2: the "−" button on the left puck
    ShiftDownLeft = 0x0200,
    PowerUpLeft = 0x0400,
    OnOffLeft = 0x0800,

    // right controller
    A = 0x0010,
    B = 0x0020,
    Y = 0x0040,
    Z = 0x0080,
    ShiftUpRight = 0x1000, // Click v2: the "+" button on the right puck
    ShiftDownRight = 0x2000,
    PowerUpRight = 0x4000,
    OnOffRight = 0x8000,

    AllKnown = 0xFFFF,
}

public static class ZwiftButtonsExtensions
{
    /// <summary>Human-readable list, e.g. "A+ShiftUpRight", or "-" when empty.</summary>
    public static string Describe(this ZwiftButtons buttons) =>
        buttons == ZwiftButtons.None
            ? "-"
            : string.Join('+', Enum.GetValues<ZwiftButtons>()
                .Where(b => b is not ZwiftButtons.None and not ZwiftButtons.AllKnown && buttons.HasFlag(b)));
}

/// <summary>Detects press/release edges between consecutive controller notifications.</summary>
public sealed class ButtonStateTracker
{
    public ZwiftButtons Current { get; private set; }

    public (ZwiftButtons Pressed, ZwiftButtons Released) Update(ZwiftButtons state)
    {
        var pressed = state & ~Current;
        var released = Current & ~state;
        Current = state;
        return (pressed, released);
    }

    public void Reset() => Current = ZwiftButtons.None;
}

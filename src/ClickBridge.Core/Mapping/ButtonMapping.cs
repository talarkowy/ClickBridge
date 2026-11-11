using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Mapping;

public enum BridgeMode
{
    /// <summary>Connect the left Click v2 puck, which relays the buttons of both pucks.</summary>
    Auto,

    /// <summary>Connect only the right puck (fallback when the left one is locked): ＋ shifts up, B shifts down.</summary>
    RightOnly,
}

/// <summary>Maps Zwift controller buttons to OpenBikeControl button IDs.</summary>
public sealed class ButtonMapping
{
    private readonly Dictionary<ZwiftButtons, byte> _map;

    private ButtonMapping(Dictionary<ZwiftButtons, byte> map) => _map = map;

    public IReadOnlyDictionary<ZwiftButtons, byte> Entries => _map;

    public static ButtonMapping Default(BridgeMode mode)
    {
        var map = new Dictionary<ZwiftButtons, byte>
        {
            // gears: right "+" up, left "−" down
            [ZwiftButtons.ShiftUpRight] = ObcButton.ShiftUp,
            [ZwiftButtons.ShiftUpLeft] = ObcButton.ShiftDown,

            // left puck: navigation diamond
            [ZwiftButtons.Up] = ObcButton.Up,
            [ZwiftButtons.Down] = ObcButton.Down,
            [ZwiftButtons.Left] = ObcButton.Left,
            [ZwiftButtons.Right] = ObcButton.Right,

            // right puck: face buttons
            [ZwiftButtons.A] = ObcButton.Select,
            [ZwiftButtons.B] = ObcButton.Back,
            [ZwiftButtons.Y] = ObcButton.Menu,
            [ZwiftButtons.Z] = ObcButton.CameraView,
        };

        if (mode == BridgeMode.RightOnly)
        {
            map[ZwiftButtons.B] = ObcButton.ShiftDown;
        }

        return new ButtonMapping(map);
    }

    /// <summary>Returns a copy with <paramref name="button"/> mapped to <paramref name="obcId"/> (null = unmapped).</summary>
    public ButtonMapping With(ZwiftButtons button, byte? obcId)
    {
        if (button == ZwiftButtons.None || !Enum.IsDefined(button) || button == ZwiftButtons.AllKnown)
        {
            throw new ArgumentException($"'{button}' is not a single Zwift button.", nameof(button));
        }

        var copy = new Dictionary<ZwiftButtons, byte>(_map);
        if (obcId is { } id)
        {
            copy[button] = id;
        }
        else
        {
            copy.Remove(button);
        }

        return new ButtonMapping(copy);
    }

    /// <summary>OBC IDs that should be "pressed" for the given set of held Zwift buttons.</summary>
    public IEnumerable<byte> Map(ZwiftButtons held)
    {
        foreach (var (button, id) in _map)
        {
            if (held.HasFlag(button))
            {
                yield return id;
            }
        }
    }
}

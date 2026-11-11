using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickBridge.Core.Config;

/// <summary>Settings of the bridge, loaded from an optional <c>clickbridge.json</c>.</summary>
public sealed class BridgeConfig
{
    public const string FileName = "clickbridge.json";

    /// <summary>Name shown in the training app (mDNS instance name).</summary>
    public string DeviceName { get; set; } = "ClickBridge";

    /// <summary>Preferred TCP port; the next free one is used if taken.</summary>
    public int Port { get; set; } = 36867;

    public BridgeMode Mode { get; set; } = BridgeMode.Auto;

    /// <summary>Advertise the service over mDNS (otherwise the app cannot discover it).</summary>
    public bool Mdns { get; set; } = true;

    /// <summary>IPv4 address to advertise; auto-detected when empty.</summary>
    public string? AdvertiseAddress { get; set; }

    /// <summary>How often the device status (battery / connected) is resent.</summary>
    public int StatusIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Overrides of the default mapping: Zwift button name → OBC button (name like "ShiftUp", hex "0x14",
    /// or "None" to disable). Example: <c>{ "Left": "SteerLeft", "Right": "SteerRight" }</c>.
    /// </summary>
    public Dictionary<string, string>? Mapping { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static BridgeConfig Parse(string json) =>
        JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions) ?? new BridgeConfig();

    public static BridgeConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Default mapping for <see cref="Mode"/> plus the overrides from <see cref="Mapping"/>.</summary>
    public ButtonMapping BuildMapping()
    {
        var mapping = ButtonMapping.Default(Mode);
        foreach (var (buttonName, target) in Mapping ?? [])
        {
            if (!Enum.TryParse<ZwiftButtons>(buttonName, ignoreCase: true, out var button)
                || button is ZwiftButtons.None or ZwiftButtons.AllKnown)
            {
                throw new FormatException($"Unknown Zwift button '{buttonName}' in mapping.");
            }

            if (string.Equals(target, "None", StringComparison.OrdinalIgnoreCase))
            {
                mapping = mapping.With(button, null);
            }
            else if (ObcButton.TryParse(target, out var id))
            {
                mapping = mapping.With(button, id);
            }
            else
            {
                throw new FormatException($"Unknown OpenBikeControl button '{target}' for '{buttonName}'.");
            }
        }

        return mapping;
    }
}

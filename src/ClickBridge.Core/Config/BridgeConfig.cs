using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;
using System.Globalization;
using System.Text;
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

    /// <summary>Maximum session length before resetting the left puck in OfflineReset mode.</summary>
    public int OfflineResetSeconds { get; set; } = 55;

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

    /// <summary>
    /// Bluetooth addresses of the controllers the bridge may connect to, e.g. <c>["AA:BB:CC:DD:EE:FF"]</c>.
    /// Empty = any device that advertises as a Zwift controller.
    /// </summary>
    public List<string>? AllowedControllers { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new(JsonOptions) { WriteIndented = true };

    public static BridgeConfig Parse(string json) =>
        JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions) ?? new BridgeConfig();

    public static BridgeConfig Load(string path) => Parse(File.ReadAllText(path));

    public void Save(string path)
    {
        _ = BuildMapping();
        WriteAtomically(path, JsonSerializer.Serialize(this, WriteJsonOptions));
    }

    /// <summary>
    /// Saves only <see cref="Mode"/>. An existing file keeps its comments, layout and other settings;
    /// the whole configuration is written when there is no file or it has no "mode" entry.
    /// </summary>
    public void SaveMode(string path)
    {
        _ = BuildMapping();
        if (File.Exists(path) && ReplaceMode(File.ReadAllBytes(path), Mode) is { } updated)
        {
            WriteAtomically(path, updated);
        }
        else
        {
            Save(path);
        }
    }

    /// <summary>Returns the JSON text with the top-level "mode" value replaced, or null if there is none.</summary>
    internal static string? ReplaceMode(ReadOnlySpan<byte> utf8Json, BridgeMode mode)
    {
        if (utf8Json.StartsWith(Encoding.UTF8.Preamble))
        {
            utf8Json = utf8Json[Encoding.UTF8.Preamble.Length..];
        }

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName
                    || !string.Equals(reader.GetString(), nameof(Mode), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!reader.Read() || reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number))
                {
                    return null;
                }

                var start = (int)reader.TokenStartIndex;
                var end = (int)reader.BytesConsumed;
                return Encoding.UTF8.GetString(utf8Json[..start])
                       + JsonSerializer.Serialize(mode.ToString())
                       + Encoding.UTF8.GetString(utf8Json[end..]);
            }
        }
        catch (JsonException)
        {
            // not valid JSON: let the caller rewrite the file
        }

        return null;
    }

    private static void WriteAtomically(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, fullPath, overwrite: true);
    }

    /// <summary>Parsed <see cref="AllowedControllers"/>; empty when every Zwift controller is accepted.</summary>
    public IReadOnlySet<ulong> ParseAllowedControllers()
    {
        var result = new HashSet<ulong>();
        foreach (var text in AllowedControllers ?? [])
        {
            var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
            if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
            {
                throw new FormatException($"Invalid Bluetooth address '{text}' in allowedControllers (expected AA:BB:CC:DD:EE:FF).");
            }

            result.Add(address);
        }

        return result;
    }

    /// <summary>Checks every setting; throws <see cref="FormatException"/> with a readable message.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new FormatException($"Unknown bridge mode '{Mode}'.");
        }

        if (Port is < 0 or > 65535)
        {
            throw new FormatException("port must be between 0 and 65535.");
        }

        if (OfflineResetSeconds is < 10 or > 60)
        {
            throw new FormatException("offlineResetSeconds must be between 10 and 60.");
        }

        if (StatusIntervalSeconds is < 0 or > 86400)
        {
            throw new FormatException("statusIntervalSeconds must be between 0 and 86400.");
        }

        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Contains('.', StringComparison.Ordinal)
            || DeviceName.Any(char.IsControl) || Encoding.UTF8.GetByteCount(DeviceName) > 63)
        {
            throw new FormatException("deviceName must be 1-63 bytes long, without dots or control characters.");
        }

        _ = ParseAllowedControllers();
    }

    /// <summary>Default mapping for <see cref="Mode"/> plus the overrides from <see cref="Mapping"/>.</summary>
    public ButtonMapping BuildMapping()
    {
        Validate();
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

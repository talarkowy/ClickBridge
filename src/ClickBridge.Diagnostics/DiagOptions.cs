using ClickBridge.Core.Zwift;

namespace ClickBridge.Diagnostics;

internal sealed record DiagOptions
{
    public bool ScanOnly { get; init; }
    public bool NoHandshake { get; init; }
    public ZwiftDeviceKind? OnlyKind { get; init; }
    public string LogDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "logs");
    public TimeSpan SummaryInterval { get; init; } = TimeSpan.FromSeconds(30);

    public const string Usage = """
        click-diag — Zwift Click diagnostics (BLE)

        Usage: click-diag [options]

          --scan-only        Only list Zwift advertisements, do not connect.
          --no-handshake     Connect and subscribe, but do not send "RideOn".
          --side left|right  Connect only the left or the right Click v2 controller.
          --log-dir <path>   Log directory (default: ./logs).
          --summary <sec>    Summary interval in seconds (default: 30, 0 = off).
          -h, --help         Show this help.

        Keys while running:
          S  print summary      H  resend "RideOn" handshake
          M  write a marker     R  force reconnect of all controllers
          Q  quit (also Ctrl+C)
        """;

    /// <summary>Returns null when the help text should be shown; throws on invalid input.</summary>
    public static DiagOptions? Parse(string[] args)
    {
        var options = new DiagOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?":
                    return null;
                case "--scan-only":
                    options = options with { ScanOnly = true };
                    break;
                case "--no-handshake":
                    options = options with { NoHandshake = true };
                    break;
                case "--side":
                    options = options with
                    {
                        OnlyKind = NextValue(args, ref i).ToLowerInvariant() switch
                        {
                            "left" or "l" => ZwiftDeviceKind.ClickV2Left,
                            "right" or "r" => ZwiftDeviceKind.ClickV2Right,
                            var other => throw new ArgumentException($"Unknown side '{other}', expected left or right."),
                        },
                    };
                    break;
                case "--log-dir":
                    options = options with { LogDirectory = Path.GetFullPath(NextValue(args, ref i)) };
                    break;
                case "--summary":
                    var seconds = int.Parse(NextValue(args, ref i), System.Globalization.CultureInfo.InvariantCulture);
                    options = options with { SummaryInterval = TimeSpan.FromSeconds(Math.Max(0, seconds)) };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return options;
    }

    public bool Accepts(ZwiftDeviceKind kind) =>
        kind != ZwiftDeviceKind.Unknown && (OnlyKind is null || OnlyKind == kind);

    private static string NextValue(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
}

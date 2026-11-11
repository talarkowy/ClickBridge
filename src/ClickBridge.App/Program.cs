using System.Globalization;
using ClickBridge.App;
using ClickBridge.Core.Config;
using ClickBridge.Core.Logging;
using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Hosting;

const string usage = """
    clickbridge — Zwift Click v2 -> OpenBikeControl (MyWhoosh) bridge

    Usage: clickbridge [options]

      --config <path>             Settings file (default: clickbridge.json next to the exe or in the current folder).
      --name <text>               Name shown in the training app (default: ClickBridge).
      --port <n>                  Preferred TCP port (default: 36867).
      --right-only                Use only the right Click v2 puck (+ shifts up, B shifts down).
      --zwift-login               Sign in to Zwift and save a session for automatic Click v2 unlocks.
      --zwift-logout              Delete the saved local Zwift session and exit.
      --advertise-address <ip>    IPv4 address to advertise (default: auto-detect).
      --no-mdns                   Do not advertise over mDNS.
      --log-dir <path>            Log directory (default: ./logs).
      -h, --help                  Show this help.

    Keys while running:
      U / D       tap Shift Up / Shift Down (test without the Click)
      arrows      tap Up / Down / Left / Right     Enter / Backspace   tap Select / Back
      S           status                          R                   reconnect controllers
      Q           quit (also Ctrl+C)
    """;

BridgeConfig config;
string logDirectory;
ZwiftSessionAction zwiftSessionAction;
try
{
    (config, logDirectory, zwiftSessionAction) = ParseArguments(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(usage);
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(usage);
    return 2;
}

if (zwiftSessionAction == ZwiftSessionAction.Forget)
{
    try
    {
        ZwiftAccount.ForgetSavedSession();
        Console.WriteLine("Local Zwift session removed.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not remove the local Zwift session ({ex.GetType().Name}).");
        return 1;
    }
}

using var log = new FileConsoleLog(logDirectory, "clickbridge");
log.Info("APP", $"clickbridge started. Log file: {log.FilePath}");

using var account = new ZwiftAccount(log);
if (config.Mode != BridgeMode.RightOnly || zwiftSessionAction == ZwiftSessionAction.SignIn)
{
    try
    {
        if (zwiftSessionAction == ZwiftSessionAction.SignIn)
        {
            await ConsoleZwiftSignIn.SignInAsync(account);
        }
        else
        {
            await account.LoadAsync();
        }
    }
    catch (Exception ex)
    {
        var detail = ex switch
        {
            UnauthorizedAccessException => "Zwift rejected the credentials.",
            HttpRequestException { StatusCode: { } status } => $"Zwift returned HTTP {(int)status}.",
            HttpRequestException => "Could not reach Zwift.",
            OperationCanceledException => "Zwift sign-in timed out.",
            InvalidOperationException => ex.Message,
            _ => $"{ex.GetType().Name}.",
        };
        log.Error("APP", $"Zwift sign-in failed. {detail}");
        return 1;
    }

    log.Info("APP", account.Client is null
        ? "Automatic Click v2 unlock is off. Use --zwift-login to enable it."
        : "Zwift session loaded for automatic Click v2 unlock.");
}

await using var host = new BridgeHost(config, log, account);
try
{
    await host.StartAsync();
}
catch (Exception ex)
{
    log.Error("APP", $"Startup failed: {ex.Message}");
    return 1;
}

log.Info("APP", "Keys: U/D = test shift up/down, arrows/Enter/Backspace = test navigation, S = status, R = reconnect, Q = quit");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var nextSummary = DateTime.Now.AddSeconds(15);
while (!cts.IsCancellationRequested)
{
    if (!Console.IsInputRedirected && Console.KeyAvailable)
    {
        var key = Console.ReadKey(intercept: true).Key;
        byte? tap = key switch
        {
            ConsoleKey.U or ConsoleKey.OemPlus or ConsoleKey.Add => ObcButton.ShiftUp,
            ConsoleKey.D or ConsoleKey.OemMinus or ConsoleKey.Subtract => ObcButton.ShiftDown,
            ConsoleKey.UpArrow => ObcButton.Up,
            ConsoleKey.DownArrow => ObcButton.Down,
            ConsoleKey.LeftArrow => ObcButton.Left,
            ConsoleKey.RightArrow => ObcButton.Right,
            ConsoleKey.Enter => ObcButton.Select,
            ConsoleKey.Backspace => ObcButton.Back,
            _ => null,
        };

        if (tap is { } id)
        {
            await host.TapAsync(id);
        }
        else if (key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            cts.Cancel();
        }
        else if (key == ConsoleKey.S)
        {
            PrintSummary();
        }
        else if (key == ConsoleKey.R)
        {
            await host.ReconnectControllersAsync();
        }
    }

    if (DateTime.Now >= nextSummary)
    {
        PrintSummary();
        nextSummary = DateTime.Now.AddMinutes(1);
    }

    try
    {
        await Task.Delay(50, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // quitting
    }
}

log.Info("APP", "Shutting down...");
return 0;

void PrintSummary()
{
    foreach (var line in host.Summary())
    {
        log.Info("STATUS", line);
    }
}

static (BridgeConfig Config, string LogDirectory, ZwiftSessionAction ZwiftSession) ParseArguments(string[] args)
{
    string? configPath = null;
    var overrides = new List<Action<BridgeConfig>>();
    var logDirectory = Path.Combine(Environment.CurrentDirectory, "logs");
    var zwiftSession = ZwiftSessionAction.Reuse;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h" or "--help" or "/?":
                throw new HelpRequestedException();
            case "--config":
                configPath = Next(args, ref i);
                break;
            case "--name":
                var name = Next(args, ref i);
                overrides.Add(c => c.DeviceName = name);
                break;
            case "--port":
                var port = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                overrides.Add(c => c.Port = port);
                break;
            case "--right-only":
                overrides.Add(c => c.Mode = BridgeMode.RightOnly);
                break;
            case "--zwift-login" or "--zwift-logout":
                var action = args[i] == "--zwift-login" ? ZwiftSessionAction.SignIn : ZwiftSessionAction.Forget;
                if (zwiftSession != ZwiftSessionAction.Reuse && zwiftSession != action)
                {
                    throw new ArgumentException("Choose either --zwift-login or --zwift-logout.");
                }

                zwiftSession = action;
                break;
            case "--advertise-address":
                var address = Next(args, ref i);
                overrides.Add(c => c.AdvertiseAddress = address);
                break;
            case "--no-mdns":
                overrides.Add(c => c.Mdns = false);
                break;
            case "--log-dir":
                logDirectory = Path.GetFullPath(Next(args, ref i));
                break;
            default:
                throw new ArgumentException($"Unknown argument '{args[i]}'.");
        }
    }

    if (zwiftSession == ZwiftSessionAction.Forget)
    {
        return (new BridgeConfig(), logDirectory, zwiftSession);
    }

    configPath ??= new[]
        {
            Path.Combine(AppContext.BaseDirectory, BridgeConfig.FileName),
            Path.Combine(Environment.CurrentDirectory, BridgeConfig.FileName),
        }
        .FirstOrDefault(File.Exists);

    var config = configPath is null ? new BridgeConfig() : BridgeConfig.Load(configPath);
    foreach (var apply in overrides)
    {
        apply(config);
    }

    _ = config.BuildMapping(); // validate early
    if (configPath is not null)
    {
        Console.WriteLine($"Settings: {Path.GetFullPath(configPath)}");
    }

    return (config, logDirectory, zwiftSession);

    static string Next(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}.");
}

internal sealed class HelpRequestedException : Exception;

internal enum ZwiftSessionAction
{
    Reuse,
    SignIn,
    Forget,
}

using ClickBridge.Ble;
using ClickBridge.Core.Logging;
using ClickBridge.Diagnostics;
using System.Collections.Concurrent;

DiagOptions? options;
try
{
    options = DiagOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(DiagOptions.Usage);
    return 2;
}

if (options is null)
{
    Console.WriteLine(DiagOptions.Usage);
    return 0;
}

using var log = new FileConsoleLog(options.LogDirectory, "click-diag");
log.Info("APP", $"click-diag started. Log file: {log.FilePath}");
log.Info("APP", $"Options: scanOnly={options.ScanOnly} noHandshake={options.NoHandshake} side={options.OnlyKind?.ToString() ?? "any"} summary={options.SummaryInterval.TotalSeconds}s");

if (!await ZwiftControllerScanner.CheckAdapterAsync(log))
{
    return 1;
}

log.Info("APP", "Close Rouvy / BikeControl / Zwift first: a controller connected elsewhere does not advertise.");
log.Info("APP", "Keys: S=summary  H=resend RideOn  M=marker  R=reconnect  Q=quit");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var monitors = new ConcurrentDictionary<ulong, DiagMonitor>();
using var scanner = new ZwiftControllerScanner(log);
scanner.Seen += seen =>
{
    if (options.ScanOnly || !options.Accepts(seen.Kind))
    {
        return;
    }

    var monitor = monitors.GetOrAdd(
        seen.Address,
        address => new DiagMonitor(new ZwiftController(address, seen.Kind, log, sendHandshake: !options.NoHandshake), log));
    if (monitor.Controller.ShouldRetry)
    {
        _ = monitor.Controller.EnsureConnectedAsync("advertisement seen");
    }
};
scanner.Start();

var markerCount = 0;
var nextSummary = DateTime.Now + options.SummaryInterval;
while (!cts.IsCancellationRequested)
{
    if (!Console.IsInputRedirected && Console.KeyAvailable)
    {
        switch (Console.ReadKey(intercept: true).Key)
        {
            case ConsoleKey.Q or ConsoleKey.Escape:
                cts.Cancel();
                break;
            case ConsoleKey.S:
                PrintSummary();
                break;
            case ConsoleKey.M:
                log.Event("MARK", $"===== marker #{++markerCount} =====");
                break;
            case ConsoleKey.H:
                foreach (var monitor in monitors.Values)
                {
                    await monitor.Controller.SendHandshakeAsync();
                }

                break;
            case ConsoleKey.R:
                foreach (var monitor in monitors.Values)
                {
                    await monitor.Controller.ForceReconnectAsync();
                }

                break;
        }
    }

    if (options.SummaryInterval > TimeSpan.Zero && DateTime.Now >= nextSummary)
    {
        PrintSummary();
        nextSummary = DateTime.Now + options.SummaryInterval;
    }

    try
    {
        await Task.Delay(100, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // quitting
    }
}

PrintSummary();
scanner.Stop();
foreach (var monitor in monitors.Values)
{
    monitor.Controller.Dispose();
}

log.Info("APP", $"Bye. Log saved to {log.FilePath}");
return 0;

void PrintSummary()
{
    if (monitors.IsEmpty)
    {
        log.Info("SUMMARY", options.ScanOnly ? "Scan-only mode." : "No Zwift controller connected yet.");
        return;
    }

    foreach (var monitor in monitors.Values.OrderBy(m => m.Controller.Kind))
    {
        log.Info("SUMMARY", $"{monitor.Controller.Tag}: {monitor.Summary()}");
    }
}

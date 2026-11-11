using ClickBridge.Core.Config;
using ClickBridge.Core.Logging;
using ClickBridge.Hosting;

namespace ClickBridge.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var openRequested = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, @"Local\ClickBridge.Tray.Open");
        using var instance = new Mutex(initiallyOwned: true, @"Local\ClickBridge.Tray", out var firstInstance);
        if (!firstInstance)
        {
            openRequested.Set();
            return;
        }

        try
        {
            var config = LoadConfig();
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickBridge", "logs");
            using var log = new FileConsoleLog(logDirectory, "clickbridge-tray", writeToConsole: false);
            using var account = new ZwiftAccount(log);
            using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("ClickBridge.ico")!;
            using var icon = new Icon(iconStream);
            // WinForms disposes the application context before Application.Run returns.
            // Bridge shutdown still needs the cancellation source after the UI has closed.
            using var shutdown = new CancellationTokenSource();
            using var app = new TrayApplication(config, account, log, icon, openRequested, shutdown);
            try
            {
                Application.Run(app);
            }
            finally
            {
                app.StopAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Wystąpił błąd ClickBridge.\n{ex.Message}", "ClickBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static BridgeConfig LoadConfig()
    {
        var path = new[]
        {
            Path.Combine(AppContext.BaseDirectory, BridgeConfig.FileName),
            Path.Combine(Environment.CurrentDirectory, BridgeConfig.FileName),
        }.FirstOrDefault(File.Exists);
        return path is null ? new BridgeConfig() : BridgeConfig.Load(path);
    }
}

using ClickBridge.Core.Config;
using ClickBridge.Core.Logging;
using ClickBridge.Hosting;

namespace ClickBridge.Tray;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly ZwiftAccount _account;
    private readonly IBridgeLog _log;
    private readonly BridgeHost _bridge;
    private readonly AccountWindow _window;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripItem _reconnect;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource _shutdown;
    private readonly Task<bool> _startup;
    private Task<string>? _operation;
    private bool _uiDisposed;

    public TrayApplication(BridgeConfig config, ZwiftAccount account, IBridgeLog log, Icon icon,
        EventWaitHandle openRequested, CancellationTokenSource shutdown)
    {
        _account = account;
        _log = log;
        _shutdown = shutdown;
        _bridge = new BridgeHost(config, log, account);
        _window = new AccountWindow(icon);
        _window.SignInRequested += async (_, _) => await SignInAsync();
        _window.SignOutRequested += async (_, _) => await RunAsync(() =>
        {
            _account.SignOut();
            return Task.FromResult("Usunięto lokalną sesję Zwift.");
        });
        _window.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _window.TakePassword();
                _window.Hide();
            }
        };
        _window.FormClosed += (_, _) =>
        {
            if (CanUpdateUi)
            {
                ExitThread();
            }
        };

        _menu.Items.Add("Otwórz ClickBridge", null, (_, _) => OpenWindow());
        _reconnect = _menu.Items.Add("Połącz ponownie kontroler", null, async (_, _) =>
        {
            OpenWindow();
            await RunAsync(async () =>
            {
                await _bridge.ReconnectControllersAsync().ConfigureAwait(false);
                return "Ponowiono połączenie. Naciśnij przycisk na Clicku, aby go wybudzić.";
            });
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Zakończ", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Icon = icon, Text = "ClickBridge", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenWindow();
            }
        };

        _startup = StartAsync();
        _timer.Tick += (_, _) =>
        {
            if (openRequested.WaitOne(0))
            {
                OpenWindow();
            }
            else if (_window.Visible)
            {
                RefreshStatus();
            }
        };
        _menu.Opening += (_, _) => RefreshStatus();
        RefreshStatus();
        _timer.Start();
    }

    private async Task<bool> StartAsync()
    {
        try
        {
            await _account.LoadAsync().ConfigureAwait(false);
            if (_shutdown.IsCancellationRequested)
            {
                return false;
            }

            await _bridge.StartAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("TRAY", $"Bridge startup failed ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    private void OpenWindow()
    {
        RefreshStatus();
        _window.Show();
        _window.Activate();
    }

    private Task SignInAsync()
    {
        var email = _window.Email;
        var password = _window.TakePassword();
        if (email.Length == 0 || password.Length == 0)
        {
            _window.ShowFeedback("Wpisz e-mail i hasło do konta Zwift.", error: true);
            return Task.CompletedTask;
        }

        return RunAsync(async () =>
        {
            var saved = await _account.SignInAsync(email, password, _shutdown.Token).ConfigureAwait(false);
            var message = saved ? "Zalogowano do Zwift." : "Zalogowano. Zapis sesji się nie powiódł.";
            if (BridgeStarted && !_shutdown.IsCancellationRequested)
            {
                try
                {
                    await _bridge.ReconnectLeftControllerAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("TRAY", $"Could not reconnect the left controller ({ex.GetType().Name}).");
                    return message + " Połącz kontroler ponownie z menu ikony.";
                }
            }

            return message;
        }, "Logowanie do Zwift…");
    }

    private async Task RunAsync(Func<Task<string>> action, string pendingMessage = "")
    {
        if (!_startup.IsCompleted || _operation is not null || !CanUpdateUi)
        {
            return;
        }

        _window.ShowFeedback(pendingMessage);
        try
        {
            _operation = action();
            RefreshStatus();
            var message = await _operation;
            if (CanUpdateUi)
            {
                _window.ShowFeedback(message);
            }
        }
        catch (Exception ex)
        {
            if (!CanUpdateUi)
            {
                return;
            }

            var message = ex switch
            {
                UnauthorizedAccessException when pendingMessage.Length > 0 => "Zwift odrzucił e-mail lub hasło.",
                HttpRequestException { StatusCode: { } status } => $"Zwift zwrócił błąd HTTP {(int)status}.",
                HttpRequestException => "Nie udało się połączyć z usługą Zwift.",
                OperationCanceledException => "Przekroczono czas połączenia z usługą Zwift.",
                _ => "Operacja się nie powiodła. Sprawdź log ClickBridge.",
            };
            _log.Warn("TRAY", $"Operation failed ({ex.GetType().Name}).");
            _window.ShowFeedback(message, error: true);
        }
        finally
        {
            _operation = null;
            if (CanUpdateUi)
            {
                RefreshStatus();
            }
        }
    }

    private bool CanUpdateUi => !_uiDisposed && !_shutdown.IsCancellationRequested;

    private bool BridgeStarted => _startup.IsCompletedSuccessfully && _startup.Result;

    private void RefreshStatus()
    {
        var client = _account.Client;
        var sessionStatus = client switch
        {
            { HasSession: true } => "Konto Zwift · sesja dostępna",
            not null => "Sesja Zwift wygasła. Zaloguj się ponownie.",
            _ when !_startup.IsCompleted => "Wczytywanie sesji Zwift…",
            _ => "Zaloguj się do Zwift, aby odblokować lewy Click.",
        };
        var bridgeStatus = !_startup.IsCompleted ? "Uruchamianie mostka…"
            : !BridgeStarted ? "Błąd uruchamiania mostka. Sprawdź log ClickBridge."
            : $"Click: {ControllerStatus()}\nMyWhoosh: {(_bridge.TrainingAppConnected ? "połączony" : "oczekuje na połączenie")}";
        var available = _startup.IsCompleted && _operation is null;
        _window.UpdateStatus(sessionStatus, bridgeStatus, available, client is not null);
        _reconnect.Enabled = available && BridgeStarted && _bridge.BluetoothAvailable;
    }

    private string ControllerStatus() => !_bridge.BluetoothAvailable ? "Bluetooth niedostępny"
        : _bridge.ControllerConnected ? "połączony" : "oczekuje na kontroler";

    protected override void ExitThreadCore()
    {
        if (!CanUpdateUi)
        {
            return;
        }

        _timer.Stop();
        _icon.Visible = false;
        _shutdown.Cancel();
        base.ExitThreadCore();
    }

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        await _startup.ConfigureAwait(false);
        if (_operation is { } pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex) { _log.Verbose("TRAY", $"Operation ended during shutdown ({ex.GetType().Name})."); }
        }

        await _bridge.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_uiDisposed)
        {
            _uiDisposed = true;
            _timer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _window.Dispose();
        }

        base.Dispose(disposing);
    }
}

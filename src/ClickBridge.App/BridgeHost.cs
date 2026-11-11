using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using ClickBridge.Ble;
using ClickBridge.Core.Config;
using ClickBridge.Core.Logging;
using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;
using ClickBridge.Obc;

namespace ClickBridge.App;

/// <summary>Wires Zwift controllers (BLE) to the OpenBikeControl server (TCP + mDNS).</summary>
internal sealed class BridgeHost : IAsyncDisposable
{
    private const string Tag = "BRIDGE";
    private const string ServiceType = "_openbikecontrol._tcp";
    private const string ObcServiceUuid = "d273f680-d548-419d-b9d1-fa0472345229";

    /// <summary>In Auto mode the right puck is used alone only if no left puck shows up for this long.</summary>
    private static readonly TimeSpan LeftPuckGrace = TimeSpan.FromSeconds(10);

    private readonly BridgeConfig _config;
    private readonly IBridgeLog _log;
    private readonly ButtonMapping _mapping;
    private readonly ObcStateTracker _tracker;
    private readonly ObcServer _server;
    private readonly ConcurrentDictionary<ulong, ZwiftController> _controllers = new();
    private readonly ConcurrentDictionary<ZwiftDeviceKind, DateTime> _lastSeen = new();
    private readonly ConcurrentDictionary<ZwiftDeviceKind, bool> _ignoredKindsLogged = new();
    private readonly DateTime _startedAt = DateTime.Now;

    private ZwiftControllerScanner? _scanner;
    private WindowsMdnsAdvertiser? _mdns;

    public BridgeHost(BridgeConfig config, IBridgeLog log)
    {
        _config = config;
        _log = log;
        _mapping = config.BuildMapping();
        _tracker = new ObcStateTracker(_mapping);
        _server = new ObcServer(log, config.Port, TimeSpan.FromSeconds(Math.Max(5, config.StatusIntervalSeconds)));
    }

    public async Task StartAsync()
    {
        _log.Info(Tag, $"Mode: {_config.Mode}. Mapping: " +
                       string.Join(", ", _mapping.Entries.Select(e => $"{e.Key}->{ObcButton.Name(e.Value)}")));

        _server.Start();
        if (_config.Mdns)
        {
            Advertise();
        }
        else
        {
            _log.Warn(Tag, "mDNS disabled: the training app will not discover the bridge automatically.");
        }

        if (await ZwiftControllerScanner.CheckAdapterAsync(_log).ConfigureAwait(false))
        {
            _scanner = new ZwiftControllerScanner(_log);
            _scanner.Seen += OnSeen;
            _scanner.Start();
        }
        else
        {
            _log.Warn(Tag, "Running without Bluetooth: only the test keys will send buttons.");
        }
    }

    /// <summary>Sends a short press of an OBC button (keyboard test mode).</summary>
    public async Task TapAsync(byte obcId)
    {
        _log.Event("TEST", $"Tap {ObcButton.Name(obcId)} -> {_server.Clients.Count} app(s)");
        _server.SendButtonState([ObcButtonState.Press(obcId)]);
        await Task.Delay(120).ConfigureAwait(false);
        _server.SendButtonState([ObcButtonState.Release(obcId)]);
    }

    public async Task ReconnectControllersAsync()
    {
        foreach (var controller in _controllers.Values)
        {
            await controller.ForceReconnectAsync().ConfigureAwait(false);
        }
    }

    public IEnumerable<string> Summary()
    {
        var mdns = _mdns?.IsRegistered == true ? $"advertised as '{_mdns.ServiceName}'" : "not advertised";
        yield return $"Server: port {_server.Port}, {mdns}";

        var clients = _server.Clients;
        if (clients.Count == 0)
        {
            yield return "Apps: none connected (waiting for MyWhoosh)";
        }

        foreach (var client in clients)
        {
            var since = (DateTime.Now - client.ConnectedAt).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var heartbeat = client.LastHeartbeatAt is { } h
                ? $", last keep-alive {(DateTime.Now - h).TotalSeconds:0}s ago"
                : string.Empty;
            yield return $"App {client.Tag}: {client.Remote} {client.AppInfo?.ToString() ?? "(no app info)"} connected {since}{heartbeat}";
        }

        if (_controllers.IsEmpty)
        {
            yield return "Controllers: none yet (press a button on the Click to wake it)";
        }

        foreach (var c in _controllers.Values.OrderBy(c => c.Kind))
        {
            var state = c.IsReady ? "READY" : c.ConnectionStatus?.ToString() ?? "connecting";
            var battery = c.Battery < 0 ? "?" : c.Battery + "%";
            yield return $"Controller {c.Tag}: {state} fw={c.Firmware ?? "?"} battery={battery}";
        }
    }

    private void Advertise()
    {
        IPAddress? address = null;
        if (!string.IsNullOrWhiteSpace(_config.AdvertiseAddress))
        {
            if (!IPAddress.TryParse(_config.AdvertiseAddress, out address))
            {
                _log.Error(Tag, $"Invalid advertiseAddress '{_config.AdvertiseAddress}'.");
                return;
            }
        }
        else
        {
            foreach (var candidate in NetworkAddressPicker.Candidates())
            {
                _log.Verbose(Tag, $"Address candidate: {candidate.Address} on {candidate.Interface} gateway={candidate.HasGateway}");
            }

            address = NetworkAddressPicker.Pick();
        }

        if (address is null)
        {
            _log.Error(Tag, "No usable IPv4 address found - is the computer connected to the network?");
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            _log.Error(Tag, "mDNS advertising needs Windows 10 1809 or newer.");
            return;
        }

        _mdns = new WindowsMdnsAdvertiser(_log);
        var txt = new Dictionary<string, string>
        {
            ["version"] = "1",
            ["id"] = $"clickbridge-{Environment.MachineName.ToLowerInvariant()}",
            ["name"] = _config.DeviceName,
            ["service-uuids"] = ObcServiceUuid,
            ["manufacturer"] = "ClickBridge",
            ["model"] = "Zwift Click v2 bridge",
        };
        _mdns.Register(_config.DeviceName, ServiceType, (ushort)_server.Port, address, txt);
    }

    private void OnSeen(ZwiftAdvertisementSeen seen)
    {
        _lastSeen[seen.Kind] = DateTime.Now;
        if (!ShouldUse(seen.Kind))
        {
            return;
        }

        var controller = _controllers.GetOrAdd(seen.Address, address =>
        {
            var c = new ZwiftController(address, seen.Kind, _log);
            c.ButtonsChanged += OnButtonsChanged;
            c.ReadyChanged += OnReadyChanged;
            c.BatteryChanged += (_, _) => PublishStatus();
            return c;
        });

        if (controller.ShouldRetry)
        {
            _ = controller.EnsureConnectedAsync("advertisement seen");
        }
    }

    private bool ShouldUse(ZwiftDeviceKind kind)
    {
        switch (_config.Mode, kind)
        {
            case (BridgeMode.RightOnly, ZwiftDeviceKind.ClickV2Right):
            case (BridgeMode.Auto, ZwiftDeviceKind.ClickV2Left):
                return true;

            case (BridgeMode.Auto, ZwiftDeviceKind.ClickV2Right):
                // The left puck relays both sides, so the right one alone is only a fallback.
                var leftConnected = _controllers.Values.Any(c => c.Kind == ZwiftDeviceKind.ClickV2Left && c.IsReady);
                var leftRecentlySeen = _lastSeen.TryGetValue(ZwiftDeviceKind.ClickV2Left, out var t) && DateTime.Now - t < LeftPuckGrace;
                var use = !leftConnected && !leftRecentlySeen && DateTime.Now - _startedAt > LeftPuckGrace;
                if (use && _ignoredKindsLogged.TryAdd(kind, true))
                {
                    _log.Warn(Tag, "Only the right Click v2 puck is around - using it alone. Tip: mode RightOnly maps B to shift down.");
                }

                return use;

            default:
                if (_ignoredKindsLogged.TryAdd(kind, true))
                {
                    _log.Info(Tag, $"Ignoring Zwift device of kind {kind} in mode {_config.Mode}.");
                }

                return false;
        }
    }

    private void OnButtonsChanged(ZwiftController controller, ZwiftButtons held, ZwiftButtons pressed, ZwiftButtons released)
    {
        var changes = _tracker.Update(controller.Tag, held);
        var sent = changes.Count == 0 ? "(not mapped)" : string.Join(", ", changes);
        _log.Event(controller.Tag, $"down: {pressed.Describe()} up: {released.Describe()} -> {sent}");
        _server.SendButtonState(changes);
    }

    private void OnReadyChanged(ZwiftController controller, bool ready)
    {
        if (!ready)
        {
            _server.SendButtonState(_tracker.Remove(controller.Tag));
        }

        PublishStatus();
    }

    private void PublishStatus()
    {
        var ready = _controllers.Values.Where(c => c.IsReady).ToList();
        var battery = ready.Where(c => c.Battery >= 0).Select(c => c.Battery).DefaultIfEmpty(-1).Min();
        _server.UpdateStatus(battery, connected: ready.Count > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _scanner?.Dispose();
        foreach (var controller in _controllers.Values)
        {
            controller.Dispose();
        }

        _mdns?.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

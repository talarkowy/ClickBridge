using ClickBridge.Core.Logging;
using ClickBridge.Core.Zwift;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace ClickBridge.Ble;

public sealed record ZwiftAdvertisementSeen(ulong Address, ZwiftDeviceKind Kind, short Rssi, string LocalName, byte[] Payload);

/// <summary>Watches BLE advertisements for Zwift controllers. Which ones to connect is up to the caller.</summary>
public sealed class ZwiftControllerScanner : IDisposable
{
    private readonly IBridgeLog _log;
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly ConcurrentDictionary<ulong, string> _lastPayloadByAddress = new();

    public ZwiftControllerScanner(IBridgeLog log)
    {
        _log = log;
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
            {
                _log.Error("SCAN", $"Watcher stopped: {e.Error}");
            }
        };
    }

    /// <summary>Raised for every advertisement that carries Zwift manufacturer data.</summary>
    public event Action<ZwiftAdvertisementSeen>? Seen;

    /// <summary>Checks that a BLE adapter with central role is present and powered.</summary>
    public static async Task<bool> CheckAdapterAsync(IBridgeLog log)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            log.Error("BLE", "No Bluetooth adapter found.");
            return false;
        }

        var radio = await adapter.GetRadioAsync();
        log.Info("BLE", $"Adapter {adapter.BluetoothAddress:X12}: LE={adapter.IsLowEnergySupported} central={adapter.IsCentralRoleSupported} radio={radio?.State.ToString() ?? "?"}");
        if (!adapter.IsLowEnergySupported || !adapter.IsCentralRoleSupported)
        {
            log.Error("BLE", "The adapter does not support the BLE central role.");
            return false;
        }

        return true;
    }

    public void Start()
    {
        _watcher.Start();
        _log.Info("SCAN", "Scanning for Zwift controllers. Wake them up by pressing a button.");
    }

    public void Stop()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            _watcher.Stop();
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId != ZwiftConstants.ManufacturerId)
            {
                continue;
            }

            var payload = data.Data.ToArray();
            var seen = new ZwiftAdvertisementSeen(
                args.BluetoothAddress,
                ZwiftAdvertisement.Classify(payload),
                args.RawSignalStrengthInDBm,
                args.Advertisement.LocalName,
                payload);

            // Log only when a device is new or its advertised payload changed.
            var hex = Hex.Format(payload);
            if (!_lastPayloadByAddress.TryGetValue(seen.Address, out var previous) || previous != hex)
            {
                _lastPayloadByAddress[seen.Address] = hex;
                _log.Info(
                    "SCAN",
                    $"Zwift device {ZwiftController.FormatAddress(seen.Address)} kind={seen.Kind} rssi={seen.Rssi}dBm data={hex}");
            }

            Seen?.Invoke(seen);
            return;
        }
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        Stop();
    }
}

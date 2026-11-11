using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using ClickBridge.Core.Logging;
using ClickBridge.Core.Zwift;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ClickBridge.Ble;

/// <summary>
/// One BLE connection to one Zwift controller: discovers the Zwift service, subscribes, sends the "RideOn"
/// handshake and raises events for frames, button changes and battery. Re-runs the setup after Windows
/// restores a dropped connection.
/// </summary>
public sealed class ZwiftController : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly IBridgeLog _log;
    private readonly bool _sendHandshake;
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private readonly Lock _frameLock = new();
    private readonly ButtonStateTracker _tracker = new();
    private readonly List<GattDeviceService> _services = [];

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattCharacteristic? _async;
    private GattCharacteristic? _syncRx;
    private GattCharacteristic? _syncTx;
    private volatile bool _ready;
    private bool _disposed;

    public ZwiftController(ulong address, ZwiftDeviceKind kind, IBridgeLog log, bool sendHandshake = true)
    {
        Address = address;
        Kind = kind;
        _log = log;
        _sendHandshake = sendHandshake;
        Tag = $"{kind.ShortName()} {FormatAddress(address)}";
    }

    /// <summary>Every frame received from the controller (any characteristic), already parsed.</summary>
    public event Action<ZwiftController, string, ZwiftMessage>? FrameReceived;

    /// <summary>Held buttons changed: (controller, held, newly pressed, newly released).</summary>
    public event Action<ZwiftController, ZwiftButtons, ZwiftButtons, ZwiftButtons>? ButtonsChanged;

    public event Action<ZwiftController, int>? BatteryChanged;

    /// <summary>Raised when the controller becomes usable (true) or is lost (false).</summary>
    public event Action<ZwiftController, bool>? ReadyChanged;

    public ulong Address { get; }

    public ZwiftDeviceKind Kind { get; }

    public string Tag { get; }

    public DateTime LastAttemptAt { get; private set; } = DateTime.MinValue;

    public bool IsReady => _ready && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public BluetoothConnectionStatus? ConnectionStatus => _device?.ConnectionStatus;

    public int Battery { get; private set; } = -1;

    public string? Firmware { get; private set; }

    /// <summary>True when the Zwift service was not exposed (locked controller), null before the first attempt.</summary>
    public bool? CustomServiceHidden { get; private set; }

    public bool HandshakeAnswered { get; private set; }

    public bool ShouldRetry => !_disposed && !IsReady && DateTime.Now - LastAttemptAt > RetryDelay;

    public async Task EnsureConnectedAsync(string reason)
    {
        if (_disposed || IsReady || !await _setupLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (IsReady)
            {
                return;
            }

            LastAttemptAt = DateTime.Now;
            _log.Info(Tag, $"Connecting ({reason})...");
            await SetupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetReady(false);
            _log.Error(Tag, $"Setup failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _setupLock.Release();
        }
    }

    public async Task ForceReconnectAsync()
    {
        await _setupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _log.Warn(Tag, "Forced reconnect: dropping the connection.");
            DropConnection();
        }
        finally
        {
            _setupLock.Release();
        }

        await EnsureConnectedAsync("manual reconnect").ConfigureAwait(false);
    }

    public async Task SendHandshakeAsync()
    {
        var characteristic = _syncRx;
        if (characteristic is null)
        {
            _log.Warn(Tag, "Cannot send handshake: SyncRx characteristic not available.");
            return;
        }

        byte[] command = Kind.IsClickV2() ? ZwiftConstants.ClickV2StartCommand : ZwiftConstants.RideOn.ToArray();
        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        var status = await characteristic.WriteValueAsync(command.AsBuffer(), option);
        _log.Info(Tag, $"SyncRx <= {Hex.Format(command)} ({option}) -> {status}");
    }

    private async Task SetupAsync()
    {
        ReleaseGatt();

        if (_device is null)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(Address);
            if (_device is null)
            {
                _log.Error(Tag, "BluetoothLEDevice.FromBluetoothAddressAsync returned null.");
                return;
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            _log.Info(Tag, $"Device name='{_device.Name}' paired={_device.DeviceInformation.Pairing.IsPaired}");
        }

        if (_gattSession is null)
        {
            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            _gattSession.MaintainConnection = true;
            _gattSession.SessionStatusChanged += (_, e) => _log.Verbose(Tag, $"GATT session: {e.Status} (error {e.Error})");
        }

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            _log.Error(Tag, $"GetGattServices failed: {servicesResult.Status} protocolError={servicesResult.ProtocolError}");
            return;
        }

        _services.AddRange(servicesResult.Services);
        _log.Verbose(Tag, "Services: " + string.Join(", ", _services.Select(s => s.Uuid)));

        await ReadDeviceInfoAsync().ConfigureAwait(false);

        var custom = _services.FirstOrDefault(s => s.Uuid == ZwiftUuids.RideCustomService)
                     ?? _services.FirstOrDefault(s => s.Uuid == ZwiftUuids.CustomService);
        CustomServiceHidden = custom is null;
        if (custom is null)
        {
            _log.Warn(Tag, "Zwift custom service is NOT exposed -> the controller is probably locked (hidden service).");
            return;
        }

        var charsResult = await custom.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            _log.Error(Tag, $"GetCharacteristics failed: {charsResult.Status}");
            return;
        }

        foreach (var c in charsResult.Characteristics)
        {
            _log.Verbose(Tag, $"  char {c.Uuid} [{c.CharacteristicProperties}]");
        }

        _async = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.Async);
        _syncRx = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.SyncRx);
        _syncTx = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.SyncTx);
        if (_async is null || _syncRx is null || _syncTx is null)
        {
            _log.Error(Tag, "Missing Zwift characteristic(s) (async / syncRx / syncTx).");
            return;
        }

        _async.ValueChanged += OnAsyncValueChanged;
        _syncTx.ValueChanged += OnSyncTxValueChanged;
        await SubscribeAsync(_async, preferIndicate: false).ConfigureAwait(false);
        await SubscribeAsync(_syncTx, preferIndicate: true).ConfigureAwait(false);

        lock (_frameLock)
        {
            _tracker.Reset();
        }

        if (_sendHandshake)
        {
            await SendHandshakeAsync().ConfigureAwait(false);
        }
        else
        {
            _log.Warn(Tag, "Handshake disabled: not sending RideOn.");
        }

        SetReady(true);
    }

    private async Task SubscribeAsync(GattCharacteristic characteristic, bool preferIndicate)
    {
        var props = characteristic.CharacteristicProperties;
        var canIndicate = props.HasFlag(GattCharacteristicProperties.Indicate);
        var canNotify = props.HasFlag(GattCharacteristicProperties.Notify);
        if (!canIndicate && !canNotify)
        {
            _log.Warn(Tag, $"{characteristic.Uuid} supports neither notify nor indicate.");
            return;
        }

        var value = (preferIndicate && canIndicate) || !canNotify
            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
            : GattClientCharacteristicConfigurationDescriptorValue.Notify;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(value);
        var message = $"Subscribe {characteristic.Uuid} ({value}) -> {status}";
        if (status == GattCommunicationStatus.Success)
        {
            _log.Verbose(Tag, message);
        }
        else
        {
            _log.Warn(Tag, message);
        }
    }

    private async Task ReadDeviceInfoAsync()
    {
        try
        {
            var dis = _services.FirstOrDefault(s => s.Uuid == ZwiftUuids.DeviceInformationService);
            if (dis is not null && await ReadAsync(dis, ZwiftUuids.FirmwareRevision).ConfigureAwait(false) is { } fw)
            {
                Firmware = Encoding.UTF8.GetString(fw).TrimEnd('\0');
                _log.Info(Tag, $"Firmware: {Firmware}");
            }

            var bas = _services.FirstOrDefault(s => s.Uuid == ZwiftUuids.BatteryService);
            if (bas is not null && await ReadAsync(bas, ZwiftUuids.BatteryLevel).ConfigureAwait(false) is [var level, ..])
            {
                SetBattery(level);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(Tag, $"Reading device information failed: {ex.Message}");
        }
    }

    private static async Task<byte[]?> ReadAsync(GattDeviceService service, Guid characteristicUuid)
    {
        var chars = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
        {
            return null;
        }

        var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
        return read.Status == GattCommunicationStatus.Success ? read.Value.ToArray() : null;
    }

    private void OnAsyncValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleFrame("ASYNC", args.CharacteristicValue.ToArray());

    private void OnSyncTxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleFrame("SYNCTX", args.CharacteristicValue.ToArray());

    private void HandleFrame(string channel, byte[] data)
    {
        // Serialized so button edges are computed in arrival order.
        lock (_frameLock)
        {
            var message = ZwiftMessageParser.Parse(data);
            switch (message)
            {
                case ControllerNotification controller:
                    var (pressed, released) = _tracker.Update(controller.Pressed);
                    FrameReceived?.Invoke(this, channel, message);
                    if (pressed != ZwiftButtons.None || released != ZwiftButtons.None)
                    {
                        ButtonsChanged?.Invoke(this, controller.Pressed, pressed, released);
                    }

                    return;
                case BatteryMessage battery:
                    SetBattery(battery.Percent);
                    break;
                case RideOnResponse:
                    HandshakeAnswered = true;
                    break;
            }

            FrameReceived?.Invoke(this, channel, message);
        }
    }

    private void SetBattery(int percent)
    {
        if (percent == Battery)
        {
            return;
        }

        Battery = percent;
        _log.Info(Tag, $"Battery {percent}%");
        BatteryChanged?.Invoke(this, percent);
    }

    private void SetReady(bool ready)
    {
        if (_ready == ready)
        {
            return;
        }

        _ready = ready;
        if (ready)
        {
            _log.Event(Tag, "Ready.");
        }
        else
        {
            // Whatever was held is gone with the connection.
            lock (_frameLock)
            {
                _tracker.Reset();
            }
        }

        ReadyChanged?.Invoke(this, ready);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _log.Warn(Tag, "Disconnected.");
            SetReady(false);
        }
        else
        {
            _log.Info(Tag, "Connection restored.");
            if (!_ready)
            {
                _ = EnsureConnectedAsync("connection restored");
            }
        }
    }

    private void ReleaseGatt()
    {
        SetReady(false);
        if (_async is not null)
        {
            _async.ValueChanged -= OnAsyncValueChanged;
        }

        if (_syncTx is not null)
        {
            _syncTx.ValueChanged -= OnSyncTxValueChanged;
        }

        _async = _syncRx = _syncTx = null;
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _services.Clear();
    }

    private void DropConnection()
    {
        ReleaseGatt();
        _gattSession?.Dispose();
        _gattSession = null;
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropConnection();
        _setupLock.Dispose();
    }

    public static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));
}

using ClickBridge.Core.Logging;
using ClickBridge.Core.Zwift;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ClickBridge.Ble;

/// <summary>
/// One BLE connection to one Zwift controller: discovers the Zwift service, subscribes, sends the "RideOn"
/// handshake and raises events for frames, button changes and battery. Re-runs the setup after Windows
/// restores a dropped connection.
/// </summary>
public sealed class ZwiftController : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Unlock requests use the Zwift account, so a misbehaving device must not trigger them in a burst.</summary>
    private static readonly TimeSpan MinUnlockGap = TimeSpan.FromSeconds(3);

    private readonly IBridgeLog _log;
    private readonly bool _sendHandshake;
    private readonly Func<ZwiftUnlockClient?>? _unlockClient;
    private readonly TimeSpan? _offlineResetInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private readonly Lock _frameLock = new();
    private readonly ButtonStateTracker _tracker = new();
    private readonly List<GattDeviceService> _services = [];

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattCharacteristic? _async;
    private GattCharacteristic? _syncRx;
    private GattCharacteristic? _syncTx;
    private CancellationTokenSource? _unlockAttempt;
    private ReadOnlyMemory<byte> _pendingUnlockChallenge;
    private CancellationTokenSource? _resetSession;
    private ClickV2ResetWatchdog? _resetWatchdog;
    private Task _resetTask = Task.CompletedTask;
    private Task? _disposeTask;
    private bool _resetting;
    private long _lastAttemptAt = long.MinValue;
    private long _lastUnlockAt = long.MinValue;
    private volatile bool _ready;
    private volatile bool _disposed;

    public ZwiftController(ulong address, ZwiftDeviceKind kind, IBridgeLog log, bool sendHandshake = true,
        Func<ZwiftUnlockClient?>? unlockClient = null, TimeSpan? offlineResetInterval = null)
    {
        Address = address;
        Kind = kind;
        _log = log;
        _sendHandshake = sendHandshake;
        _offlineResetInterval = kind == ZwiftDeviceKind.ClickV2Left ? offlineResetInterval : null;
        _unlockClient = _offlineResetInterval is null ? unlockClient : null;
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

    public bool IsReady => _ready && ConnectionStatus == BluetoothConnectionStatus.Connected;

    public BluetoothConnectionStatus? ConnectionStatus
    {
        get
        {
            try
            {
                return _device?.ConnectionStatus;
            }
            catch (ObjectDisposedException)
            {
                return null; // read while another thread was dropping the connection
            }
        }
    }

    public int Battery { get; private set; } = -1;

    public string? Firmware { get; private set; }

    /// <summary>True when the Zwift service was not exposed (locked controller), null before the first attempt.</summary>
    public bool? CustomServiceHidden { get; private set; }

    public bool HandshakeAnswered { get; private set; }

    public bool ShouldRetry => !_disposed && !IsReady && ElapsedSince(Volatile.Read(ref _lastAttemptAt)) > RetryDelay;

    // Monotonic clock: wall-clock jumps (DST, time sync) must not delay or rush a retry.
    private static TimeSpan ElapsedSince(long tickCount) =>
        tickCount == long.MinValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(Environment.TickCount64 - tickCount);

    public async Task EnsureConnectedAsync(string reason)
    {
        if (_disposed || IsReady || !await _setupLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_disposed || IsReady)
            {
                return;
            }

            Volatile.Write(ref _lastAttemptAt, Environment.TickCount64);
            _log.Info(Tag, $"Connecting ({reason})...");
            await SetupAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetReady(false);
            if (!_disposed)
            {
                _log.Error(Tag, $"Setup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            try
            {
                if (!_ready && !_disposed)
                {
                    // A half-configured link would stay up (MaintainConnection) and the controller would stop
                    // advertising, so nothing would ever trigger another attempt. Let it advertise again.
                    DropConnection();
                }
            }
            finally
            {
                _setupLock.Release();
            }
        }
    }

    public async Task ForceReconnectAsync()
    {
        await _setupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _log.Warn(Tag, "Forced reconnect: dropping the connection.");
            DropConnection();
        }
        finally
        {
            _setupLock.Release();
        }

        await EnsureConnectedAsync("manual reconnect").ConfigureAwait(false);
    }

    /// <returns>Whether the controller accepted the write.</returns>
    public async Task<bool> SendHandshakeAsync()
    {
        var characteristic = _syncRx;
        if (characteristic is null)
        {
            _log.Warn(Tag, "Cannot send handshake: SyncRx characteristic not available.");
            return false;
        }

        byte[] command = Kind.IsClickV2() ? ZwiftConstants.ClickV2StartCommand : ZwiftConstants.RideOn.ToArray();
        var option = SyncRxWriteOption(characteristic);
        var status = await characteristic.WriteValueAsync(command.AsBuffer(), option).AsTask().ConfigureAwait(false);
        _log.Info(Tag, $"SyncRx <= {Hex.Format(command)} ({option}) -> {status}");
        return status == GattCommunicationStatus.Success;
    }

    private static GattWriteOption SyncRxWriteOption(GattCharacteristic characteristic) =>
        characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

    private async Task SetupAsync()
    {
        ReleaseGatt();

        if (_device is null)
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(Address).AsTask().ConfigureAwait(false);
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
            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask().ConfigureAwait(false);
            _gattSession.MaintainConnection = true;
            _gattSession.SessionStatusChanged += (_, e) => _log.Verbose(Tag, $"GATT session: {e.Status} (error {e.Error})");
        }

        var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
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

        var charsResult = await custom.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            _log.Error(Tag, $"GetCharacteristics failed: {charsResult.Status}");
            return;
        }

        foreach (var c in charsResult.Characteristics)
        {
            _log.Verbose(Tag, $"  char {c.Uuid} [{c.CharacteristicProperties}]");
        }

        var asyncCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.Async);
        var syncRx = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.SyncRx);
        var syncTx = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == ZwiftUuids.SyncTx);
        if (asyncCharacteristic is null || syncRx is null || syncTx is null)
        {
            _log.Error(Tag, "Missing Zwift characteristic(s) (async / syncRx / syncTx).");
            return;
        }

        lock (_frameLock)
        {
            if (_disposed)
            {
                return;
            }

            _async = asyncCharacteristic;
            _syncRx = syncRx;
            _syncTx = syncTx;
            _resetWatchdog = _offlineResetInterval is not null && _sendHandshake ? new ClickV2ResetWatchdog() : null;
            asyncCharacteristic.ValueChanged += OnAsyncValueChanged;
            syncTx.ValueChanged += OnSyncTxValueChanged;
        }

        // Buttons arrive on ASYNC: without that subscription the controller would look ready but stay silent.
        if (!await SubscribeAsync(asyncCharacteristic, preferIndicate: false).ConfigureAwait(false))
        {
            _log.Error(Tag, "Could not subscribe to button notifications.");
            return;
        }

        await SubscribeAsync(syncTx, preferIndicate: true).ConfigureAwait(false);

        lock (_frameLock)
        {
            _tracker.Reset();
        }

        if (_sendHandshake)
        {
            if (!await SendHandshakeAsync().ConfigureAwait(false))
            {
                _log.Error(Tag, "The RideOn handshake was not written.");
                return;
            }

            if (_offlineResetInterval is not null)
            {
                await Task.Delay(300, _lifetime.Token).ConfigureAwait(false);
                var status = await syncRx.WriteValueAsync(ZwiftConstants.ClickV2OfflineStartCommand.AsBuffer(), SyncRxWriteOption(syncRx)).AsTask().ConfigureAwait(false);
                _log.Info(Tag, $"Offline start FF 04 00 -> {status} (unsigned; no Zwift request).");
                if (status != GattCommunicationStatus.Success)
                {
                    return;
                }
            }
        }
        else
        {
            _log.Warn(Tag, "Handshake disabled: not sending RideOn.");
        }

        lock (_frameLock)
        {
            if (_disposed || _syncRx != syncRx || ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                return;
            }

            SetReady(true);
            if (_offlineResetInterval is { } interval && _sendHandshake)
            {
                var session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var watchdog = _resetWatchdog ?? new ClickV2ResetWatchdog();
                _resetSession = session;
                _resetWatchdog = watchdog;
                _log.Info(Tag, $"Offline reset armed: STOPPED or {interval.TotalSeconds:0}s. Both pucks may pause during restart.");
                _resetTask = Task.Run(() => RunOfflineResetAsync(syncRx, session, watchdog, interval));
            }
        }
    }

    private async Task RunOfflineResetAsync(GattCharacteristic rx, CancellationTokenSource session,
        ClickV2ResetWatchdog watchdog, TimeSpan interval)
    {
        try
        {
            var reason = await watchdog.WaitAsync(interval, session.Token).ConfigureAwait(false);
            await _setupLock.WaitAsync(session.Token).ConfigureAwait(false);
            try
            {
                Task<GattCommunicationStatus> write;
                lock (_frameLock)
                {
                    session.Token.ThrowIfCancellationRequested();
                    if (_disposed || _resetSession != session || _syncRx != rx || !IsReady)
                    {
                        return;
                    }

                    _resetting = true;
                    SetReady(false); // releases all OBC buttons before rebooting the hub
                    _log.Warn(Tag, $"Offline RESET ({reason}). Releasing buttons and restarting the left hub.");
                }

                try
                {
                    lock (_frameLock)
                    {
                        session.Token.ThrowIfCancellationRequested();
                        write = rx.WriteValueAsync(new byte[] { ZwiftOpcodes.Reset }.AsBuffer(), SyncRxWriteOption(rx)).AsTask(session.Token);
                    }

                    var status = await write.ConfigureAwait(false);
                    _log.Info(Tag, $"Offline RESET 18 -> {status}.");
                    await Task.Delay(1000, session.Token).ConfigureAwait(false);
                }
                finally
                {
                    DropConnection();
                }
            }
            finally
            {
                _setupLock.Release();
            }

            await EnsureConnectedAsync("offline reset completed").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Reconnect, disconnect or shutdown invalidated this BLE session.
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _log.Warn(Tag, $"Offline reset failed ({ex.GetType().Name}). Waiting for a new advertisement to reconnect.");
            }
        }
        finally
        {
            lock (_frameLock)
            {
                if (_resetSession == session)
                {
                    _resetSession = null;
                    _resetWatchdog = null;
                }

                session.Dispose();
            }
        }
    }

    // Caller holds _frameLock. The task owns and disposes its cancellation source.
    private void CancelOfflineReset()
    {
        var session = _resetSession;
        _resetSession = null;
        _resetWatchdog = null;
        session?.Cancel();
    }

    private async Task<bool> SubscribeAsync(GattCharacteristic characteristic, bool preferIndicate)
    {
        var props = characteristic.CharacteristicProperties;
        var canIndicate = props.HasFlag(GattCharacteristicProperties.Indicate);
        var canNotify = props.HasFlag(GattCharacteristicProperties.Notify);
        if (!canIndicate && !canNotify)
        {
            _log.Warn(Tag, $"{characteristic.Uuid} supports neither notify nor indicate.");
            return false;
        }

        var value = (preferIndicate && canIndicate) || !canNotify
            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
            : GattClientCharacteristicConfigurationDescriptorValue.Notify;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(value).AsTask().ConfigureAwait(false);
        var message = $"Subscribe {characteristic.Uuid} ({value}) -> {status}";
        if (status == GattCommunicationStatus.Success)
        {
            _log.Verbose(Tag, message);
        }
        else
        {
            _log.Warn(Tag, message);
        }

        return status == GattCommunicationStatus.Success;
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
        var chars = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
        {
            return null;
        }

        var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
        return read.Status == GattCommunicationStatus.Success ? read.Value.ToArray() : null;
    }

    private void OnAsyncValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleFrame(sender, "ASYNC", args.CharacteristicValue.ToArray());

    private void OnSyncTxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        HandleFrame(sender, "SYNCTX", args.CharacteristicValue.ToArray());

    private void HandleFrame(GattCharacteristic source, string channel, byte[] data)
    {
        // Serialized so button edges are computed in arrival order.
        lock (_frameLock)
        {
            if (_disposed || (source != _async && source != _syncTx))
            {
                return;
            }

            var message = ZwiftMessageParser.Parse(data);
            switch (message)
            {
                case ControllerNotification controller:
                    if (_resetting)
                    {
                        return;
                    }

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
                case UnlockChallenge challenge when source == _async:
                    StartUnlock(challenge.Payload);
                    break;
                case VendorMessage { LooksLikeStopped: true }:
                    _resetWatchdog?.NotifyStopped();
                    break;
            }

            FrameReceived?.Invoke(this, channel, message);
        }
    }

    // Called under _frameLock. The pending reply belongs to the current GATT binding and latest challenge.
    private void StartUnlock(ReadOnlyMemory<byte> payload)
    {
        if (_unlockClient?.Invoke() is not { } client || _syncRx is not { } rx
            || ConnectionStatus != BluetoothConnectionStatus.Connected
            || payload.Span.SequenceEqual(_pendingUnlockChallenge.Span))
        {
            return;
        }

        if (ElapsedSince(_lastUnlockAt) < MinUnlockGap)
        {
            _log.Warn(Tag, "Unlock challenge ignored: it came too soon after the previous one.");
            return;
        }

        _lastUnlockAt = Environment.TickCount64;
        CancelUnlock();
        var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var challenge = payload.ToArray();
        _unlockAttempt = attempt;
        _pendingUnlockChallenge = challenge;
        _log.Info(Tag, "Click v2 unlock challenge received. Requesting a signed reply from Zwift.");
        _ = Task.Run(() => SendUnlockReplyAsync(client, rx, challenge, attempt));
    }

    private async Task SendUnlockReplyAsync(ZwiftUnlockClient client, GattCharacteristic rx,
        ReadOnlyMemory<byte> payload, CancellationTokenSource attempt)
    {
        try
        {
            var reply = await client.SolveAsync(payload, attempt.Token).ConfigureAwait(false);
            if (reply is null)
            {
                LogUnlockResult(attempt, _log.Info, "Zwift returned HTTP 204 (No Content). No signed unlock reply to send.");
                return;
            }

            var frame = ZwiftConstants.CreateUnlockReply(reply);
            Task<GattCommunicationStatus> write;
            lock (_frameLock)
            {
                if (_disposed || _unlockAttempt != attempt || _syncRx != rx || _unlockClient?.Invoke() != client
                    || ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    return;
                }

                attempt.Token.ThrowIfCancellationRequested();
                _log.Info(Tag, $"Received signed unlock reply from Zwift (HTTP 200, {reply.Length}B).");
                write = rx.WriteValueAsync(frame.AsBuffer(), SyncRxWriteOption(rx)).AsTask(attempt.Token);
            }

            var status = await write.ConfigureAwait(false);
            var sent = status == GattCommunicationStatus.Success;
            LogUnlockResult(attempt, sent ? _log.Event : _log.Warn, sent ? $"Signed unlock reply sent to SyncRx ({frame.Length}B)."
                : $"Could not write the signed unlock reply to SyncRx ({status}).");
        }
        catch (Exception ex)
        {
            var warning = ex switch
            {
                UnauthorizedAccessException or HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
                    => "Zwift session rejected. Sign in to Zwift again.",
                HttpRequestException { StatusCode: { } status } => $"Zwift unlock request failed (HTTP {(int)status}).",
                OperationCanceledException => "Zwift unlock attempt timed out.",
                _ => $"Zwift unlock attempt failed ({ex.GetType().Name}).",
            };
            LogUnlockResult(attempt, _log.Warn, warning);
        }
        finally
        {
            lock (_frameLock)
            {
                if (_unlockAttempt == attempt)
                {
                    _unlockAttempt = null;
                    _pendingUnlockChallenge = default;
                }

                attempt.Dispose();
            }
        }
    }

    private void LogUnlockResult(CancellationTokenSource attempt, Action<string, string> log, string message)
    {
        lock (_frameLock)
        {
            if (_unlockAttempt != attempt || _disposed)
            {
                return;
            }

            log(Tag, message);
        }
    }

    private void CancelUnlock()
    {
        var attempt = _unlockAttempt;
        _unlockAttempt = null;
        _pendingUnlockChallenge = default;
        attempt?.Cancel();
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
        if (_disposed || sender != _device)
        {
            return;
        }

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            lock (_frameLock)
            {
                CancelUnlock();
                if (!_resetting)
                {
                    CancelOfflineReset();
                }
            }

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
        GattCharacteristic? asyncCharacteristic;
        GattCharacteristic? syncTx;
        lock (_frameLock)
        {
            CancelUnlock();
            CancelOfflineReset();
            _resetting = false;
            asyncCharacteristic = _async;
            syncTx = _syncTx;
            _async = _syncRx = _syncTx = null;
        }

        SetReady(false);
        if (asyncCharacteristic is not null)
        {
            asyncCharacteristic.ValueChanged -= OnAsyncValueChanged;
        }

        if (syncTx is not null)
        {
            syncTx.ValueChanged -= OnSyncTxValueChanged;
        }

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

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_frameLock)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _lifetime.Cancel();
            CancelUnlock();
            CancelOfflineReset();
            var resetTask = _resetTask;
            _disposeTask = Task.Run(async () =>
            {
                await resetTask.ConfigureAwait(false);
                await _setupLock.WaitAsync().ConfigureAwait(false);
                try { DropConnection(); }
                finally { _setupLock.Release(); }
                _lifetime.Dispose();
            });
            return new ValueTask(_disposeTask);
        }
    }

    public static string FormatAddress(ulong address) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));
}

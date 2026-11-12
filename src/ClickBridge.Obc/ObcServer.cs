using ClickBridge.Core.Logging;
using ClickBridge.Core.Obc;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ClickBridge.Obc;

/// <summary>
/// OpenBikeControl device side over TCP: training apps (MyWhoosh, ...) connect as clients and receive
/// button state (0x01) and device status (0x02) messages.
/// </summary>
public sealed class ObcServer : IAsyncDisposable
{
    private const string Tag = "OBC";
    private const int PortAttempts = 10;

    /// <summary>Anyone on the LAN can connect, so the number of simultaneous apps is capped.</summary>
    public const int MaxClients = 8;

    private readonly IBridgeLog _log;
    private readonly int _preferredPort;
    private readonly TimeSpan _statusInterval;
    private readonly ConcurrentDictionary<int, ObcClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _statusLock = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _statusLoop;
    private byte[] _status = ObcMessages.DeviceStatus(-1, connected: false);
    private int _nextClientId;

    /// <param name="preferredPort">Port to listen on; 0 picks a free one.</param>
    public ObcServer(IBridgeLog log, int preferredPort, TimeSpan statusInterval)
    {
        _log = log;
        _preferredPort = preferredPort;
        _statusInterval = statusInterval;
    }

    /// <summary>The port actually bound (may differ from the preferred one if it was taken).</summary>
    public int Port { get; private set; }

    public IReadOnlyCollection<ObcClientConnection> Clients => [.. _clients.Values];

    public event Action<ObcClientConnection>? ClientConnected;

    public event Action<ObcClientConnection>? ClientDisconnected;

    public void Start()
    {
        _listener = Bind();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log.Info(Tag, $"Listening on TCP port {Port}.");
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _statusLoop = StatusLoopAsync(_cts.Token);
    }

    public void SendButtonState(IReadOnlyCollection<ObcButtonState> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        Broadcast(ObcMessages.ButtonState(changes));
    }

    /// <summary>Updates the device status; it is broadcast right away when it changed.</summary>
    public void UpdateStatus(int batteryPercent, bool connected)
    {
        var message = ObcMessages.DeviceStatus(batteryPercent, connected);
        lock (_statusLock)
        {
            if (message.AsSpan().SequenceEqual(_status))
            {
                return;
            }

            _status = message;
        }

        Broadcast(message);
    }

    private byte[] CurrentStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    private void Broadcast(byte[] message)
    {
        foreach (var client in _clients.Values)
        {
            client.Send(message);
        }
    }

    private TcpListener Bind()
    {
        var attempts = _preferredPort == 0 ? 1 : PortAttempts;
        for (var i = 0; i < attempts && _preferredPort + i <= IPEndPoint.MaxPort; i++)
        {
            var port = _preferredPort == 0 ? 0 : _preferredPort + i;
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                _log.Warn(Tag, $"Port {port} unavailable ({ex.SocketErrorCode}), trying the next one.");
                listener.Dispose();
            }
        }

        throw new InvalidOperationException($"No free TCP port in {_preferredPort}..{_preferredPort + attempts - 1}.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _log.Warn(Tag, $"Accept failed: {ex.Message}");
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); // no hot loop on a broken listener
                continue;
            }

            if (_clients.Count >= MaxClients)
            {
                _log.Warn(Tag, $"Rejected a connection from {client.Client.RemoteEndPoint}: {MaxClients} apps are already connected.");
                client.Dispose();
                continue;
            }

            var connection = new ObcClientConnection(Interlocked.Increment(ref _nextClientId), client, _log, OnClientClosed);
            _clients[connection.Id] = connection;
            _log.Event(connection.Tag, $"App connected from {connection.Remote}.");
            connection.Send(CurrentStatus());
            connection.Start(ct);
            ClientConnected?.Invoke(connection);
        }
    }

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_statusInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Broadcast(CurrentStatus());
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void OnClientClosed(ObcClientConnection connection)
    {
        if (_clients.TryRemove(connection.Id, out _))
        {
            var app = connection.AppInfo?.AppId ?? "unknown app";
            _log.Warn(connection.Tag, $"App disconnected ({app}, {connection.Remote}).");
            ClientDisconnected?.Invoke(connection);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        foreach (var task in new[] { _acceptLoop, _statusLoop })
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }

        _listener?.Dispose();
        _cts.Dispose();
    }
}

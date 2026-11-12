using ClickBridge.Core.Logging;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ClickBridge.Obc;

/// <summary>One connected training app (TCP client). Outgoing messages are queued and written one by one.</summary>
public sealed class ObcClientConnection : IDisposable
{
    /// <summary>
    /// The protocol has no framing, so two messages arriving in one TCP read could be merged by the app.
    /// A short gap between writes keeps each message in its own segment.
    /// </summary>
    private static readonly TimeSpan MinWriteGap = TimeSpan.FromMilliseconds(15);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly IBridgeLog _log;
    private readonly Action<ObcClientConnection> _onClosed;
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    internal ObcClientConnection(int id, TcpClient client, IBridgeLog log, Action<ObcClientConnection> onClosed)
    {
        Id = id;
        _client = client;
        _client.NoDelay = true;
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _stream = client.GetStream();
        _log = log;
        _onClosed = onClosed;
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Tag = $"OBC #{id}";
    }

    public int Id { get; }

    public string Remote { get; }

    public string Tag { get; }

    public DateTime ConnectedAt { get; } = DateTime.Now;

    /// <summary>What the app told us about itself (0x04), if it did.</summary>
    public ObcAppInfo? AppInfo { get; private set; }

    /// <summary>Last keep-alive byte (0x00) from the app; MyWhoosh sends one every ~4 s.</summary>
    public DateTime? LastHeartbeatAt { get; private set; }

    public void Send(byte[] message)
    {
        if (!_outbound.Writer.TryWrite(message))
        {
            _log.Warn(Tag, "Outgoing queue full (app not reading?) - closing the connection.");
            Close();
        }
    }

    internal void Start(CancellationToken serverToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAny(ReadLoopAsync(linked.Token), WriteLoopAsync(linked.Token)).ConfigureAwait(false);
            }
            finally
            {
                linked.Dispose();
                Close();
            }
        }, CancellationToken.None); // the cleanup above must run even when the server is stopping
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var parser = new ObcIncomingParser();
        var buffer = new byte[512];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                _log.Verbose(Tag, $"<= {Hex.Format(buffer.AsSpan(0, read))}");
                foreach (var message in parser.Append(buffer.AsSpan(0, read)))
                {
                    switch (message)
                    {
                        case ObcAppInfo appInfo:
                            AppInfo = appInfo;
                            _log.Event(Tag, $"App info: {appInfo}");
                            break;
                        case ObcHeartbeat:
                            LastHeartbeatAt = DateTime.Now;
                            break;
                        case ObcHaptic haptic:
                            _log.Verbose(Tag, $"Haptic request ignored (Click v2 cannot vibrate): {haptic}");
                            break;
                        case ObcUnknown unknown:
                            _log.Warn(Tag, $"Unknown message from app: {unknown}");
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // connection closed
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _stream.WriteAsync(message, ct).ConfigureAwait(false);
                _log.Verbose(Tag, $"=> {Hex.Format(message)}");
                await Task.Delay(MinWriteGap, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // connection closed
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        _cts.Cancel();
        _client.Dispose();
        _onClosed(this);
    }

    // _cts is deliberately not disposed: a loop may still observe its token, and it owns no timer or handle.
    public void Dispose() => Close();
}

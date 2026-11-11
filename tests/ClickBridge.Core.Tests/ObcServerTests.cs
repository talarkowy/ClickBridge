using System.Net;
using System.Net.Sockets;
using ClickBridge.Core.Logging;
using ClickBridge.Core.Obc;
using ClickBridge.Obc;

namespace ClickBridge.Core.Tests;

public sealed class ObcServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ObcServer _server = new(new NullLog(), preferredPort: 0, statusInterval: TimeSpan.FromMinutes(5));
    private readonly TcpClient _client = new();
    private NetworkStream _stream = null!;

    public async Task InitializeAsync()
    {
        _server.Start();
        await _client.ConnectAsync(IPAddress.Loopback, _server.Port);
        _stream = _client.GetStream();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task NewClient_GetsCurrentStatus()
    {
        Assert.Equal([0x02, 0xFF, 0x00], await ReadAsync(3));
    }

    [Fact]
    public async Task ButtonState_AndStatusChanges_AreBroadcast()
    {
        await ReadAsync(3); // initial status
        await WaitUntilAsync(() => _server.Clients.Count == 1);

        _server.SendButtonState([ObcButtonState.Press(ObcButton.ShiftUp)]);
        Assert.Equal([0x01, 0x01, 0x01], await ReadAsync(3));

        _server.UpdateStatus(85, connected: true);
        Assert.Equal([0x02, 0x55, 0x01], await ReadAsync(3));
    }

    [Fact]
    public async Task UnchangedStatus_IsNotResent()
    {
        await ReadAsync(3);
        await WaitUntilAsync(() => _server.Clients.Count == 1);

        _server.UpdateStatus(-1, connected: false); // same as the initial status
        _server.SendButtonState([ObcButtonState.Release(ObcButton.ShiftDown)]);

        Assert.Equal([0x01, 0x02, 0x00], await ReadAsync(3));
    }

    [Fact]
    public async Task AppInfo_FromClient_IsRecorded()
    {
        await _stream.WriteAsync(ObcMessages.AppInfo("mywhoosh", "5.0", [0x01, 0x02]));

        await WaitUntilAsync(() => _server.Clients.FirstOrDefault()?.AppInfo is not null);
        Assert.Equal("mywhoosh", _server.Clients.Single().AppInfo!.AppId);
    }

    [Fact]
    public async Task DisconnectedClient_IsRemoved()
    {
        await WaitUntilAsync(() => _server.Clients.Count == 1);

        _client.Client.Shutdown(SocketShutdown.Both);
        _client.Close();

        await WaitUntilAsync(() => _server.Clients.Count == 0);
    }

    private async Task<byte[]> ReadAsync(int count)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[count];
        await _stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class NullLog : IBridgeLog
    {
        public void Verbose(string tag, string message) { }

        public void Info(string tag, string message) { }

        public void Event(string tag, string message) { }

        public void Warn(string tag, string message) { }

        public void Error(string tag, string message) { }
    }
}

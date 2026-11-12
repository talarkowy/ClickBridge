using ClickBridge.Core.Config;
using ClickBridge.Core.Mapping;
using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Tests;

public class ClickV2ResetWatchdogTests
{
    [Fact]
    public async Task StoppedEndsSessionEarly_AndDuplicateSignalsAreHarmless()
    {
        var watchdog = new ClickV2ResetWatchdog();
        var pending = watchdog.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        watchdog.NotifyStopped();
        watchdog.NotifyStopped();
        Assert.Equal("STOPPED received", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SessionTimeLimitTriggersWithoutStoppedOrButtonEvents()
    {
        var watchdog = new ClickV2ResetWatchdog();
        Assert.Equal("session time limit", await watchdog.WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task DisconnectCancelsPendingReset_AndNextSessionStartsFresh()
    {
        var old = new ClickV2ResetWatchdog();
        using var disconnected = new CancellationTokenSource();
        var pending = old.WaitAsync(TimeSpan.FromMinutes(1), disconnected.Token);
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var next = new ClickV2ResetWatchdog();
        using var nextDisconnected = new CancellationTokenSource();
        var nextPending = next.WaitAsync(TimeSpan.FromMinutes(1), nextDisconnected.Token);
        old.NotifyStopped();
        Assert.False(nextPending.IsCompleted);
        nextDisconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextPending);
    }

    [Fact]
    public async Task ShutdownWinsOverAnAlreadyReceivedStoppedMessage()
    {
        var watchdog = new ClickV2ResetWatchdog();
        watchdog.NotifyStopped();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            watchdog.WaitAsync(TimeSpan.Zero, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void HubResetReleasesButtonsFromBothPucks_AndAllowsTheNextPress()
    {
        var config = BridgeConfig.Parse("""{"mode":"OfflineReset", "offlineResetSeconds":55}""");
        var tracker = new ObcStateTracker(config.BuildMapping());
        Assert.Equal(BridgeMode.OfflineReset, config.Mode);
        Assert.Equal([ObcButton.Back], config.BuildMapping().Map(ZwiftButtons.B));
        Assert.Equal(2, tracker.Update("hub", ZwiftButtons.ShiftUpLeft | ZwiftButtons.ShiftUpRight).Count);
        var released = tracker.Remove("hub");
        Assert.Contains(ObcButtonState.Release(ObcButton.ShiftDown), released);
        Assert.Contains(ObcButtonState.Release(ObcButton.ShiftUp), released);
        Assert.Empty(tracker.Remove("hub"));
        Assert.Equal([ObcButtonState.Press(ObcButton.ShiftDown)], tracker.Update("hub", ZwiftButtons.ShiftUpLeft));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(61)]
    public void InvalidResetIntervalsAreRejected(int seconds)
    {
        Assert.Throws<FormatException>(() => new BridgeConfig { OfflineResetSeconds = seconds }.BuildMapping());
    }

    [Fact]
    public void SavingModePreservesOtherSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clickbridge-config-{Guid.NewGuid():N}.json");
        try
        {
            var config = BridgeConfig.Parse("""{"mode":"Auto","port":43210,"mapping":{"A":"Menu"}}""");
            config.Mode = BridgeMode.OfflineReset;
            config.Save(path);
            var restored = BridgeConfig.Load(path);
            Assert.Equal(BridgeMode.OfflineReset, restored.Mode);
            Assert.Equal(55, restored.OfflineResetSeconds);
            Assert.Equal(43210, restored.Port);
            Assert.Equal([ObcButton.Menu], restored.BuildMapping().Map(ZwiftButtons.A));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

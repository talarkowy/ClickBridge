namespace ClickBridge.Core.Zwift;

/// <summary>A single BLE session ends on STOPPED or the configured time limit, never on button inactivity.</summary>
public sealed class ClickV2ResetWatchdog
{
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void NotifyStopped() => _stopped.TrySetResult();

    public async Task<string> WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _stopped.Task.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "STOPPED received";
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return "session time limit";
        }
    }
}

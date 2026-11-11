namespace ClickBridge.Core.Logging;

/// <summary>Minimal log sink shared by the BLE, network and app layers.</summary>
public interface IBridgeLog
{
    /// <summary>High-volume detail; written to the log file only.</summary>
    void Verbose(string tag, string message);

    void Info(string tag, string message);

    /// <summary>Something the user cares about (button press, client connected, ...).</summary>
    void Event(string tag, string message);

    void Warn(string tag, string message);

    void Error(string tag, string message);
}

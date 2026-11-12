using System.Globalization;

namespace ClickBridge.Core.Logging;

/// <summary>Thread-safe log that writes every line to a file and all but verbose lines to the console.</summary>
public sealed class FileConsoleLog : IBridgeLog, IDisposable
{
    private readonly Lock _lock = new();
    private readonly StreamWriter _file;
    private readonly bool _writeToConsole;
    private bool _disposed;

    /// <param name="retainDays">
    /// When set, older log files with the same prefix are deleted from <paramref name="directory"/>.
    /// </param>
    public FileConsoleLog(string directory, string filePrefix, bool writeToConsole = true, int? retainDays = null)
    {
        Directory.CreateDirectory(directory);
        if (retainDays is { } days)
        {
            DeleteOldLogs(directory, filePrefix, DateTime.Now.AddDays(-days));
        }

        (FilePath, _file) = Open(directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}");
        _writeToConsole = writeToConsole;
    }

    // Two launches within the same second must not fight over one file name.
    private static (string Path, StreamWriter Writer) Open(string directory, string name)
    {
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(directory, attempt == 1 ? $"{name}.log" : $"{name}-{attempt}.log");
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return (path, new StreamWriter(stream) { AutoFlush = true });
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                // taken by another instance: try the next suffix
            }
        }
    }

    private static void DeleteOldLogs(string directory, string filePrefix, DateTime olderThan)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, $"{filePrefix}-*.log"))
            {
                if (File.GetLastWriteTime(file) < olderThan)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old logs are only housekeeping; never fail the start because of them.
        }
    }

    public string FilePath { get; }

    public void Verbose(string tag, string message) => Write(tag, message, ConsoleColor.DarkGray, toConsole: false);

    public void Info(string tag, string message) => Write(tag, message, ConsoleColor.Gray, toConsole: true);

    public void Event(string tag, string message) => Write(tag, message, ConsoleColor.Green, toConsole: true);

    public void Warn(string tag, string message) => Write(tag, message, ConsoleColor.Yellow, toConsole: true);

    public void Error(string tag, string message) => Write(tag, message, ConsoleColor.Red, toConsole: true);

    private void Write(string tag, string message, ConsoleColor color, bool toConsole)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} [{tag,-22}] {message}");
        lock (_lock)
        {
            if (_disposed)
            {
                return; // a late BLE / network callback after shutdown
            }

            _file.WriteLine(line);
            if (_writeToConsole && toConsole)
            {
                var previous = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = previous;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _file.Dispose();
        }
    }
}

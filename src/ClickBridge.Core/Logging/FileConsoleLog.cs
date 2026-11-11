using System.Globalization;

namespace ClickBridge.Core.Logging;

/// <summary>Thread-safe log that writes every line to a file and all but verbose lines to the console.</summary>
public sealed class FileConsoleLog : IBridgeLog, IDisposable
{
    private readonly Lock _lock = new();
    private readonly StreamWriter _file;
    private readonly bool _writeToConsole;

    public FileConsoleLog(string directory, string filePrefix, bool writeToConsole = true)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(FilePath, append: false) { AutoFlush = true };
        _writeToConsole = writeToConsole;
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
            _file.Dispose();
        }
    }
}

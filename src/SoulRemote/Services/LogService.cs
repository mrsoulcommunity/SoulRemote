using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace SoulRemote.Services;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;

    public string Display => $"{Timestamp:HH:mm:ss}  [{Level.ToString().ToUpperInvariant()}]  {Message}";
}

public interface ILogService
{
    ReadOnlyObservableCollection<LogEntry> Entries { get; }
    void Log(LogLevel level, string message);
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Error(string message, Exception ex);
    void Clear();
}

/// <summary>
/// In-memory + file logger. The observable collection is always mutated on the
/// UI dispatcher so it can be bound directly by the log view.
/// </summary>
public sealed class LogService : ILogService
{
    private const int MaxEntries = 1000;
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly string _logFilePath;

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    public LogService()
    {
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoulRemote", "logs");
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"soulremote-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Log(LogLevel.Error, $"{message}: {ex.Message}");

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        WriteToFile(entry);
        DispatchToUi(() =>
        {
            _entries.Add(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        });
    }

    public void Clear() => DispatchToUi(_entries.Clear);

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFilePath, entry.Display + Environment.NewLine);
        }
        catch
        {
            // Logging must never throw into the caller.
        }
    }

    private static void DispatchToUi(Action action)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            // BeginInvoke (non-blocking) so background logging never waits on the UI thread,
            // which also avoids deadlocking during shutdown.
            dispatcher.BeginInvoke(action);
        else
            action();
    }
}

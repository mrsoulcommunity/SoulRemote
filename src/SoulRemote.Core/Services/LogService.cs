using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SoulRemote.Abstractions;

namespace SoulRemote.Services;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;

    public string Display =>
        $"{Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  " +
        $"[{Level.ToString().ToUpperInvariant()}]  {Message}";
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

    /// <summary>Folder holding the rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Deletes log files older than <paramref name="days"/>. Returns how many went.</summary>
    int PruneOlderThan(int days);

    /// <summary>Deletes every log file on disk. Returns how many went.</summary>
    int DeleteAllFiles();
}

/// <summary>
/// In-memory + file logger. The observable collection is always mutated through the
/// supplied dispatcher so the log view can bind to it directly.
/// </summary>
public sealed class LogService : ILogService
{
    private const int MaxEntries = 1000;
    private const string FilePrefix = "soulremote-";
    private const string FileSuffix = ".log";

    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly IUiDispatcher _dispatcher;

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }
    public string LogDirectory { get; }

    public LogService(IUiDispatcher? dispatcher = null, string? logDirectory = null)
    {
        _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);

        LogDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoulRemote", "logs");
        try { Directory.CreateDirectory(LogDirectory); }
        catch { /* logging must never stop the app from starting */ }

    }

    /// <summary>
    /// Today's log file, resolved per write rather than pinned at construction.
    /// Soul Remote is meant to run for weeks — start with Windows, live in the tray —
    /// so "the process started on the 3rd" is the normal case, and a path captured
    /// then would file every entry from the 4th onward under the 3rd.
    /// </summary>
    private string CurrentLogFile => Path.Combine(LogDirectory,
        FilePrefix + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileSuffix);

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Log(LogLevel.Error, $"{message}: {ex.Message}");

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        WriteToFile(entry);
        _dispatcher.Post(() =>
        {
            _entries.Add(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        });
    }

    public void Clear() => _dispatcher.Post(_entries.Clear);

    /// <summary>
    /// Deletes every log file, including today's. "Clear the activity log" plainly
    /// means the record goes, and the record is on disk: the log names paired chat
    /// ids, the bot handle and the relay URL, so emptying only the in-memory list
    /// would leave exactly what the user was trying to remove.
    /// </summary>
    public int DeleteAllFiles()
    {
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(LogDirectory, FilePrefix + "*" + FileSuffix))
            {
                try { File.Delete(path); removed++; }
                catch { /* in use or read-only: leave it */ }
            }
        }
        catch { /* the folder may have gone; nothing to delete */ }
        return removed;
    }

    /// <summary>
    /// Log files are named by date, so age comes from the name rather than the
    /// filesystem timestamp — a file that was merely touched should still expire on
    /// schedule. Anything that does not parse is left alone; this deletes files, so
    /// it only ever deletes what it is certain about.
    /// </summary>
    public int PruneOlderThan(int days)
    {
        if (days <= 0)
            return 0;

        var cutoff = DateTime.Today.AddDays(-days);
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(LogDirectory, FilePrefix + "*" + FileSuffix))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length != FilePrefix.Length + 8)
                    continue;
                var stamp = name[FilePrefix.Length..];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;
                if (date >= cutoff)
                    continue;
                if (string.Equals(path, CurrentLogFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(path); removed++; }
                catch { /* in use or read-only: leave it */ }
            }
        }
        catch { /* the folder may have gone; nothing to prune */ }
        return removed;
    }

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            lock (_fileLock)
                File.AppendAllText(CurrentLogFile, entry.Display + Environment.NewLine);
        }
        catch
        {
            // Logging must never throw into the caller.
        }
    }
}

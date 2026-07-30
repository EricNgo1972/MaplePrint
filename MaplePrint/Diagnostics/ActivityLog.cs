using System.Collections.Concurrent;

namespace MaplePrint.Diagnostics;

public enum ActivityKind { Info, Success, Error }

public sealed record ActivityEntry(DateTime At, ActivityKind Kind, string Message)
{
    public override string ToString() => $"{At:HH:mm:ss}  {Kind,-7}  {Message}";
}

/// <summary>
/// A bounded, in-memory record of what the agent has done, plus an append-only
/// file for the days when someone phones to ask why a receipt never came out.
/// Bounded because this process is expected to run for months without a
/// restart — an unbounded list would be a slow memory leak on a counter PC.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 500;

    private readonly ConcurrentQueue<ActivityEntry> _entries = new();
    private readonly string _filePath;
    private readonly object _fileLock = new();

    public ActivityLog(string folder)
    {
        _filePath = Path.Combine(folder, "activity.log");
    }

    /// <summary>Raised on every entry. Marshal to the UI thread before touching controls.</summary>
    public event Action<ActivityEntry>? Appended;

    public IReadOnlyList<ActivityEntry> Snapshot() => _entries.ToArray();

    public void Info(string message) => Append(ActivityKind.Info, message);
    public void Success(string message) => Append(ActivityKind.Success, message);
    public void Error(string message) => Append(ActivityKind.Error, message);

    private void Append(ActivityKind kind, string message)
    {
        var entry = new ActivityEntry(DateTime.Now, kind, message);

        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries) _entries.TryDequeue(out _);

        WriteToFile(entry);
        Appended?.Invoke(entry);
    }

    /// <summary>
    /// Best-effort: losing a log line is never worth failing a print job over,
    /// so every failure here is swallowed deliberately.
    /// </summary>
    private void WriteToFile(ActivityEntry entry)
    {
        try
        {
            lock (_fileLock)
            {
                // Roll at ~1 MB so the file can't grow without bound either.
                var info = new FileInfo(_filePath);
                if (info.Exists && info.Length > 1_000_000)
                {
                    var previous = _filePath + ".1";
                    File.Delete(previous);
                    File.Move(_filePath, previous);
                }

                File.AppendAllText(_filePath, $"{entry.At:yyyy-MM-dd HH:mm:ss}  {entry.Kind,-7}  {entry.Message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignored on purpose — see summary.
        }
    }

    public string FilePath => _filePath;
}

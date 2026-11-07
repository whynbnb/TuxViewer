namespace TuxViewer.Services;

/// <summary>
/// Small in-memory ring buffer used by the diagnostics bundle exporter.
/// It deliberately avoids external logging configuration so bug reports always
/// contain the most recent runtime messages.
/// </summary>
public static class AppLog
{
    private const int Capacity = 2000;
    private static readonly Lock Gate = new();
    private static readonly Queue<string> Entries = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Write(string level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message.ReplaceLineEndings(" ")}";
        lock (Gate)
        {
            Entries.Enqueue(line);
            while (Entries.Count > Capacity) Entries.Dequeue();
        }

        if (string.Equals(Environment.GetEnvironmentVariable("TUXVIEWER_LOG"), "1", StringComparison.Ordinal))
            Console.Error.WriteLine(line);
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Entries.ToArray();
    }

    public static string Dump()
    {
        var lines = Snapshot();
        return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

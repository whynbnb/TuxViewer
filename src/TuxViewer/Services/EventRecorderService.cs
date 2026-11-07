using System.Text;
using System.Text.Json;

namespace TuxViewer.Services;

/// <summary>
/// Appends runtime events to a JSONL file under the XDG state directory.
/// A single recording session can contain realtime events, X11 RECORD events
/// and X11 protocol requests/replies, distinguished by the channel field.
/// </summary>
public sealed class EventRecorderService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public static string DirectoryPath
    {
        get
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "TuxViewer", "events");
        }
    }

    public bool IsRecording { get; private set; }
    public string? CurrentPath { get; private set; }
    public long LinesWritten { get; private set; }

    public string Start()
    {
        lock (_gate)
        {
            if (IsRecording && CurrentPath is not null) return CurrentPath;

            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, $"tuxviewer-events-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            var suffix = 1;
            while (File.Exists(path))
                path = Path.Combine(DirectoryPath, $"tuxviewer-events-{DateTime.Now:yyyyMMdd-HHmmss}-{suffix++}.jsonl");

            _writer = new StreamWriter(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                AutoFlush = false,
            };
            IsRecording = true;
            CurrentPath = path;
            LinesWritten = 0;
            AppLog.Info($"Event recording started: {path}");
            return path;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRecording) return;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch { /* ignore */ }
            _writer = null;
            IsRecording = false;
            AppLog.Info($"Event recording stopped: {CurrentPath} ({LinesWritten} lines)");
        }
    }

    public void Write(string channel, object record)
    {
        if (!IsRecording) return;
        lock (_gate)
        {
            if (_writer is null || !IsRecording) return;
            try
            {
                var entry = new
                {
                    timestamp = DateTimeOffset.Now,
                    channel,
                    record,
                };
                _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                LinesWritten++;
                if (LinesWritten % 100 == 0) _writer.Flush();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Event recording write failed: {ex.Message}");
            }
        }
    }

    public static string SerializeLine(string channel, object record)
        => JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            channel,
            record,
        }, JsonOptions);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

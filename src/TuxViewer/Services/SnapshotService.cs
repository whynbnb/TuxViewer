using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services;

public static class SnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Directory
    {
        get
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "TuxViewer", "snapshots");
        }
    }

    public static IReadOnlyList<WindowSnapshot> LoadAll()
    {
        var result = new List<WindowSnapshot>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return result;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<WindowSnapshot>(File.ReadAllText(file), JsonOptions);
                    if (snapshot is not null) result.Add(snapshot);
                }
                catch { /* skip malformed snapshot */ }
            }
        }
        catch { /* snapshot storage is optional */ }
        return result.OrderByDescending(s => s.CapturedAt).ToList();
    }

    public static async Task SaveAsync(WindowSnapshot snapshot, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = Path.Combine(Directory, $"{snapshot.Id}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
    }

    public static void Delete(WindowSnapshot snapshot)
    {
        try
        {
            var file = Path.Combine(Directory, $"{snapshot.Id}.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { /* ignore */ }
    }

    public static IReadOnlyList<SnapshotDiff> Compare(WindowSnapshot old, WindowSnapshot current)
    {
        var diffs = new List<SnapshotDiff>();
        var keys = old.Values.Keys.Union(current.Values.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var hasOld = old.Values.TryGetValue(key, out var oldValue);
            var hasNew = current.Values.TryGetValue(key, out var newValue);
            if (hasOld && hasNew)
            {
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    diffs.Add(new SnapshotDiff { Key = key, OldValue = oldValue, NewValue = newValue });
            }
            else if (hasNew)
            {
                diffs.Add(new SnapshotDiff { Key = key, NewValue = newValue, State = "added" });
            }
            else
            {
                diffs.Add(new SnapshotDiff { Key = key, OldValue = oldValue, State = "removed" });
            }
        }
        return diffs;
    }
}

using System.Text;

namespace TuxViewer.Services;

/// <summary>
/// Best-effort fallback for X11 clients that do not set _NET_WM_PID (for example xeyes).
/// Matches WM_CLASS / instance against /proc process names.
/// </summary>
public static class ProcessResolver
{
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, List<int>> ByName = new(StringComparer.Ordinal);
    private static DateTime _lastScan = DateTime.MinValue;

    public static int? FindPid(string? instance, string? className)
    {
        lock (CacheLock)
        {
            if ((DateTime.UtcNow - _lastScan).TotalSeconds > 5) Scan();
        }

        foreach (var candidate in new[] { className, instance })
        {
            var key = Normalize(candidate);
            if (key.Length < 2) continue;
            if (ByName.TryGetValue(key, out var exact) && exact.Count > 0)
                return exact[0];

            foreach (var (name, pids) in ByName)
            {
                if (name.Length < 3 || pids.Count == 0) continue;
                if (name.EndsWith(key, StringComparison.Ordinal) || key.EndsWith(name, StringComparison.Ordinal))
                    return pids[0];
            }
        }
        return null;
    }

    private static void Scan()
    {
        ByName.Clear();
        _lastScan = DateTime.UtcNow;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out var pid) || pid == Environment.ProcessId) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    Add(comm, pid);
                    var exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                    if (!string.IsNullOrWhiteSpace(exe)) Add(Path.GetFileName(exe), pid);
                }
                catch { /* process disappeared or is not ours */ }
            }
        }
        catch { /* ignore */ }
    }

    private static void Add(string? processName, int pid)
    {
        var normalized = Normalize(processName);
        if (normalized.Length < 2) return;
        if (!ByName.TryGetValue(normalized, out var list))
            ByName[normalized] = list = [];
        if (!list.Contains(pid)) list.Add(pid);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}

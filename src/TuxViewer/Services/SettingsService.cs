using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>
/// Loads and debounces writes of <see cref="AppSettings"/> to the XDG config directory.
/// Settings are intentionally independent from session state.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private AppSettings? _pending;
    private bool _disposed;

    public string FilePath { get; }
    public AppSettings Settings { get; private set; } = new();
    public string? LastError { get; private set; }

    public SettingsService()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        FilePath = Path.Combine(configHome, "TuxViewer", "settings.json");
        _timer = new Timer(_ => WritePending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Settings = new AppSettings();
                return;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Settings = Normalize(loaded ?? new AppSettings());
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Settings = new AppSettings();
        }
    }

    /// <summary>Mutate the live settings object and schedule a debounced write.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        if (_disposed) return;
        try
        {
            lock (_lock)
            {
                mutate(Settings);
                _pending = Settings.Clone();
                _timer.Change(600, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Write any pending snapshot immediately.</summary>
    public void Flush()
    {
        AppSettings? snapshot;
        lock (_lock)
        {
            snapshot = _pending;
            _pending = null;
            if (snapshot is null) return;
        }
        Write(snapshot);
    }

    private void WritePending()
    {
        AppSettings? snapshot;
        lock (_lock)
        {
            snapshot = _pending;
            _pending = null;
        }
        if (snapshot is not null) Write(snapshot);
    }

    private void Write(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Version = 1;
        if (settings.WindowWidth is < 800 or > 10000) settings.WindowWidth = 1440;
        if (settings.WindowHeight is < 600 or > 10000) settings.WindowHeight = 900;
        if (settings.WindowX is < -30000 or > 30000) settings.WindowX = null;
        if (settings.WindowY is < -30000 or > 30000) settings.WindowY = null;
        settings.BackendFilterIndex = Math.Clamp(settings.BackendFilterIndex, 0, 4);
        settings.TreeGroupModeIndex = Math.Clamp(settings.TreeGroupModeIndex, 0, 3);
        settings.RuleCompositorIndex = Math.Clamp(settings.RuleCompositorIndex, 0, 2);
        settings.RuleWidth = settings.RuleWidth?.Trim() ?? "";
        settings.RuleHeight = settings.RuleHeight?.Trim() ?? "";
        settings.RuleWorkspace = settings.RuleWorkspace?.Trim() ?? "";
        settings.RuleOpacity = settings.RuleOpacity?.Trim() ?? "";
        return settings;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _timer.Dispose();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using TuxViewer.Services;

namespace TuxViewer.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settings;
    private int _languageIndex;
    private int _themeIndex;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        RebuildOptions();
        _languageIndex = settings.Settings.Language switch
        {
            "zh" => 1,
            "en" => 2,
            _ => 0,
        };
        _themeIndex = settings.Settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<string> LanguageOptions { get; } = [];
    public ObservableCollection<string> ThemeOptions { get; } = [];
    public string SettingsPath => _settings.FilePath;
    public string EventsPath => EventRecorderService.DirectoryPath;
    public string DiagnosticsPath => DiagnosticsBundleService.DirectoryPath;
    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (!Set(ref _languageIndex, Math.Clamp(value, 0, 2))) return;
            var code = value switch { 1 => "zh", 2 => "en", _ => "auto" };
            _settings.Update(settings => settings.Language = code);
            LocalizationService.SetLanguage(code);
        }
    }

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!Set(ref _themeIndex, Math.Clamp(value, 0, 2))) return;
            var code = value switch { 1 => "light", 2 => "dark", _ => "system" };
            _settings.Update(settings => settings.Theme = code);
            ThemeService.SetTheme(code);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RebuildOptions);

    public void Detach() => LocalizationService.LanguageChanged -= OnLanguageChanged;

    private void RebuildOptions()
    {
        var language = Math.Clamp(_languageIndex, 0, 2);
        var theme = Math.Clamp(_themeIndex, 0, 2);

        LanguageOptions.Clear();
        LanguageOptions.Add(LocalizationService.L("跟随系统", "Follow system"));
        LanguageOptions.Add("中文");
        LanguageOptions.Add("English");

        ThemeOptions.Clear();
        ThemeOptions.Add(LocalizationService.L("跟随系统", "Follow system"));
        ThemeOptions.Add(LocalizationService.L("浅色", "Light"));
        ThemeOptions.Add(LocalizationService.L("深色", "Dark"));

        _languageIndex = language;
        _themeIndex = theme;
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(ThemeIndex));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

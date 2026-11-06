using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TuxViewer.Services;
using TuxViewer.ViewModels;
using TuxViewer.Views;

namespace TuxViewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            settingsService.Load();
            var service = new InspectorService();
            var viewModel = new MainWindowViewModel(service, settingsService);
            viewModel.InitialSelection = ReadSelectArgument(desktop.Args);
            viewModel.SelectedTabIndex = ReadTabArgument(desktop.Args);
            viewModel.AutoCapture = desktop.Args?.Contains("--capture", StringComparer.OrdinalIgnoreCase) == true;
            if (desktop.Args?.Contains("--tree", StringComparer.OrdinalIgnoreCase) == true)
                viewModel.TreeMode = true;
            viewModel.AutoRecord = desktop.Args?.Contains("--record", StringComparer.OrdinalIgnoreCase) == true;
            if (desktop.Args?.Contains("--record-selected", StringComparer.OrdinalIgnoreCase) == true)
                viewModel.X11RecordOnlySelected = true;
            viewModel.AutoLoadAtspi = desktop.Args?.Contains("--atspi-load", StringComparer.OrdinalIgnoreCase) == true;
            viewModel.AutoHotkey = desktop.Args?.Contains("--hotkey", StringComparer.OrdinalIgnoreCase) == true;
            if (TryReadIntArgument(desktop.Args, "--group") is int group)
                viewModel.TreeGroupModeIndex = Math.Clamp(group, 0, 3);

            var window = new MainWindow { DataContext = viewModel };
            ApplyWindowSize(window, settingsService.Settings);
            window.Opened += (_, _) => ApplyWindowPosition(window, settingsService.Settings);
            window.Closed += (_, _) =>
            {
                if (window.WindowState == WindowState.Normal)
                {
                    settingsService.Update(settings =>
                    {
                        settings.WindowWidth = window.Width;
                        settings.WindowHeight = window.Height;
                        settings.WindowX = window.Position.X;
                        settings.WindowY = window.Position.Y;
                    });
                }
                viewModel.PersistNow();
                settingsService.Flush();
                settingsService.Dispose();
                service.Dispose();
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyWindowSize(Window window, Models.AppSettings settings)
    {
        var width = Math.Clamp(settings.WindowWidth, 800, 10000);
        var height = Math.Clamp(settings.WindowHeight, 600, 10000);
        window.Width = Math.Max(width, window.MinWidth);
        window.Height = Math.Max(height, window.MinHeight);
    }

    private static void ApplyWindowPosition(Window window, Models.AppSettings settings)
    {
        if (settings.WindowX is not int x || settings.WindowY is not int y) return;
        var screen = window.Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - 200));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - 100));
        window.Position = new PixelPoint(x, y);
    }

    private static string? ReadSelectArgument(string[]? args)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--select", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int? TryReadIntArgument(string[]? args, string name)
    {
        if (args is null) return null;
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var value))
                return value;
        return null;
    }

    private static int ReadTabArgument(string[]? args)
    {
        if (args is null) return 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--tab", StringComparison.OrdinalIgnoreCase)) continue;
            return args[i + 1].ToLowerInvariant() switch
            {
                "process" or "proc" => 1,
                "x11" or "properties" => 2,
                "wayland" or "wl" => 3,
                "env" or "environment" => 4,
                "raw" or "json" => 5,
                "events" or "event" => 6,
                "screenshot" or "shot" => 7,
                "rules" or "rule" => 8,
                "x11events" or "xrecord" or "record" => 9,
                "atspi" or "controls" or "widgets" => 10,
                "hotkey" or "shortcut" => 11,
                "control" or "actions" => 12,
                "report" or "snapshot" => 13,
                "outputs" or "output" or "monitors" => 14,
                "diagnostics" or "diag" => 15,
                _ => 0,
            };
        }
        return 0;
    }
}

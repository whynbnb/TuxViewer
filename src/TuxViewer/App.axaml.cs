using Avalonia;
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
            var service = new InspectorService();
            var viewModel = new MainWindowViewModel(service);
            viewModel.InitialSelection = ReadSelectArgument(desktop.Args);
            viewModel.SelectedTabIndex = ReadTabArgument(desktop.Args);
            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) => service.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
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
                "diagnostics" or "diag" => 7,
                _ => 0,
            };
        }
        return 0;
    }
}

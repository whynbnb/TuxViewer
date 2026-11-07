using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using TuxViewer.Models;
using TuxViewer.Services;
using TuxViewer.ViewModels;
using TuxViewer.Views;

namespace TuxViewer;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new SettingsService();
            settingsService.Load();
            LocalizationService.Initialize(settingsService.Settings.Language);
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            AppLog.Info($"TuxViewer {version} starting · language={LocalizationService.LanguageCode} · settings={settingsService.FilePath}");
            if (!string.IsNullOrEmpty(settingsService.LastError))
                AppLog.Warn($"Settings load: {settingsService.LastError}");
            var service = new InspectorService();
            var viewModel = new MainWindowViewModel(service, settingsService);
            var startup = StartupOptions.Parse(desktop.Args ?? []);
            viewModel.InitialSelection = startup.Select;
            viewModel.SelectedTabIndex = startup.TabIndex ?? 0;
            viewModel.AutoCapture = startup.Capture;
            if (startup.Tree) viewModel.TreeMode = true;
            if (startup.GroupModeIndex is int group)
                viewModel.TreeGroupModeIndex = Math.Clamp(group, 0, 3);
            viewModel.AutoRecord = startup.Record;
            if (startup.RecordSelected) viewModel.X11RecordOnlySelected = true;
            viewModel.AutoLoadAtspi = startup.AtspiLoad;
            viewModel.AutoHotkey = startup.Hotkey;
            viewModel.AutoPickMode = startup.PickMode;
            viewModel.AutoPickX11 = startup.PickX11;
            viewModel.AutoPickX11Mode = startup.PickX11Mode;

            var window = new MainWindow { DataContext = viewModel };
            _mainWindow = window;
            _viewModel = viewModel;
            _desktop = desktop;
            ApplyWindowSize(window, settingsService.Settings);
            window.Opened += (_, _) =>
            {
                ApplyWindowPosition(window, settingsService.Settings);
                InitializeTray(window, viewModel, desktop);
            };
            window.Closed += (_, _) =>
            {
                DisposeTray();
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
                AppLog.Info("TuxViewer exiting");
                settingsService.Flush();
                settingsService.Dispose();
                service.Dispose();
            };
            desktop.MainWindow = window;

            SingleInstanceService.Active?.SetHandler(HandleInstanceRequestAsync);
            LocalizationService.LanguageChanged += OnLanguageChanged;
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

    private Task HandleInstanceRequestAsync(SingleInstanceRequest request)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ApplyInstanceRequestAsync(request));
        return Task.CompletedTask;
    }

    private async Task ApplyInstanceRequestAsync(SingleInstanceRequest request)
    {
        if (_mainWindow is null || _viewModel is null) return;
        try
        {
            if (!_mainWindow.IsVisible) _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();

            var options = StartupOptions.Parse(request.Args);
            await _viewModel.ApplyStartupOptionsAsync(options);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Forwarded GUI request failed: {ex.Message}");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_mainWindow is not null && _viewModel is not null && _desktop is not null)
            InitializeTray(_mainWindow, _viewModel, _desktop);
    }

    private void InitializeTray(
        MainWindow window,
        MainWindowViewModel viewModel,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TUXVIEWER_NO_TRAY"), "1", StringComparison.Ordinal))
            return;

        DisposeTray();
        try
        {
            var menu = new NativeMenu();

            var show = new NativeMenuItem(LocalizationService.L("显示 TuxViewer", "Show TuxViewer"));
            show.Click += (_, _) => ShowWindow(window);
            menu.Add(show);

            var pick = new NativeMenuItem(LocalizationService.L("X11 点击拾取", "Pick X11 (click)"));
            pick.Click += (_, _) =>
            {
                ShowWindow(window);
                _ = viewModel.PickAsync(x11Only: true, mode: PickMode.Click);
            };
            menu.Add(pick);

            var focused = new NativeMenuItem(LocalizationService.L("拾取当前焦点", "Pick current focus"));
            focused.Click += (_, _) =>
            {
                ShowWindow(window);
                _ = viewModel.PickFocusedAsync();
            };
            menu.Add(focused);

            menu.Add(new NativeMenuItemSeparator());

            var refresh = new NativeMenuItem(LocalizationService.L("刷新窗口", "Refresh windows"));
            refresh.Click += (_, _) => _ = viewModel.RefreshAsync();
            menu.Add(refresh);

            menu.Add(new NativeMenuItemSeparator());

            var quit = new NativeMenuItem(LocalizationService.L("退出", "Quit"));
            quit.Click += (_, _) => desktop.Shutdown();
            menu.Add(quit);

            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TuxViewer/Assets/tuxviewer.png")));
            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "TuxViewer",
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => ToggleWindow(window);
            AppLog.Info("Tray icon initialized.");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Tray icon unavailable: {ex.Message}");
        }
    }

    private void DisposeTray()
    {
        if (_trayIcon is null) return;
        try { _trayIcon.IsVisible = false; } catch { /* ignore */ }
        try { _trayIcon.Dispose(); } catch { /* ignore */ }
        _trayIcon = null;
    }

    private static void ShowWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void ToggleWindow(Window window)
    {
        if (window.IsVisible) window.Hide();
        else ShowWindow(window);
    }
}

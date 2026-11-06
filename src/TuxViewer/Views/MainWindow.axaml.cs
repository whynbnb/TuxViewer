using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using TuxViewer.ViewModels;

namespace TuxViewer.Views;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Shutdown();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized || ViewModel is null) return;
        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshAsync();
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickAsync();
        Activate();
    }

    private async void PickX11_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickAsync(x11Only: true);
        Activate();
    }

    private async void Focused_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickFocusedAsync();
        Activate();
    }

    private void CancelPick_Click(object? sender, RoutedEventArgs e) => ViewModel?.CancelPick();

    private async void CopySummary_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.Summary)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.Summary);
    }

    private async void CopyJson_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.RawJson)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.RawJson);
    }

    private async void Highlight_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.HighlightSelectedAsync();
    }

    private async void Focus_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.FocusSelectedAsync();
        Activate();
    }

    private async void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.CloseSelectedAsync();
    }

    private async void InstallGnome_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.InstallGnomeExtensionAsync();
    }

    private void RefreshDiagnostics_Click(object? sender, RoutedEventArgs e) => ViewModel?.RefreshDiagnostics();

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null) return;
        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                await ViewModel.PickAsync(x11Only: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                Activate();
                break;
            case Key.F5:
                e.Handled = true;
                await ViewModel.RefreshAsync();
                break;
            case Key.Escape:
                if (ViewModel.IsPicking)
                {
                    e.Handled = true;
                    ViewModel.CancelPick();
                }
                break;
        }
    }
}

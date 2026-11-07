using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TuxViewer.Models;
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

    private void SaveFilterPreset_Click(object? sender, RoutedEventArgs e) => ViewModel?.SaveFilterPreset();

    private void ApplyFilterPreset_Click(object? sender, RoutedEventArgs e) => ViewModel?.ApplySelectedFilterPreset();

    private void DeleteFilterPreset_Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedFilterPreset();

    private void ResetFilters_Click(object? sender, RoutedEventArgs e) => ViewModel?.ResetFilters();

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.RefreshAsync();
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickAsync(mode: PickMode.Click);
        Activate();
    }

    private async void PickDrag_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickAsync(mode: PickMode.Drag);
        Activate();
    }

    private async void PickX11_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PickAsync(x11Only: true, mode: PickMode.Click);
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

    private async void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.CaptureScreenshotAsync();
    }

    private async void SaveScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.ScreenshotPng is not { Length: > 0 } png) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var safeName = string.Concat((ViewModel.SelectedTitle ?? "window")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName.Length > 60) safeName = safeName[..60];

        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "保存窗口截图",
            SuggestedFileName = $"{safeName}.png",
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("PNG 图片")
                {
                    Patterns = ["*.png"],
                    MimeTypes = ["image/png"],
                },
            ],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
        }
        catch { /* user cancelled or disk error */ }
    }

    private async void CopyScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.ScreenshotImage is not { } bitmap) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try { await clipboard.SetBitmapAsync(bitmap); }
        catch { /* clipboard backend may not support images */ }
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

    private void ClearEvents_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearEvents();

    private async void CopyEvents_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.BuildEventsText());
    }

    private async void CopyRule_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.GeneratedRule)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.GeneratedRule);
    }

    private async void ToggleX11Record_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.ToggleX11RecordAsync();
    }

    private void ClearX11Events_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearX11Events();

    private async void CopyX11Events_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.BuildX11EventsText());
    }

    private async void LoadAtspi_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.LoadAtspiAsync();
    }

    private async void CopyAtspi_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasAtspiTree) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.BuildAtspiText());
    }

    private void ToggleHotkey_Click(object? sender, RoutedEventArgs e) => ViewModel?.ToggleGlobalHotkey();

    private async void CopyHotkey_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.BuildHotkeyText());
    }

    private async void ExportHtml_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var file = await PickSaveAsync("导出 HTML 报告", SafeName(ViewModel.SelectedTitle) + ".html", "html", "text/html");
        if (file is null) return;
        var html = await ViewModel.BuildReportHtmlAsync();
        if (string.IsNullOrWhiteSpace(html)) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, html);
        ViewModel.SetReportStatus($"已导出 HTML：{file.Name}");
    }

    private async void ExportMarkdown_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var file = await PickSaveAsync("导出 Markdown 报告", SafeName(ViewModel.SelectedTitle) + ".md", "md", "text/markdown");
        if (file is null) return;
        var baseName = Path.GetFileNameWithoutExtension(file.Name);
        var imageName = baseName + ".png";
        var markdown = await ViewModel.BuildReportMarkdownAsync(imageName);
        if (string.IsNullOrWhiteSpace(markdown)) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, markdown);

        if (ViewModel.ScreenshotPng is { Length: > 0 } png)
        {
            var directory = Path.GetDirectoryName(file.Path.LocalPath);
            if (!string.IsNullOrWhiteSpace(directory))
                await File.WriteAllBytesAsync(Path.Combine(directory, imageName), png);
        }
        ViewModel.SetReportStatus($"已导出 Markdown：{file.Name}");
    }

    private async void ExportJson_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.RawJson)) return;
        var file = await PickSaveAsync("导出检视 JSON", SafeName(ViewModel.SelectedTitle) + ".json", "json", "application/json");
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path.LocalPath, ViewModel.RawJson);
        ViewModel.SetReportStatus($"已导出 JSON：{file.Name}");
    }

    private async void SaveSnapshot_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.SaveSnapshotAsync();
    }

    private void CompareSnapshot_Click(object? sender, RoutedEventArgs e) => ViewModel?.CompareSelectedSnapshot();

    private void DeleteSnapshot_Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedSnapshot();

    private void RefreshSnapshots_Click(object? sender, RoutedEventArgs e) => ViewModel?.LoadSnapshots();

    private async void RefreshOutputs_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.LoadOutputsAsync();
    }

    private async Task<IStorageFile?> PickSaveAsync(string title, string suggested, string extension, string mime)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return null;
        return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.ToUpperInvariant())
                {
                    Patterns = [$"*.{extension}"],
                    MimeTypes = [mime],
                },
            ],
        });
    }

    private static string SafeName(string? title)
    {
        var name = string.Concat((title ?? "window")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return name.Length > 60 ? name[..60] : name;
    }

    private async void ControlAction_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { Tag: string tag }) return;
        switch (tag)
        {
            case "above":
                await ViewModel.SetAboveSelectedAsync();
                break;
            case "below":
                await ViewModel.SetAboveSelectedAsync(below: true);
                break;
            case "clearabove":
                await ViewModel.SetAboveSelectedAsync(clear: true);
                break;
            case "fullon":
                await ViewModel.SetFullscreenSelectedAsync(true);
                break;
            case "fulloff":
                await ViewModel.SetFullscreenSelectedAsync(false);
                break;
            case "floating":
                await ViewModel.ToggleFloatingSelectedAsync();
                break;
            case "stickyon":
                await ViewModel.SetStickySelectedAsync(true);
                break;
            case "stickoff":
                await ViewModel.SetStickySelectedAsync(false);
                break;
            case "opacity":
                await ViewModel.ApplyOpacitySelectedAsync();
                break;
            case "clearopacity":
                await ViewModel.ClearOpacitySelectedAsync();
                break;
            case "geometry":
                await ViewModel.ApplyGeometrySelectedAsync();
                break;
            case "close":
                await ViewModel.CloseSelectedAsync();
                break;
            case "kill":
                await ViewModel.ForceKillSelectedAsync();
                break;
            case "openfolder":
                await ViewModel.OpenExeFolderAsync();
                break;
            case "copyproc":
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(ViewModel.BuildProcessText());
                break;
            }
        }
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null) return;
        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                await ViewModel.PickAsync(
                    x11Only: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                    mode: PickMode.Click);
                Activate();
                break;
            case Key.F3:
                e.Handled = true;
                await ViewModel.PickAsync(
                    x11Only: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                    mode: PickMode.Drag);
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

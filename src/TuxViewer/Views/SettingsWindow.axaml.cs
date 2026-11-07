using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TuxViewer.Services;
using TuxViewer.ViewModels;

namespace TuxViewer.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(new SettingsService())
    {
    }

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        var viewModel = new SettingsViewModel(settings);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Detach();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

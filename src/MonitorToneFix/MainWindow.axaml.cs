using Avalonia.Controls;
using MonitorToneFix.ViewModels;

namespace MonitorToneFix;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        // Cancel the first close attempt: killing picom and restoring xfwm4 compositing
        // is async, and blocking the UI thread on it here would deadlock (the awaited
        // continuations need to resume on this same thread). Do it properly, then close.
        e.Cancel = true;
        await _viewModel.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }
}

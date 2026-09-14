using Avalonia.Controls;
using MonitorToneFix.ViewModels;

namespace MonitorToneFix;

public partial class MainWindow : Window
{
    private bool _reallyQuit;
    private bool _shutdownComplete;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainWindowViewModel();
        DataContext = ViewModel;
        Closing += OnClosing;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
        {
            // "Ao clicar em minimizar, o programa vai para a bandeja" — hide instead of
            // leaving a minimized taskbar entry.
            Hide();
        }
    }

    public void ShowFromTray()
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>Invoked only from the tray's "Sair" — performs the real shutdown and lets the window close.</summary>
    public async Task QuitForRealAsync()
    {
        _reallyQuit = true;
        if (!_shutdownComplete)
        {
            await ViewModel.ShutdownAsync();
            _shutdownComplete = true;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        if (!_reallyQuit)
        {
            // "Ao clicar em fechar, o programa vai para a bandeja" — clicking X hides
            // instead of exiting; only the tray's "Sair" performs a real shutdown.
            e.Cancel = true;
            Hide();
            return;
        }

        // Cancel the first close attempt: killing picom and restoring xfwm4 compositing
        // is async, and blocking the UI thread on it here would deadlock (the awaited
        // continuations need to resume on this same thread). Do it properly, then close.
        e.Cancel = true;
        await ViewModel.ShutdownAsync();
        _shutdownComplete = true;
        Close();
    }
}

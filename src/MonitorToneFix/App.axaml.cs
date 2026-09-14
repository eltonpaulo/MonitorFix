using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MonitorToneFix;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            SetUpTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var iconBitmap = new Bitmap(AssetLoader.Open(new Uri("avares://MonitorToneFix/Assets/icon.png")));

        var showItem = new NativeMenuItem("Abrir MonitorToneFix");
        showItem.Click += (_, _) => _mainWindow?.ShowFromTray();

        var activateItem = new NativeMenuItem("Ativar filtro");
        activateItem.Click += (_, _) => _mainWindow?.ViewModel.ActivateFilterCommand.Execute(null);

        var deactivateItem = new NativeMenuItem("Desativar filtro");
        deactivateItem.Click += (_, _) => _mainWindow?.ViewModel.DeactivateFilterCommand.Execute(null);

        var exitItem = new NativeMenuItem("Sair");
        exitItem.Click += async (_, _) =>
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.QuitForRealAsync();
            }
            desktop.Shutdown();
        };

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconBitmap),
            ToolTipText = "MonitorToneFix",
            Menu = new NativeMenu { showItem, new NativeMenuItemSeparator(), activateItem, deactivateItem, new NativeMenuItemSeparator(), exitItem },
        };
        _trayIcon.Clicked += (_, _) => _mainWindow?.ShowFromTray();

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }
}

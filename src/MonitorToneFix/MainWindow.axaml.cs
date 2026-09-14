using Avalonia.Controls;
using MonitorToneFix.ViewModels;

namespace MonitorToneFix;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}

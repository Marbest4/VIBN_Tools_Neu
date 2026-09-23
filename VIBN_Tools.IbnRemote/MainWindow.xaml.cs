using System.Windows;

namespace VIBN_Tools.IbnRemote;

public partial class MainWindow : Window
{
    private readonly IbnRemoteMainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new IbnRemoteMainViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();
}

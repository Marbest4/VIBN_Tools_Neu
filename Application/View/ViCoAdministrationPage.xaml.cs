using System.Windows.Controls;
using System.Windows;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View;

public partial class ViCoAdministrationPage : UserControl
{
    private readonly ViCoAdministrationPageVM _viewModel;

    public ViCoAdministrationPage()
    {
        InitializeComponent();
        _viewModel = ViCoFeatureBootstrapper.CreateAdministrationViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private void OpenToolDirectory_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenToolDirectory((sender as FrameworkElement)?.DataContext as ManagedToolDirectoryVM);
    }

    private void DeleteToolDirectory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ManagedToolDirectoryVM directory)
            return;

        var decision = MessageBox.Show(
            $"Der Ordner '{directory.Name}' wird vollständig und dauerhaft gelöscht:\n\n{directory.Path}\n\nFortfahren?",
            "Tool-Ordner löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision == MessageBoxResult.Yes)
            _viewModel.DeleteToolDirectory(directory);
    }
}

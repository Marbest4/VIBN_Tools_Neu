using System.Windows.Controls;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View;

/// <summary>Hosts the Special Device view model and disposes its TIA bridge at application exit.</summary>
public partial class SpecialDevicePage : UserControl
{
    private readonly SpecialDevicePageVM _viewModel;

    public SpecialDevicePage()
    {
        InitializeComponent();
        _viewModel = ViCoFeatureBootstrapper.CreateSpecialDeviceViewModel();
        DataContext = _viewModel;
    }
}

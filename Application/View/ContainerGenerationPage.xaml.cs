using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.Application.VM;

namespace VIBN_Tools.Application.View
{
    /// <summary>
    /// Interaction logic for ContainerGenerationPage.xaml
    /// </summary>
    public partial class ContainerGenerationPage : UserControl
    {
        public ContainerGenerationPage()
        {
            InitializeComponent();

            Loaded += View_Loaded;
            Unloaded += (_, _) => (DataContext as ContainerGenerationPageVM)?.OnViewUnloaded();
        }

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ContainerGenerationPageVM vm)
            {
                vm.OnViewLoaded();
            }
        }

        private void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox || comboBox.Template is null) return;
            ToggleButton toggleButton = comboBox.Template.FindName("toggleButton", comboBox) as ToggleButton;
            if (toggleButton != null)
            {
                toggleButton.BorderThickness = new Thickness(0, 0, 0, 0);
                Border border = toggleButton.Template?.FindName("templateRoot", toggleButton) as Border;
                if (border != null)
                {
                    border.Background = comboBox.Background;
                }

            }
        }

        private void AuxiliaryGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs args)
        {
            if (args.PropertyName is nameof(ContainerEntry.IsChangeAcknowledged) or nameof(ContainerEntry.HasUnconfirmedChange))
            { args.Cancel = true; return; }
            if (args.PropertyName != nameof(ContainerEntry.ReviewMessage)) return;
            BindingOperations.SetBinding(args.Column, DataGridColumn.VisibilityProperty,
                new Binding("DataContext.IsReimportDetailsVisible")
                { Source = this, Converter = new BooleanToVisibilityConverter() });
        }

        private void OpenReimportComparisonWindow_Click(object sender, RoutedEventArgs e)
        {
            var window = new ReimportComparisonWindow
            {
                DataContext = DataContext,
                Owner = Window.GetWindow(this)
            };
            window.Show();
        }

        private void AuxiliaryDataExpander_Expanded(object sender, RoutedEventArgs e) =>
            AuxiliaryDataRow.Height = new GridLength(1, GridUnitType.Star);

        private void AuxiliaryDataExpander_Collapsed(object sender, RoutedEventArgs e) =>
            AuxiliaryDataRow.Height = GridLength.Auto;


    }
}

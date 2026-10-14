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
            foreach (var grid in VIBN_Tools.GlobalClasses.VisualTreeHelpers.FindVisualChildren<DataGrid>(this))
                foreach (var column in grid.Columns) SetColumnToolTip(column, column.Header?.ToString() ?? "");
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
            SetColumnToolTip(args.Column, args.PropertyName);
            if (args.PropertyName != nameof(ContainerEntry.ReviewMessage)) return;
            BindingOperations.SetBinding(args.Column, DataGridColumn.VisibilityProperty,
                new Binding("DataContext.IsReimportDetailsVisible")
                { Source = this, Converter = new BooleanToVisibilityConverter() });
        }

        private static void SetColumnToolTip(DataGridColumn column, string name)
        {
            var text = name switch
            {
                "Component" => "Name des Containers bzw. der zugeordneten Komponente.",
                "Type" => "Containertyp aus der geladenen Req-XML; bestimmt verfügbare Slots und Signalgrenzen.",
                "ID" => "Kennung/Kommentar des Signals aus der Quelldatei.",
                "Address" => "Aktuelle Signaladresse oder symbolischer Pfad.",
                "DataType" => "Datentyp des Signals, beispielsweise Bool, Int oder Real.",
                "Signal" => "Signalname aus der eingelesenen Quelle.",
                "Slot" => "Zielslot des Signals im Container.",
                "ValidationError" or "Prüfhinweise" or "Prüfstatus" => "Technische Prüffehler. Leer, wenn keine Fehler erkannt wurden; wird bei Prüfung/Export aktualisiert.",
                "AssignmentWarning" => "Fachlicher Hinweis zur Adress-/Slot-Zuordnung, etwa Ausgangsadresse an einem PLC_IN-Slot. Leer ohne Hinweis.",
                "ReviewStateText" => "Status einer Änderung durch Reimport oder manuelle Bearbeitung. Leer bei unverändertem Stand.",
                "ReviewMessage" or "Reimport-Änderung" => "Details zu Reimport-Änderungen oder Zuordnungshinweisen; optional einblendbar.",
                "RequirementsLocation" or "Req-XML-Fundstelle" => "Komponente, Typ, Slot/Key und XML-Zeile der auslösenden Req-Regel. Bei No matching components bleibt die Spalte leer.",
                "Note" => "Begründung, warum ein Signal ausgeschlossen, mehrdeutig oder nicht zugeordnet ist.",
                "Manually Checked / geprüft" => "Manuelle fachliche Bestätigung der Containerzuordnung.",
                _ => $"{name}: Angaben zum aktuellen Signal-/Containerarbeitsstand."
            };
            var style = new Style(typeof(DataGridColumnHeader), column.HeaderStyle);
            style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, text));
            column.HeaderStyle = style;
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

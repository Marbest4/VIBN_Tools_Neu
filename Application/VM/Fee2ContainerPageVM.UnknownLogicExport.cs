using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.SharedWpf.Commands;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    public ICommand ExportUnknownLogicsCommand { get; private set; } = null!;
    public bool CanExportUnknownLogics => !IsBusy && Roots.Any(root => root.IsSelected && root.Editor.Containers.Any(container =>
        container.IsIncluded && string.Equals(container.Type, "N/A", StringComparison.OrdinalIgnoreCase) &&
        (container.ParentContainerId.Length == 0 || root.Editor.Containers.Any(parent => parent.Id == container.ParentContainerId && parent.IsIncluded))));
    public string ExportUnknownLogicsHelp => IsBusy ? "Ein Vorgang läuft. Nach dessen Abschluss können N/A-Logiken exportiert werden." :
        !CanExportUnknownLogics ? "Mindestens einen FEE-Root und einen N/A-Container für den Export markieren." :
        "Exportiert alle markierten N/A-Container als JSON: aktuelle Logikdefinition, alle Slots, verbundene Signale mit Adresse, Datentyp und Interface sowie Objektverknüpfungen. FEE wird nicht verändert.";

    private void InitializeUnknownLogicExport() => ExportUnknownLogicsCommand = new AsyncRelayCommand(ExportUnknownLogicsAsync, () => CanExportUnknownLogics);

    private async Task ExportUnknownLogicsAsync()
    {
        if (!CanExportUnknownLogics) return;
        var roots = Roots.Where(root => root.IsSelected).Select(root => root.CreateEditedRoot()).ToArray();
        var dialog = new SaveFileDialog { Title = "N/A-Logiken und aktuelle Signalverbindungen exportieren",
            Filter = "Logik- und Signalübersicht (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
            FileName = "FEE2Container_NA_Logiken.json", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        BeginOperation(); var operation = _operationCancellation!;
        try
        {
            var document = FeeUnknownLogicExporter.Create(roots);
            await FeeUnknownLogicExporter.SaveAsync(document, dialog.FileName, operation.Token);
            StatusText = $"N/A-Logiken exportiert: {document.Containers.Count} Container. Datei: {dialog.FileName}";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { StatusText = "N/A-Logikexport abgebrochen."; }
        catch (Exception exception) { ReportOperationFailure("N/A-Logiken konnten nicht exportiert werden", exception); }
        finally { EndOperation(); }
    }
}

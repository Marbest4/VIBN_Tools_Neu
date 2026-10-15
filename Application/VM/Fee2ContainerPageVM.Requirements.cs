using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Core.Containers;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    private bool _useRequirements = true;
    private string _requirementsSourcePath = "";
    private IReadOnlyList<string> _supportedContainerTypes = FeeContainerLiveReconstructor.SupportedContainerTypes;
    public ICommand OpenRequirementsCommand { get; }
    public bool CanConfigureRequirements => !IsBusy;
    public bool UseRequirements
    {
        get => _useRequirements;
        set
        {
            if (!value || _useRequirements == value || IsBusy) return;
            _useRequirements = value;
            OnPropertyChanged();
            RefreshRequirementDefinitions();
            StatusText = "Typdefinitionen geändert. Container aus Signalen erneut erstellen, um die neue Req-Auswahl anzuwenden.";
        }
    }
    public string RequirementsDescription => _service.Requirements is null
        ? "Req.xml erforderlich: Datei laden oder aus ContainerGeneration übernehmen."
        : $"Req-XML: {(string.IsNullOrWhiteSpace(_requirementsSourcePath) ? "aus ContainerGeneration übernommen" : _requirementsSourcePath)} · {_service.Requirements.Types.Count} Typ(en); weitere bekannte FEE-Typen bleiben erkennbar.";

    private void RefreshRequirementDefinitions()
    {
        _service.Requirements = null;
        if (UseRequirements && RequirementsProvider.RequirementsFile is { IsInitialized: true } source)
        {
            _requirementsSourcePath = RequirementsProvider.SourcePath;
            var snapshot = new XDocument(source.Document);
            var matcher = new ContainerGenerator();
            _service.Requirements = new ContainerRequirementsCatalog(snapshot, (signal, candidates) => matcher.MatchRequirementTypes(snapshot, signal, candidates));
        }
        _supportedContainerTypes = (_service.Requirements?.Types ?? []).Concat(FeeContainerLiveReconstructor.SupportedContainerTypes)
            .Concat(Roots.SelectMany(root => root.Editor.Containers).Select(container => container.Type))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToArray();
        OnPropertyChanged(nameof(SupportedContainerTypes));
        OnPropertyChanged(nameof(RequirementsDescription));
        NotifyWorkflow();
    }

    private async Task OpenRequirementsAsync()
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog { Title = "Erforderliche Req-XML für FEE2Container auswählen", Filter = "Req-XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;
        BeginOperation();
        var token = _operationCancellation!.Token;
        try
        {
            var source = new RequirementsXml();
            var result = await source.ReadFromFileAsync(dialog.FileName);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess) throw new InvalidDataException(result.ErrorMessage);
            _ = new ContainerRequirementsCatalog(source.Document); // Validate before publishing the shared source.
            RequirementsProvider.RequirementsFile = source;
            RequirementsProvider.SourcePath = dialog.FileName;
            _useRequirements = true;
            OnPropertyChanged(nameof(UseRequirements));
            RefreshRequirementDefinitions();
            StatusText = "Req-XML eingelesen. Container aus Signalen erneut erstellen; vorhandene Bearbeitungen bleiben bis dahin erhalten.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Einlesen der Req-XML abgebrochen."; }
        catch (Exception exception) { ReportOperationFailure("Req-XML konnte nicht gelesen werden", exception); }
        finally { EndOperation(); }
    }
}

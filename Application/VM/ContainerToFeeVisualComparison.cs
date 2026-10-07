using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using VIBN_Tools.Application.View;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.Application.VM;

public sealed partial class ContainerToFeeVisualPageVM
{
    public ICommand CompareContainerFilesCommand { get; }

    private void OpenContainerFileComparison()
    {
        try
        {
            var current = HasPlan ? _planService.CreateEffectiveContainerDocument() : ContainerFileXml.Document([]);
            var vm = new ContainerFileComparisonVM(current,
                HasPlan ? "Aktueller visueller Plan: " + SourceXmlPath : "Alte Datei oder aktuellen FEE-Stand auswählen",
                ReadComparisonFeeAsync, ApplyComparisonAsync,
                () => !IsBusy && !_feeSdkAborted && _connection.CanUseFeeFeatures && _connection.AreModelValidationObjectsCurrent);
            var window = new ContainerFileComparisonWindow(vm);
            if (System.Windows.Application.Current?.MainWindow is { } owner) window.Owner = owner;
            window.ShowDialog();
        }
        catch (Exception ex) { StatusText = "Dateivergleich konnte nicht geöffnet werden: " + ex.Message; }
    }

    private async Task<XDocument> ReadComparisonFeeAsync()
    {
        XDocument? document = null;
        await RunBusyAsync("Aktueller FEE-Stand für den Vergleich wird gelesen …", async token =>
        {
            document = await ContainerFileChangeApplier.ReadCurrentFileAsync(token, allowIncomplete: true);
        });
        return document ?? throw new InvalidOperationException(StatusText);
    }

    private async Task<string> ApplyComparisonAsync(IReadOnlyList<ContainerFileChange> changes, XDocument reviewed)
    {
        string? message = null;
        await RunBusyAsync("Geprüfte Containeränderungen werden auf FEE angewendet …", async token =>
        {
            if (HasPlan) await _planService.SaveSidecarAsync(cancellationToken: token);
            var progress = new Progress<VisualGenerationProgress>(item =>
            { GenerationProgress = item.Percent; GenerationProgressText = item.Message; });
            message = await ContainerFileChangeApplier.ApplyAsync(changes, reviewed, progress, token);
            StatusText = message;
            _log.Information(LogArea, message);
            if (HasPlan) await RefreshFeeStateAsync(token);
        });
        return message ?? throw new InvalidOperationException(StatusText);
    }
}

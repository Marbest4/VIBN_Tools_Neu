using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

public sealed class Fee2ContainerPageVM : MvvmBase
{
    private const string LogArea = "FEE2Container";
    private readonly Fee2ContainerService _service;
    private readonly FeeConnectionService _connection;
    private Fee2ContainerRootSelectionVM? _selectedRoot;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private int _progressValue;
    private string _statusText = "FEE verbinden und Hauptknoten einlesen.";

    public Fee2ContainerPageVM()
        : this(new Fee2ContainerService(), Services.Connection ?? new FeeConnectionService()) { }

    internal Fee2ContainerPageVM(Fee2ContainerService service, FeeConnectionService connection)
    {
        _service = service;
        _connection = connection;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public ObservableCollection<Fee2ContainerRootSelectionVM> Roots { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ObservableCollection<Fee2ContainerFoundContainerVM> FoundContainers { get; } = new();
    public ObservableCollection<Fee2ContainerFoundSignalVM> FoundSignals { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelCommand { get; }
    public FeeConnectionService Connection => _connection;

    public Fee2ContainerRootSelectionVM? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (ReferenceEquals(_selectedRoot, value)) return;
            _selectedRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionSummary));
            RefreshSelectionDetails();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(RefreshUnavailableReason));
            OnPropertyChanged(nameof(ExportUnavailableReason));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set { _progressValue = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public bool CanRefresh => !IsBusy && Connection.CanUseFeeFeatures;
    public bool CanExport => !IsBusy && Roots.Any(root => root.IsSelected);
    public string RefreshUnavailableReason => Connection.CanUseFeeFeatures
        ? (IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty)
        : Connection.UnavailableReason;
    public string ExportUnavailableReason => !Roots.Any(root => root.IsSelected)
        ? "Mindestens einen BasicFrame über die Checkbox auswählen."
        : IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty;

    public string SelectionSummary => SelectedRoot is null
        ? "Kein Root für die Detailansicht ausgewählt."
        : $"{SelectedRoot.Root.SourceKind}; {SelectedRoot.Root.ContainerCount} Container, " +
          $"{SelectedRoot.Root.SignalCount} Signale; " +
          (SelectedRoot.Root.HasProvenance
              ? $"{SelectedRoot.Root.UpdatedSignalCount} aktuell, {SelectedRoot.Root.MissingSignalCount} fehlend; " +
                $"{SelectedRoot.Root.UpdatedSlotCount} Slotrouten, {SelectedRoot.Root.UnresolvedSlotCount} ungeklärt; " +
                $"Quellfingerprint {Shorten(SelectedRoot.Root.Provenance?.SourceFingerprint)}"
              : $"{SelectedRoot.Root.InspectedObjectCount} Objekte geprüft, " +
                $"{SelectedRoot.Root.IgnoredObjectCount} nicht containerrelevant, " +
                $"{SelectedRoot.Root.ReconstructionIssues?.Count ?? 0} Prüfhinweis(e)");

    private async Task RefreshAsync()
    {
        if (!CanRefresh) { StatusText = RefreshUnavailableReason; return; }
        BeginOperation();
        try
        {
            var progress = new Progress<Fee2ContainerProgress>(update =>
            {
                ProgressValue = update.Percent;
                StatusText = update.Message;
            });
            var result = await _service.DiscoverAsync(
                _operationCancellation!.Token,
                reconstructLegacyRoots: true,
                progress);
            Roots.Clear();
            foreach (var root in result.Roots)
            {
                var selection = new Fee2ContainerRootSelectionVM(root);
                selection.PropertyChanged += OnRootSelectionChanged;
                Roots.Add(selection);
            }
            Issues.Clear();
            foreach (var issue in result.Issues)
                Issues.Add($"{issue.RootName}: {issue.Message}".TrimStart(':', ' '));
            SelectedRoot = Roots.FirstOrDefault();
            if (SelectedRoot is not null) SelectedRoot.IsSelected = true;
            StatusText = result.Roots.Count == 0
                ? "Keine obersten BasicFrames im geöffneten FEE-Projekt gefunden."
                : $"{result.Roots.Count} oberste BasicFrame(s) gefunden; " +
                  $"{result.IgnoredWithoutProvenance} ohne Provenienz live rekonstruiert; " +
                  $"{result.Issues.Count} Hinweis(e).";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Einlesen der FEE-Roots wurde abgebrochen.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"FEE-Roots konnten nicht gelesen werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally { EndOperation(); }
    }

    private async Task ExportAsync()
    {
        var selectedRoots = Roots.Where(root => root.IsSelected).Select(root => root.Root).ToArray();
        if (!CanExport || selectedRoots.Length == 0) { StatusText = ExportUnavailableReason; return; }
        var sourceName = selectedRoots.Length == 1 ? selectedRoots[0].Name : $"{selectedRoots.Length}-FEE-Roots";
        var dialog = new SaveFileDialog
        {
            Title = "ContainerFile aus ausgewählten FEE-Hauptknoten exportieren",
            Filter = "Container XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            FileName = $"{ExportFileNamePolicy.Create(sourceName, "FEE2Container")}.container.xml",
            AddExtension = true,
            DefaultExt = ".xml",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        BeginOperation();
        try
        {
            StatusText = "Ausgewählte FEE-Teilbäume, Logiken, Slots und Signale werden zusammengeführt …";
            var export = await _service.CreateCombinedExportAsync(selectedRoots, _operationCancellation!.Token);
            Issues.Clear();
            foreach (var issue in export.Issues)
                Issues.Add($"{(issue.ObjectGuid is Guid guid ? $"{guid:D}: " : string.Empty)}{issue.Message}");
            if (export.Snapshot.ContainerCount == 0)
            {
                StatusText = "In den ausgewählten Hauptknoten wurden keine sicher unterstützten Container erkannt. Es wurde keine Datei geschrieben.";
                ApplicationLogService.Instance.Warning(LogArea, StatusText);
                return;
            }
            _operationCancellation.Token.ThrowIfCancellationRequested();
            FeeContainerProvenanceCodec.SaveAtomically(export.Snapshot, dialog.FileName);
            StatusText = $"ContainerFile aus {selectedRoots.Length} Root(s) exportiert: " +
                         $"{export.Snapshot.ContainerCount} Container, {export.Snapshot.SignalCount} Signale, " +
                         $"{export.Issues.Count} Prüfhinweis(e). Datei: {dialog.FileName}";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "ContainerFile-Export wurde abgebrochen; es wurde keine Datei geschrieben.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"ContainerFile konnte nicht exportiert werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally { EndOperation(); }
    }

    private void BeginOperation()
    {
        _operationCancellation = new CancellationTokenSource();
        ProgressValue = 0;
        IsBusy = true;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        IsBusy = false;
    }

    private void Cancel()
    {
        _operationCancellation?.Cancel();
        StatusText = "Abbruch wurde angefordert; der laufende FEE-Batch wird noch sauber beendet …";
    }

    private void OnRootSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Fee2ContainerRootSelectionVM.IsSelected)) return;
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(FeeConnectionService.CanUseFeeFeatures) and
            not nameof(FeeConnectionService.UnavailableReason)) return;
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RefreshUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Shorten(string? value) => string.IsNullOrWhiteSpace(value)
        ? "nicht vorhanden" : value[..Math.Min(12, value.Length)];

    private void RefreshSelectionDetails()
    {
        FoundContainers.Clear();
        FoundSignals.Clear();
        var document = SelectedRoot?.Root.Provenance?.ContainerDocument;
        if (document is null) return;
        foreach (var container in document.Descendants("Container"))
        {
            var component = container.Element("Component")?.Value ?? string.Empty;
            var type = container.Element("Type")?.Value ?? string.Empty;
            var entries = container.Descendants("Entry").ToArray();
            FoundContainers.Add(new Fee2ContainerFoundContainerVM(
                container.Attribute("id")?.Value ?? string.Empty,
                component,
                type,
                entries.Count(entry => !string.IsNullOrWhiteSpace(entry.Element("Signal")?.Value))));
            foreach (var entry in entries)
            {
                var signal = entry.Element("Signal")?.Value ?? string.Empty;
                var slot = entry.Element("Slot")?.Value ?? string.Empty;
                FoundSignals.Add(new Fee2ContainerFoundSignalVM(
                    component, type, signal, slot,
                    entry.Element("Address")?.Value ?? string.Empty,
                    entry.Element("DataType")?.Value ?? string.Empty,
                    !string.IsNullOrWhiteSpace(signal) && !string.IsNullOrWhiteSpace(slot),
                    entry.Element("Note")?.Value ?? string.Empty));
            }
        }
    }
}

public sealed class Fee2ContainerRootSelectionVM : NotifyBase
{
    private bool _isSelected;
    public Fee2ContainerRootSelectionVM(Fee2ContainerRoot root) => Root = root;
    public Fee2ContainerRoot Root { get; }
    public bool IsSelected { get => _isSelected; set => SetPropertyChange(ref _isSelected, value); }
    public Guid Guid => Root.Guid;
    public string Name => Root.Name;
    public string SourceKind => Root.SourceKind;
    public int ContainerCount => Root.ContainerCount;
    public int SignalCount => Root.SignalCount;
    public int UpdatedSignalCount => Root.UpdatedSignalCount;
    public int MissingSignalCount => Root.MissingSignalCount;
    public int UpdatedSlotCount => Root.UpdatedSlotCount;
    public int UnresolvedSlotCount => Root.UnresolvedSlotCount;
    public FeeContainerProvenanceSnapshot? Provenance => Root.Provenance;
}

public sealed record Fee2ContainerFoundContainerVM(string Id, string Component, string Type, int AssignedSignalCount);

public sealed record Fee2ContainerFoundSignalVM(
    string Container, string ContainerType, string Signal, string Slot,
    string Address, string DataType, bool IsAssigned, string Note)
{
    public string AssignmentState => IsAssigned ? "Zugeordnet" : "Keine rücklesbare Zuordnung";
}

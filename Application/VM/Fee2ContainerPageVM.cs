using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

public sealed class Fee2ContainerPageVM : MvvmBase
{
    private const string LogArea = "FEE2Container";
    private readonly Fee2ContainerService _service;
    private readonly FeeConnectionService _connection;
    private Fee2ContainerRoot? _selectedRoot;
    private bool _isBusy;
    private string _statusText = "FEE verbinden und generierte Roots einlesen.";

    public Fee2ContainerPageVM()
        : this(
            new Fee2ContainerService(),
            Services.Connection ?? new FeeConnectionService())
    {
    }

    internal Fee2ContainerPageVM(
        Fee2ContainerService service,
        FeeConnectionService connection)
    {
        _service = service;
        _connection = connection;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public ObservableCollection<Fee2ContainerRoot> Roots { get; } = new();

    public ObservableCollection<string> Issues { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand ExportCommand { get; }

    public FeeConnectionService Connection => _connection;

    public Fee2ContainerRoot? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (ReferenceEquals(_selectedRoot, value))
                return;
            _selectedRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(SelectionSummary));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(CanExport));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool CanRefresh => !IsBusy && Connection.CanUseFeeFeatures;

    public bool CanExport => !IsBusy && SelectedRoot is not null;

    public string RefreshUnavailableReason => Connection.CanUseFeeFeatures
        ? (IsBusy ? "Ein FEE2Container-Vorgang läuft bereits." : string.Empty)
        : Connection.UnavailableReason;

    public string ExportUnavailableReason => SelectedRoot is null
        ? "Zuerst einen BasicFrame als Hauptknoten auswählen."
        : IsBusy
            ? "Ein FEE2Container-Vorgang läuft bereits."
            : string.Empty;

    public string SelectionSummary => SelectedRoot is null
        ? "Kein Root ausgewählt."
        : $"{SelectedRoot.SourceKind}; " +
          $"{SelectedRoot.ContainerCount} Container, {SelectedRoot.SignalCount} Signale; " +
          (SelectedRoot.HasProvenance
              ? $"{SelectedRoot.UpdatedSignalCount} aus aktuellem FEE gelesen, " +
                $"{SelectedRoot.MissingSignalCount} fehlend; " +
                $"{SelectedRoot.UpdatedSlotCount} Slotrouten gelesen, " +
                $"{SelectedRoot.UnresolvedSlotCount} ungeklärt; " +
                $"Quellfingerprint {Shorten(SelectedRoot.Provenance?.SourceFingerprint)}"
              : $"{SelectedRoot.InspectedObjectCount} Objekte geprüft, " +
                $"{SelectedRoot.IgnoredObjectCount} nicht containerrelevant, " +
                $"{SelectedRoot.ReconstructionIssues?.Count ?? 0} Prüfhinweis(e)");

    private async Task RefreshAsync()
    {
        if (!CanRefresh)
        {
            StatusText = RefreshUnavailableReason;
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "FEE-Hauptknoten und Provenienz werden eingelesen …";
            var result = await _service.DiscoverAsync();
            Roots.Clear();
            foreach (var root in result.Roots)
                Roots.Add(root);
            Issues.Clear();
            foreach (var issue in result.Issues)
                Issues.Add($"{issue.RootName}: {issue.Message}".TrimStart(':', ' '));
            SelectedRoot = Roots.FirstOrDefault();
            StatusText = result.Roots.Count == 0
                ? "Keine BasicFrames im geöffneten FEE-Projekt gefunden."
                : $"{result.Roots.Count} BasicFrame(s) gefunden; " +
                  $"{result.IgnoredWithoutProvenance} ohne Container2FEE-Provenienz werden bei Export aus der FEE-Struktur rekonstruiert; " +
                  $"{result.Issues.Count} Hinweis(e).";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"FEE-Roots konnten nicht gelesen werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (!CanExport || SelectedRoot is null)
        {
            StatusText = ExportUnavailableReason;
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "ContainerFile aus ausgewähltem FEE-Hauptknoten exportieren",
            Filter = "Container XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            FileName = $"{ExportFileNamePolicy.Create(SelectedRoot.Name, "FEE2Container")}.container.xml",
            AddExtension = true,
            DefaultExt = ".xml",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        try
        {
            StatusText = SelectedRoot.HasProvenance
                ? "ContainerFile wird aus der Provenienz aufgebaut …"
                : "FEE-Teilbaum, Logiken, Slots und Signale werden rekonstruiert …";
            var export = await _service.CreateExportAsync(SelectedRoot);
            Issues.Clear();
            foreach (var issue in export.Issues)
            {
                var objectLabel = issue.ObjectGuid is Guid guid ? $"{guid:D}: " : string.Empty;
                Issues.Add($"{SelectedRoot.Name}: {objectLabel}{issue.Message}");
            }
            if (export.Snapshot.ContainerCount == 0)
            {
                StatusText = "Im ausgewählten Hauptknoten wurden keine sicher unterstützten Container erkannt. " +
                             "Es wurde keine Datei geschrieben.";
                ApplicationLogService.Instance.Warning(LogArea, StatusText);
                return;
            }

            FeeContainerProvenanceCodec.SaveAtomically(export.Snapshot, dialog.FileName);
            StatusText = export.UsedProvenance
                ? $"ContainerFile aus exakter Container2FEE-Provenienz exportiert: {dialog.FileName}"
                : $"ContainerFile aus FEE-Struktur rekonstruiert: {export.Snapshot.ContainerCount} Container, " +
                  $"{export.Snapshot.SignalCount} Signale, {export.IgnoredObjectCount} nicht unterstützte/nicht " +
                  $"containerrelevante Objekte ignoriert, {export.Issues.Count} Prüfhinweis(e). Datei: {dialog.FileName}";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"ContainerFile konnte nicht exportiert werden: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(FeeConnectionService.CanUseFeeFeatures) and
            not nameof(FeeConnectionService.UnavailableReason))
            return;
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(RefreshUnavailableReason));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Shorten(string? value) => string.IsNullOrWhiteSpace(value)
        ? "nicht vorhanden"
        : value[..Math.Min(12, value.Length)];

}

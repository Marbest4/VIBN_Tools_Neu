using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Settings;
using VIBN_Tools.SpecialDevices;

namespace VIBN_Tools.Application.VM;

public sealed class Fee2SpecialDevicesPageVM : MvvmBase
{
    private readonly Fee2SpecialDevicesService _service;
    private readonly FeeConnectionService _connection;
    private Fee2SpecialDeviceRoot? _selectedRoot;
    private bool _isBusy;
    private int _progressValue;
    private int _progressMaximum = 1;
    private string _statusText = "FEE verbinden und erzeugte Special-Device-Roots einlesen.";

    public Fee2SpecialDevicesPageVM()
        : this(new Fee2SpecialDevicesService(), Services.Connection ?? new FeeConnectionService())
    {
    }

    internal Fee2SpecialDevicesPageVM(
        Fee2SpecialDevicesService service,
        FeeConnectionService connection)
    {
        _service = service;
        _connection = connection;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);
        ExportAllCommand = new AsyncRelayCommand(ExportAllAsync, () => CanExportAll);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => CanRemoveSelected);
        RemoveAllCommand = new AsyncRelayCommand(RemoveAllAsync, () => CanRemoveAll);
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public ObservableCollection<Fee2SpecialDeviceRoot> Roots { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand RemoveAllCommand { get; }
    public FeeConnectionService Connection => _connection;

    public Fee2SpecialDeviceRoot? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (ReferenceEquals(_selectedRoot, value))
                return;
            _selectedRoot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CanRemoveSelected));
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
            OnPropertyChanged(nameof(CanExportAll));
            OnPropertyChanged(nameof(CanRemoveSelected));
            OnPropertyChanged(nameof(CanRemoveAll));
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

    public int ProgressValue
    {
        get => _progressValue;
        private set { _progressValue = value; OnPropertyChanged(); }
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        private set { _progressMaximum = Math.Max(1, value); OnPropertyChanged(); }
    }

    public bool CanRefresh => !IsBusy && Connection.CanUseFeeFeatures;
    public bool CanExport => !IsBusy && SelectedRoot is not null;
    public bool CanExportAll => !IsBusy && Roots.Count > 0;
    public bool CanRemoveSelected => !IsBusy && SelectedRoot is not null;
    public bool CanRemoveAll => !IsBusy && Roots.Count > 0;
    public string RefreshUnavailableReason => Connection.CanUseFeeFeatures
        ? (IsBusy ? "Ein FEE2SpecialDevices-Vorgang läuft bereits." : string.Empty)
        : Connection.UnavailableReason;
    public string ExportUnavailableReason => SelectedRoot is null
        ? "Zuerst einen erkannten Special-Device-Root auswählen."
        : IsBusy ? "Ein FEE2SpecialDevices-Vorgang läuft bereits." : string.Empty;
    public string SelectionSummary => SelectedRoot is null
        ? "Kein Root ausgewählt."
        : $"{SelectedRoot.SourceKind}; {SelectedRoot.Snapshot.Manufacturer} / {SelectedRoot.Snapshot.DeviceType}; " +
          $"{SelectedRoot.Snapshot.Signals.Count} Signale, {SelectedRoot.UpdatedSignalCount} aktuell, " +
          $"{SelectedRoot.MissingSignalCount} fehlend.";

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
            ProgressValue = 0;
            ProgressMaximum = 1;
            var progress = new Progress<Fee2SpecialDevicesProgress>(item =>
            {
                ProgressMaximum = item.Total;
                ProgressValue = item.Current;
                StatusText = item.Message;
            });
            var result = await _service.DiscoverAsync(progress: progress);
            Roots.Clear();
            foreach (var root in result.Roots)
                Roots.Add(root);
            Issues.Clear();
            foreach (var issue in result.Issues)
                Issues.Add($"{issue.RootName}: {issue.Message}".TrimStart(':', ' '));
            SelectedRoot = Roots.FirstOrDefault();
            OnPropertyChanged(nameof(CanExportAll));
            OnPropertyChanged(nameof(CanRemoveAll));
            StatusText = result.Roots.Count == 0
                ? $"Keine eindeutig exportierbaren Geräte gefunden. {result.IgnoredWithoutProvenance} BasicFrames waren nicht rekonstruierbar."
                : $"{result.Roots.Count} Gerät(e) in der gesamten BasicFrame-Hierarchie gefunden; {result.IgnoredWithoutProvenance} Frames nicht eindeutig rekonstruierbar; {result.Issues.Count} Hinweis(e).";
            ApplicationLogService.Instance.Information("FEE2SpecialDevices", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"Special Devices konnten nicht aus FEE gelesen werden: {exception.Message}";
            ApplicationLogService.Instance.Error("FEE2SpecialDevices", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ExportAllAsync()
    {
        if (!CanExportAll)
        {
            StatusText = "Es sind keine eingelesenen FEE-Geräte zum Export vorhanden.";
            return Task.CompletedTask;
        }
        var dialog = new OpenFolderDialog
        {
            Title = "Zielordner für alle Special Devices auswählen",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
            return Task.CompletedTask;

        IsBusy = true;
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in Roots)
            {
                var baseName = ExportFileNamePolicy.Create(root.Snapshot.Prefix, "FEE2SpecialDevices");
                var fileName = baseName;
                var suffix = 2;
                while (!usedNames.Add(fileName) ||
                       File.Exists(Path.Combine(dialog.FolderName, $"{fileName}.specialdevice.json")))
                    fileName = $"{baseName}_{suffix++}";
                FeeSpecialDeviceProvenanceCodec.SaveAtomically(
                    root.Snapshot,
                    Path.Combine(dialog.FolderName, $"{fileName}.specialdevice.json"));
            }
            StatusText = $"{Roots.Count} Special Device(s) wurden nach {dialog.FolderName} exportiert.";
            ApplicationLogService.Instance.Information("FEE2SpecialDevices", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"Nicht alle Special Devices konnten exportiert werden: {exception.Message}";
            ApplicationLogService.Instance.Error("FEE2SpecialDevices", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
        return Task.CompletedTask;
    }

    private Task RemoveSelectedAsync()
    {
        if (SelectedRoot is null)
            return Task.CompletedTask;
        var index = Roots.IndexOf(SelectedRoot);
        Roots.Remove(SelectedRoot);
        SelectedRoot = Roots.Count == 0 ? null : Roots[Math.Min(index, Roots.Count - 1)];
        NotifyCollectionStateChanged();
        StatusText = "Das eingelesene Gerät wurde aus der Liste entfernt; das FEE-Projekt wurde nicht verändert.";
        return Task.CompletedTask;
    }

    private Task RemoveAllAsync()
    {
        Roots.Clear();
        SelectedRoot = null;
        NotifyCollectionStateChanged();
        StatusText = "Alle eingelesenen Geräte wurden aus der Liste entfernt; das FEE-Projekt wurde nicht verändert.";
        return Task.CompletedTask;
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(CanExportAll));
        OnPropertyChanged(nameof(CanRemoveAll));
        OnPropertyChanged(nameof(CanRemoveSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private Task ExportAsync()
    {
        if (!CanExport || SelectedRoot is null)
        {
            StatusText = ExportUnavailableReason;
            return Task.CompletedTask;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Special Device aus FEE exportieren",
            Filter = "VIBN Special Device (*.specialdevice.json)|*.specialdevice.json|JSON (*.json)|*.json",
            FileName = $"{ExportFileNamePolicy.Create(SelectedRoot.Snapshot.Prefix, "FEE2SpecialDevices")}.specialdevice.json",
            AddExtension = true,
            DefaultExt = ".specialdevice.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
            return Task.CompletedTask;
        IsBusy = true;
        try
        {
            FeeSpecialDeviceProvenanceCodec.SaveAtomically(SelectedRoot.Snapshot, dialog.FileName);
            StatusText = $"Special Device wurde exportiert: {dialog.FileName}";
            ApplicationLogService.Instance.Information("FEE2SpecialDevices", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"Special Device konnte nicht exportiert werden: {exception.Message}";
            ApplicationLogService.Instance.Error("FEE2SpecialDevices", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
        return Task.CompletedTask;
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

}

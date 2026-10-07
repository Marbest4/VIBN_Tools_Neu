using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.Win32;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;

namespace VIBN_Tools.Application.VM;

public sealed class ContainerFileComparisonVM : MvvmBase
{
    private XDocument _oldFile;
    private XDocument _newFile;
    private ContainerFileChange? _selected;
    private string _editedXml = "";
    private string _status = "Neue Datei auswählen und die Änderungen prüfen.";
    private string _oldLabel;
    private string _newLabel = "Neue Datei";
    private bool _busy;
    private bool _applied;
    private readonly Dictionary<ContainerFileChange, string> _edits = [];
    private readonly Func<Task<XDocument>> _readFee;
    private readonly Func<IReadOnlyList<ContainerFileChange>, XDocument, Task<string>> _apply;
    private readonly Func<bool> _canUseFee;

    public ContainerFileComparisonVM(XDocument oldFile, string oldLabel, Func<Task<XDocument>> readFee,
        Func<IReadOnlyList<ContainerFileChange>, XDocument, Task<string>> apply, Func<bool> canUseFee)
    {
        _oldFile = new XDocument(oldFile); _newFile = new XDocument(oldFile); _oldLabel = oldLabel;
        _readFee = readFee; _apply = apply; _canUseFee = canUseFee;
        OpenOldCommand = new RelayCommand(() => OpenFile(true), () => !IsBusy);
        OpenNewCommand = new RelayCommand(() => OpenFile(false), () => !IsBusy);
        ReadFeeCommand = new AsyncRelayCommand(ReadFeeAsync, () => !IsBusy && _canUseFee());
        CommitEditsCommand = new RelayCommand(CommitEdits, () => !IsBusy && SelectedChange?.NewContainer is not null);
        SaveReviewedCommand = new RelayCommand(SaveReviewed, () => !IsBusy && Changes.Count > 0);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && !_applied && _canUseFee() &&
            Changes.Any(item => item.IsSelected && item.Kind != ContainerFileChangeKind.Unchanged));
        SelectChangesCommand = new RelayCommand(() => { foreach (var item in Changes) item.IsSelected = item.Kind != ContainerFileChangeKind.Unchanged; RefreshSelection(); });
        ClearSelectionCommand = new RelayCommand(() => { foreach (var item in Changes) item.IsSelected = false; RefreshSelection(); });
    }

    public ObservableCollection<ContainerFileChange> Changes { get; } = [];
    public ObservableCollection<ContainerFileDifference> Rows { get; } = [];
    public ObservableCollection<ContainerFileDifference> InventoryRows { get; } = [];
    public string OldLabel { get => _oldLabel; private set { _oldLabel = value; OnPropertyChanged(); } }
    public string NewLabel { get => _newLabel; private set { _newLabel = value; OnPropertyChanged(); } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _busy; private set { _busy = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
    public string EditedXml { get => _editedXml; set { _editedXml = value; OnPropertyChanged(); } }
    public ContainerFileChange? SelectedChange
    {
        get => _selected;
        set
        {
            if (_selected?.NewContainer is not null) _edits[_selected] = EditedXml;
            _selected = value; OnPropertyChanged(); Rows.Clear();
            foreach (var row in value?.Rows ?? []) Rows.Add(row);
            EditedXml = value is not null && _edits.TryGetValue(value, out var edited) ? edited : value?.NewContainer?.ToString() ?? "";
        }
    }
    public ICommand OpenOldCommand { get; }
    public ICommand OpenNewCommand { get; }
    public ICommand ReadFeeCommand { get; }
    public ICommand CommitEditsCommand { get; }
    public ICommand SaveReviewedCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand SelectChangesCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    private void OpenFile(bool old)
    {
        var dialog = new OpenFileDialog { Title = old ? "Altes ContainerFile" : "Neues ContainerFile", Filter = "ContainerFile (*.xml)|*.xml" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var document = ContainerFileXml.Load(dialog.FileName);
            _ = ContainerFileComparison.Compare(old ? document : _oldFile, old ? _newFile : document);
            if (old) { _oldFile = document; OldLabel = dialog.FileName; }
            else { _newFile = document; NewLabel = dialog.FileName; }
            Rebuild();
        }
        catch (Exception ex) { Status = "Datei konnte nicht verglichen werden: " + ex.Message; }
    }

    private async Task ReadFeeAsync()
    {
        IsBusy = true;
        try { _oldFile = await _readFee(); OldLabel = "Aktueller FEE-Projektstand"; Rebuild(); }
        catch (Exception ex) { Status = "FEE-Stand konnte nicht gelesen werden: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void Rebuild(IReadOnlyDictionary<string, bool>? selection = null)
    {
        var changes = ContainerFileComparison.Compare(_oldFile, _newFile);
        _selected = null; _edits.Clear();
        Changes.Clear(); foreach (var item in changes)
        {
            if (selection?.TryGetValue(item.Name + "\u001f" + item.Type, out var selected) == true) item.IsSelected = selected;
            Changes.Add(item);
        }
        InventoryRows.Clear();
        foreach (var row in ContainerFileComparison.Align(Inventory(_oldFile), Inventory(_newFile))) InventoryRows.Add(row);
        SelectedChange = Changes.FirstOrDefault(item => item.Kind != ContainerFileChangeKind.Unchanged) ?? Changes.FirstOrDefault();
        _applied = false;
        Status = $"{Changes.Count(item => item.Kind == ContainerFileChangeKind.Added)} neu, " +
            $"{Changes.Count(item => item.Kind == ContainerFileChangeKind.Removed)} entfallen, " +
            $"{Changes.Count(item => item.Kind == ContainerFileChangeKind.Changed)} geändert. Auswahl und neuen XML-Stand vor Anwendung prüfen.";
        CommandManager.InvalidateRequerySuggested();
    }

    private static XElement Inventory(XDocument file) => new("Container",
        new XElement("AvailableSimObjects", file.Root?.Element("FeeInventory")?.Element("SimObjects")?.Elements() ?? []),
        new XElement("AvailableSignals", file.Root?.Element("FeeInventory")?.Element("Signals")?.Elements() ?? []));

    private void RefreshSelection()
    {
        var items = Changes.ToArray(); Changes.Clear(); foreach (var item in items) Changes.Add(item);
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CommitPendingEdit()
    {
        if (SelectedChange?.NewContainer is not null) _edits[SelectedChange] = EditedXml;
        if (!_edits.Any(pair => pair.Key.NewContainer?.ToString() != pair.Value)) return true;
        try
        {
            var selection = Changes.ToDictionary(item => item.Name + "\u001f" + item.Type, item => item.IsSelected);
            var containers = new List<XElement>();
            foreach (var item in Changes.Where(item => item.NewContainer is not null))
            {
                var edited = _edits.TryGetValue(item, out var text) ? ContainerFileXml.ParseContainer(text) : new XElement(item.NewContainer!);
                selection[edited.Element("Component")!.Value + "\u001f" + edited.Element("Type")!.Value] = item.IsSelected;
                containers.Add(edited);
            }
            var next = ContainerFileXml.Document(containers, _newFile.Root?.Element("FeeInventory"));
            _ = ContainerFileComparison.Compare(_oldFile, next);
            _newFile = next;
            Rebuild(selection); return true;
        }
        catch (Exception ex) { Status = "XML-Bearbeitung ist ungültig: " + ex.Message; return false; }
    }

    private void CommitEdits() => CommitPendingEdit();

    private XDocument ReviewedDocument() => ContainerFileXml.Document(Changes
        .Select(item => item.IsSelected ? item.NewContainer : item.OldContainer).OfType<XElement>(),
        _newFile.Root?.Element("FeeInventory"));

    private void SaveReviewed()
    {
        if (!CommitPendingEdit()) return;
        var dialog = new SaveFileDialog { Title = "Geprüften Containerstand speichern", Filter = "ContainerFile (*.xml)|*.xml", FileName = "Container.geprueft.xml" };
        if (dialog.ShowDialog() != true) return;
        try { ReviewedDocument().Save(dialog.FileName); Status = "Geprüfter Stand gespeichert: " + dialog.FileName; }
        catch (Exception ex) { Status = "Speichern fehlgeschlagen: " + ex.Message; }
    }

    private async Task ApplyAsync()
    {
        if (!CommitPendingEdit()) return;
        var selected = Changes.Where(item => item.IsSelected && item.Kind != ContainerFileChangeKind.Unchanged).ToArray();
        if (selected.Length == 0) return;
        IsBusy = true;
        try { Status = await _apply(selected, ReviewedDocument()); _applied = true; }
        catch (Exception ex) { Status = "Anwendung angehalten: " + ex.Message; _applied = true; }
        finally { IsBusy = false; }
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Rockwell;

namespace VIBN_Tools.Application.VM;

/// <summary>Desktop workflow for deterministic, staged Studio 5000 L5X changes.</summary>
public sealed class RockwellPageVM : MvvmBase
{
    private const string LogArea = "Rockwell";
    private RockwellProjectEditor? _editor;
    private string _sourcePath = string.Empty;
    private string _generatedPath = string.Empty;
    private string _statusText = "Eine Studio-5000-L5X-Datei auswählen.";
    private string _projectSummary = "Kein Projekt geladen.";
    private bool _isBusy;

    public RockwellPageVM()
    {
        OpenCommand = GetCommandBinding(Open);
        AddBasicsCommand = GetCommandBinding(() => Apply(editor => editor.EnsureSimulationBasics()));
        AddStandardSimulationCommand = GetCommandBinding(() => Apply(editor => editor.EnsureInputSimulation(safety: false)));
        AddSafetySimulationCommand = GetCommandBinding(() => Apply(editor => editor.EnsureInputSimulation(safety: true)));
        SaveGeneratedCommand = GetCommandBinding(SaveGenerated);
        OpenGeneratedCommand = GetCommandBinding(OpenGenerated);
    }

    public ObservableCollection<string> Messages { get; } = [];

    public ICommand OpenCommand { get; }
    public ICommand AddBasicsCommand { get; }
    public ICommand AddStandardSimulationCommand { get; }
    public ICommand AddSafetySimulationCommand { get; }
    public ICommand SaveGeneratedCommand { get; }
    public ICommand OpenGeneratedCommand { get; }

    public string SourcePath
    {
        get => _sourcePath;
        private set { _sourcePath = value; OnPropertyChanged(); }
    }

    public string GeneratedPath
    {
        get => _generatedPath;
        private set
        {
            _generatedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanOpenGenerated));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string ProjectSummary
    {
        get => _projectSummary;
        private set { _projectSummary = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEdit));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEdit => _editor is not null && !IsBusy;

    public bool CanOpenGenerated => !string.IsNullOrWhiteSpace(GeneratedPath) && File.Exists(GeneratedPath);

    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Rockwell Studio 5000 L5X auswählen",
            Filter = "Studio 5000 XML (*.l5x)|*.l5x|XML (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
            return;

        Run(() =>
        {
            _editor = RockwellProjectEditor.Load(dialog.FileName);
            SourcePath = _editor.SourcePath;
            GeneratedPath = string.Empty;
            Messages.Clear();
            RefreshSummary();
            StatusText = "L5X-Projekt geladen. Änderungen werden erst mit „Generierte L5X speichern“ geschrieben.";
            ApplicationLogService.Instance.Information(LogArea, $"L5X geladen: {SourcePath}");
        }, "L5X-Projekt konnte nicht geladen werden");
    }

    private void Apply(Func<RockwellProjectEditor, RockwellEditResult> action)
    {
        if (_editor is null)
        {
            StatusText = "Zuerst eine L5X-Datei auswählen.";
            return;
        }

        Run(() =>
        {
            var result = action(_editor);
            Messages.Clear();
            foreach (var message in result.Messages.Distinct(StringComparer.Ordinal))
                Messages.Add(message);
            RefreshSummary();
            StatusText = result.Changed
                ? $"Änderung vorgemerkt: {result.AddedItems} Element(e) ergänzt, {result.UpdatedItems} Element(e) angepasst."
                : "Keine Änderung erforderlich; die ausgewählten Rockwell-Bausteine sind bereits vorhanden.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }, "Rockwell-Projekt konnte nicht geändert werden");
    }

    private void SaveGenerated()
    {
        if (_editor is null)
        {
            StatusText = "Zuerst eine L5X-Datei auswählen.";
            return;
        }
        var defaultPath = Path.Combine(
            Path.GetDirectoryName(_editor.SourcePath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(_editor.SourcePath)}_Generated.L5X");
        var dialog = new SaveFileDialog
        {
            Title = "Generierte Rockwell-L5X speichern",
            Filter = "Studio 5000 XML (*.l5x)|*.l5x",
            FileName = Path.GetFileName(defaultPath),
            InitialDirectory = Path.GetDirectoryName(defaultPath),
            DefaultExt = ".L5X",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
            return;

        Run(() =>
        {
            GeneratedPath = _editor.SaveGenerated(dialog.FileName);
            StatusText = $"Generierte L5X gespeichert: {GeneratedPath}";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }, "Generierte L5X konnte nicht gespeichert werden");
    }

    private void OpenGenerated()
    {
        if (!CanOpenGenerated)
        {
            StatusText = "Es wurde noch keine generierte L5X gespeichert.";
            return;
        }
        Run(() => Process.Start(new ProcessStartInfo(GeneratedPath) { UseShellExecute = true }),
            "Generierte L5X konnte nicht geöffnet werden");
    }

    private void Run(Action action, string errorPrefix)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            StatusText = $"{errorPrefix}: {exception.Message}";
            ApplicationLogService.Instance.Error(LogArea, StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshSummary()
    {
        if (_editor is null)
        {
            ProjectSummary = "Kein Projekt geladen.";
            return;
        }
        var summary = _editor.GetSummary();
        ProjectSummary = $"Controller: {summary.ControllerName} · Programme: {summary.ProgramCount} " +
                         $"({summary.StandardProgramCount} Standard, {summary.SafetyProgramCount} Safety) · " +
                         $"Datentypen: {summary.DataTypeCount} · AOI: {summary.AddOnInstructionCount} · " +
                         $"Simulation: {(summary.HasSimulationBasics ? "Basic vorhanden" : "Basic fehlt")}, " +
                         $"A001 {summary.StandardSimulationRoutineCount}, Safety-A001 {summary.SafetySimulationRoutineCount}";
        OnPropertyChanged(nameof(CanEdit));
        CommandManager.InvalidateRequerySuggested();
    }
}

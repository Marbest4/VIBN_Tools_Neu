using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Rockwell;
using VIBN_Tools.Core.Diagnostics;

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
    private bool _isHelpVisible;
    private RockwellStandardDefinition? _selectedStandard;

    public RockwellPageVM()
    {
        foreach (var standard in RockwellStandardCatalog.All)
            Standards.Add(standard);
        _selectedStandard = Standards.FirstOrDefault();
        OpenCommand = GetCommandBinding(Open);
        AddBasicsCommand = GetCommandBinding(() => ApplyStage(1));
        AddStandardSimulationCommand = GetCommandBinding(() => ApplyStage(2));
        AddSafetySimulationCommand = GetCommandBinding(() => ApplyStage(3));
        SaveGeneratedCommand = GetCommandBinding(SaveGenerated);
        OpenGeneratedCommand = GetCommandBinding(OpenGenerated);
        ToggleHelpCommand = GetCommandBinding(() => IsHelpVisible = !IsHelpVisible);
    }

    public ObservableCollection<string> Messages { get; } = [];
    public ObservableCollection<RockwellStandardDefinition> Standards { get; } = [];

    public ICommand OpenCommand { get; }
    public ICommand AddBasicsCommand { get; }
    public ICommand AddStandardSimulationCommand { get; }
    public ICommand AddSafetySimulationCommand { get; }
    public ICommand SaveGeneratedCommand { get; }
    public ICommand OpenGeneratedCommand { get; }
    public ICommand ToggleHelpCommand { get; }

    public RockwellStandardDefinition? SelectedStandard
    {
        get => _selectedStandard;
        set
        {
            if (ReferenceEquals(_selectedStandard, value))
                return;
            _selectedStandard = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEdit));
            StatusText = value is null
                ? "Bitte einen Rockwell-Standard auswählen."
                : $"Standard '{value.DisplayName}' ausgewählt. Nun L5X laden und die Schritte 1 bis 3 ausführen.";
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsHelpVisible
    {
        get => _isHelpVisible;
        private set { _isHelpVisible = value; OnPropertyChanged(); }
    }

    public string WorkflowHelp =>
        "Voraussetzung: ein L5X-Export des Studio-5000-Projekts. Zuerst den Standard auswählen und die " +
        "L5X laden. Schritt 1 ergänzt nur fehlende GCCS-Basisobjekte. Schritt 2 erzeugt die Standard-A001-" +
        "Simulation, Schritt 3 die Safety-A001-Simulation. Jeder Schritt ist idempotent und ändert nur " +
        "das Arbeitsmodell; geschrieben wird erst mit 'Generierte L5X speichern'. 'Generierte L5X öffnen' " +
        "übergibt die Datei an die Windows-L5X-Zuordnung. Dafür muss Studio 5000 Logix Designer installiert " +
        "und für L5X registriert sein; Studio 5000 zeigt anschließend seinen Importdialog.";

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

    public bool CanEdit => _editor is not null && SelectedStandard is not null && !IsBusy;

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

    private void ApplyStage(int stage)
    {
        if (SelectedStandard is null)
        {
            StatusText = "Bitte zuerst einen Rockwell-Standard auswählen.";
            return;
        }
        Apply(editor => SelectedStandard.ApplyStage(editor, stage));
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
        Run(() =>
        {
            var process = Process.Start(new ProcessStartInfo(GeneratedPath) { UseShellExecute = true });
            if (process is null)
                throw new InvalidOperationException(
                    "Windows konnte keine Anwendung für L5X starten. Ist Studio 5000 Logix Designer installiert und die L5X-Dateizuordnung vorhanden?");
            StatusText = "Generierte L5X wurde an Studio 5000 übergeben. Den Importdialog in Studio 5000 bestätigen.";
            ApplicationLogService.Instance.Information(LogArea, StatusText);
        }, "Generierte L5X konnte nicht in Studio 5000 geöffnet werden");
    }

    private void Run(Action action, string errorPrefix)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        using var measurement = PerformanceMeasurementService.Instance.Start(LogArea, errorPrefix);
        try
        {
            action();
        }
        catch (Exception exception)
        {
            measurement.MarkFailed();
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

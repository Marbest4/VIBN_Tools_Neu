using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using VIBN_Tools.Application.Quality;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Quality;

namespace VIBN_Tools.Application.VM;

public sealed class ProjectQualityPageVM : MvvmBase
{
    private readonly JsonProjectProfileStore _profileStore;
    private readonly SignalIdentityRegistry _signalRegistry;
    private readonly ContainerSignalObservationReader _signalReader;
    private readonly ProjectQualityGateService _qualityGate;
    private readonly QualityGateReportWriter _reportWriter;
    private readonly IFolderSelectionService _folderSelection;
    private ProjectProfile? _selectedProfile;
    private QualityGateReport? _lastReport;
    private bool _isBusy;
    private string _statusText = "Projektprofil auswählen oder neu anlegen.";

    public ProjectQualityPageVM()
        : this(
            new JsonProjectProfileStore(),
            new SignalIdentityRegistry(),
            new ContainerSignalObservationReader(),
            new ProjectQualityGateService(
                adapters:
                [
                    new FeeSimulationPlatformAdapter(Services.Connection),
                    .. SimulationAdapterCatalog.CreateDefaults(),
                ]),
            new QualityGateReportWriter(),
            new WpfFolderSelectionService())
    {
    }

    internal ProjectQualityPageVM(
        JsonProjectProfileStore profileStore,
        SignalIdentityRegistry signalRegistry,
        ContainerSignalObservationReader signalReader,
        ProjectQualityGateService qualityGate,
        QualityGateReportWriter reportWriter,
        IFolderSelectionService folderSelection)
    {
        _profileStore = profileStore;
        _signalRegistry = signalRegistry;
        _signalReader = signalReader;
        _qualityGate = qualityGate;
        _reportWriter = reportWriter;
        _folderSelection = folderSelection;
        Editor = new ProjectProfileEditorVM();

        NewProfileCommand = GetCommandBinding(NewProfile);
        SaveProfileCommand = GetCommandBinding(SaveProfile);
        DeleteProfileCommand = GetCommandBinding(DeleteProfile);
        BrowseProjectRootCommand = GetCommandBinding(BrowseProjectRoot);
        BrowseRequirementsCommand = GetCommandBinding(() => Editor.RequirementsPath = SelectXml(Editor.RequirementsPath, "Requirements XML auswählen"));
        BrowseContainerCommand = GetCommandBinding(() => Editor.ContainerPath = SelectXml(Editor.ContainerPath, "Container XML auswählen"));
        BrowseLibraryCommand = GetCommandBinding(BrowseLibrary);
        RunQualityGateCommand = GetCommandBindingAsync(RunQualityGateAsync);
        AnalyzeSignalsCommand = GetCommandBindingAsync(() => ReconcileSignalsAsync(false));
        ApplySignalRegistryCommand = GetCommandBindingAsync(() => ReconcileSignalsAsync(true));
        ExportReportCommand = GetCommandBinding(ExportReport);
        LoadProfiles();
    }

    public ObservableCollection<ProjectProfile> Profiles { get; } = [];
    public ObservableCollection<QualityFinding> Findings { get; } = [];
    public ObservableCollection<QualityEvidence> Evidence { get; } = [];
    public ObservableCollection<SimulationTestScenario> Scenarios { get; } = [];
    public ObservableCollection<SimulationAdapterProbe> AdapterProbes { get; } = [];
    public ObservableCollection<SignalIdentity> SignalIdentities { get; } = [];
    public ProjectProfileEditorVM Editor { get; }

    public ICommand NewProfileCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand BrowseProjectRootCommand { get; }
    public ICommand BrowseRequirementsCommand { get; }
    public ICommand BrowseContainerCommand { get; }
    public ICommand BrowseLibraryCommand { get; }
    public ICommand RunQualityGateCommand { get; }
    public ICommand AnalyzeSignalsCommand { get; }
    public ICommand ApplySignalRegistryCommand { get; }
    public ICommand ExportReportCommand { get; }

    public ProjectProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value))
                return;
            _selectedProfile = value;
            OnPropertyChanged();
            if (value is not null)
                Editor.Load(value);
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string Limitations =>
        "Offline verifiziert: Profil, XML, Signalidentitäten, Manifest und Testszenarien. " +
        "TIA gilt erst nach erfolgreichem Compile als nachgewiesen. Emulate3D/EKS prüfen ohne Hersteller-SDK derzeit nur die lokale Bereitschaft. " +
        "Generierte Testszenarien benötigen vor einer Live-Ausführung eine fachliche Freigabe.";

    private void LoadProfiles()
    {
        var collection = _profileStore.Load();
        Profiles.ReplaceWith(collection.Profiles);
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == collection.SelectedProfileId) ?? Profiles.FirstOrDefault();
        if (SelectedProfile is null)
            NewProfile();
        SignalIdentities.ReplaceWith(_signalRegistry.Load());
    }

    private void NewProfile()
    {
        var profile = ProjectProfile.Create($"Projekt {Profiles.Count + 1}");
        Profiles.Add(profile);
        SelectedProfile = profile;
        StatusText = "Neues, noch nicht gespeichertes Projektprofil angelegt.";
    }

    private void SaveProfile()
    {
        var updated = Editor.Build();
        var old = Profiles.FirstOrDefault(profile => profile.Id == updated.Id);
        if (old is null)
            Profiles.Add(updated);
        else
            Profiles[Profiles.IndexOf(old)] = updated;
        SelectedProfile = updated;
        _profileStore.Save(new ProjectProfileCollection(updated.Id, Profiles.ToArray()));
        StatusText = $"Projektprofil '{updated.Name}' gespeichert.";
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null)
            return;
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        _profileStore.Save(new ProjectProfileCollection(SelectedProfile?.Id, Profiles.ToArray()));
        StatusText = "Projektprofil gelöscht.";
    }

    private void BrowseProjectRoot()
    {
        var path = _folderSelection.SelectFolder("Projektwurzel auswählen", Editor.ProjectRoot);
        if (path is not null)
            Editor.ProjectRoot = path;
    }

    private void BrowseLibrary()
    {
        var path = _folderSelection.SelectFolder("ViCo-Bibliothek auswählen", Editor.VicoLibraryPath);
        if (path is not null)
            Editor.VicoLibraryPath = path;
    }

    private static string SelectXml(string current, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "XML-Dateien (*.xml)|*.xml|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            FileName = current,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : current;
    }

    private async Task RunQualityGateAsync()
    {
        await RunBusyAsync("Quality Gate wird ausgeführt …", async () =>
        {
            SaveProfile();
            var result = await _qualityGate.RunAsync(SelectedProfile!);
            _lastReport = result.Report;
            Findings.ReplaceWith(result.Report.Findings.Concat(result.Report.Evidence.SelectMany(item => item.Findings)));
            Evidence.ReplaceWith(result.Report.Evidence);
            Scenarios.ReplaceWith(result.Scenarios?.Scenarios ?? []);
            AdapterProbes.ReplaceWith(result.AdapterProbes);
            StatusText = $"Quality Gate: {result.Report.OverallStatus}; {result.Report.ErrorCount} Fehler, " +
                         $"{result.Report.WarningCount} Warnungen, {result.Report.GeneratedScenarioCount} Szenarien.";
        });
    }

    private async Task ReconcileSignalsAsync(bool persist)
    {
        await RunBusyAsync(persist ? "Signalregister wird aktualisiert …" : "Signalidentitäten werden analysiert …", () =>
        {
            var profile = Editor.Build();
            if (!File.Exists(profile.ContainerPath))
                throw new FileNotFoundException("Bitte zuerst ein vorhandenes ContainerFile auswählen.", profile.ContainerPath);
            var observations = _signalReader.Read(profile.ContainerPath);
            var result = _signalRegistry.Reconcile(observations, persist);
            SignalIdentities.ReplaceWith(result.Identities);
            Findings.ReplaceWith(result.Findings);
            var status = result.Conflicts > 0 ? QualityStatus.Failed : result.Added + result.Updated > 0 ? QualityStatus.Warning : QualityStatus.Passed;
            if (persist && result.Conflicts == 0)
            {
                QualityEvidenceStore.Instance.Upsert(new QualityEvidence(
                    "Signalregister", profile.ContainerPath, status,
                    $"{result.Added} neu, {result.Updated} geändert, {result.Unchanged} unverändert",
                    DateTimeOffset.UtcNow, result.Findings));
            }
            StatusText = $"Signalvergleich: {result.Added} neu, {result.Updated} geändert, " +
                         $"{result.Unchanged} unverändert, {result.Conflicts} Konflikte" +
                         (persist && result.Conflicts == 0 ? "; Register gespeichert." : "; Register nicht verändert.");
            return Task.CompletedTask;
        });
    }

    private void ExportReport()
    {
        if (_lastReport is null)
        {
            StatusText = "Zuerst das Quality Gate ausführen.";
            return;
        }
        var root = Directory.Exists(Editor.ProjectRoot)
            ? Path.Combine(Editor.ProjectRoot, "QualityReports")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VIBN_Tools", "QualityReports");
        var paths = _reportWriter.Write(_lastReport, root);
        StatusText = $"Quality-Gate-Berichte exportiert: {paths.JsonPath} und {paths.HtmlPath}";
    }

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = status;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText = $"Vorgang fehlgeschlagen: {exception.Message}";
            ApplicationLogService.Instance.Error("Project Quality", StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class ProjectProfileEditorVM : MvvmBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _customer = string.Empty;
    private string _projectRoot = string.Empty;
    private string _requirementsPath = string.Empty;
    private string _containerPath = string.Empty;
    private string _tiaVersion = string.Empty;
    private string _vicoLibraryPath = string.Empty;
    private string _rockwellStandard = "GCCS";
    private string _allowedContainerTypes = string.Empty;
    private string _namingRules = string.Empty;
    private string _signalAddressRanges = string.Empty;
    private bool _feeEnabled = true;
    private bool _emulate3DEnabled;
    private string _emulate3DInstallationPath = string.Empty;
    private string _emulate3DProjectPath = string.Empty;
    private bool _eksEnabled;
    private string _eksInstallationPath = string.Empty;
    private string _eksProjectPath = string.Empty;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Customer { get => _customer; set => Set(ref _customer, value); }
    public string ProjectRoot { get => _projectRoot; set => Set(ref _projectRoot, value); }
    public string RequirementsPath { get => _requirementsPath; set => Set(ref _requirementsPath, value); }
    public string ContainerPath { get => _containerPath; set => Set(ref _containerPath, value); }
    public string TiaVersion { get => _tiaVersion; set => Set(ref _tiaVersion, value); }
    public string VicoLibraryPath { get => _vicoLibraryPath; set => Set(ref _vicoLibraryPath, value); }
    public string RockwellStandard { get => _rockwellStandard; set => Set(ref _rockwellStandard, value); }
    public string AllowedContainerTypes { get => _allowedContainerTypes; set => Set(ref _allowedContainerTypes, value); }
    public string NamingRules { get => _namingRules; set => Set(ref _namingRules, value); }
    public string SignalAddressRanges { get => _signalAddressRanges; set => Set(ref _signalAddressRanges, value); }
    public bool FeeEnabled { get => _feeEnabled; set => Set(ref _feeEnabled, value); }
    public bool Emulate3DEnabled { get => _emulate3DEnabled; set => Set(ref _emulate3DEnabled, value); }
    public string Emulate3DInstallationPath { get => _emulate3DInstallationPath; set => Set(ref _emulate3DInstallationPath, value); }
    public string Emulate3DProjectPath { get => _emulate3DProjectPath; set => Set(ref _emulate3DProjectPath, value); }
    public bool EksEnabled { get => _eksEnabled; set => Set(ref _eksEnabled, value); }
    public string EksInstallationPath { get => _eksInstallationPath; set => Set(ref _eksInstallationPath, value); }
    public string EksProjectPath { get => _eksProjectPath; set => Set(ref _eksProjectPath, value); }

    public void Load(ProjectProfile profile)
    {
        _id = profile.Id;
        Name = profile.Name;
        Customer = profile.Customer;
        ProjectRoot = profile.ProjectRoot;
        RequirementsPath = profile.RequirementsPath;
        ContainerPath = profile.ContainerPath;
        TiaVersion = profile.TiaVersion;
        VicoLibraryPath = profile.VicoLibraryPath;
        RockwellStandard = profile.RockwellStandard;
        AllowedContainerTypes = string.Join(", ", profile.AllowedContainerTypes);
        NamingRules = string.Join("; ", profile.NamingRules.Select(pair => $"{pair.Key}={pair.Value}"));
        SignalAddressRanges = string.Join(", ", profile.SignalAddressRanges);
        var fee = Tool(profile, "FEE");
        FeeEnabled = fee?.Enabled ?? true;
        var emulate = Tool(profile, "Emulate3D");
        Emulate3DEnabled = emulate?.Enabled ?? false;
        Emulate3DInstallationPath = emulate?.InstallationPath ?? string.Empty;
        Emulate3DProjectPath = emulate?.ProjectPath ?? string.Empty;
        var eks = Tool(profile, "EKS");
        EksEnabled = eks?.Enabled ?? false;
        EksInstallationPath = eks?.InstallationPath ?? string.Empty;
        EksProjectPath = eks?.ProjectPath ?? string.Empty;
    }

    public ProjectProfile Build() => new(
        _id, Name.Trim(), Customer.Trim(), ProjectRoot.Trim(), RequirementsPath.Trim(), ContainerPath.Trim(),
        TiaVersion.Trim(), VicoLibraryPath.Trim(), RockwellStandard.Trim(), Split(AllowedContainerTypes),
        ParseRules(NamingRules), Split(SignalAddressRanges),
        [
            new SimulationToolConfiguration("FEE", FeeEnabled, string.Empty, string.Empty),
            new SimulationToolConfiguration("Emulate3D", Emulate3DEnabled, Emulate3DInstallationPath.Trim(), Emulate3DProjectPath.Trim()),
            new SimulationToolConfiguration("EKS", EksEnabled, EksInstallationPath.Trim(), EksProjectPath.Trim()),
        ]);

    private static SimulationToolConfiguration? Tool(ProjectProfile profile, string id) =>
        profile.SimulationTools.FirstOrDefault(tool => string.Equals(tool.PlatformId, id, StringComparison.OrdinalIgnoreCase));

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyDictionary<string, string> ParseRules(string value) => value
        .Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('=', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
        .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }
}

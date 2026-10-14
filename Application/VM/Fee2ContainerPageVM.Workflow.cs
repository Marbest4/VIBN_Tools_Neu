using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;
using VIBN_Tools.Core.Collections;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    private readonly FeeSignalWorkspaceService _signalsService = new();
    private FeeSignalWorkspaceSnapshot? _signalsSnapshot;
    private readonly Stack<FeeWorkspaceUndoState> _undo = new();
    private FeeWorkspaceUndoState? _lastWorkspaceState;
    private bool _restoringWorkspace;
    private int _workspaceEditDepth;
    public ObservableCollection<Fee2ContainerInterfaceVM> Interfaces { get; } = new();
    public ObservableCollection<Fee2ContainerRootSelectionVM> ObjectRoots { get; } = new();
    public ICommand ReadInterfacesCommand { get; private set; } = null!;
    public ICommand GenerateSignalContainersCommand { get; private set; } = null!;
    public ICommand AssignSceneObjectsCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand AddToSpecialDevicesCommand { get; private set; } = null!;
    public bool CanReadInterfaces => CanRefresh;
    public bool CanGenerateSignalContainers => !IsBusy && Connection.CanUseFeeFeatures && Connection.AreModelValidationObjectsCurrent && _signalsSnapshot is not null && Interfaces.Any(item => item.IsSelected) &&
        _signalsSnapshot.ConnectionRevision == Connection.ConnectionRevision;
    public bool CanAssignSceneObjects => !IsBusy && Connection.CanUseFeeFeatures && Connection.AreModelValidationObjectsCurrent && SelectedRoot is not null && ObjectRoots.Any(item => item.IsSelected) &&
        _signalsSnapshot?.ConnectionRevision == Connection.ConnectionRevision && GeneratedScopeMatchesSelection;
    private bool GeneratedScopeMatchesSelection => SelectedRoot?.Editor.LiveVariables.Select(item => item.InterfaceGuid).ToHashSet(StringComparer.OrdinalIgnoreCase)
        .SetEquals(Interfaces.Where(item => item.IsSelected && item.SignalCount > 0).Select(item => item.Guid)) == true;
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public string WorkflowText => "1. Interfaces einlesen und auswählen → Container aus aktiven Signalen erstellen. 2. FEE-Roots einlesen und markieren → FEE-Objekte separat zuordnen.";

    private void InitializeSignalWorkflow()
    {
        ReadInterfacesCommand = new AsyncRelayCommand(ReadInterfacesAsync, () => CanReadInterfaces);
        GenerateSignalContainersCommand = new AsyncRelayCommand(GenerateSignalContainersAsync, () => CanGenerateSignalContainers);
        AssignSceneObjectsCommand = new AsyncRelayCommand(AssignSceneObjectsAsync, () => CanAssignSceneObjects);
        UndoCommand = new RelayCommand(UndoWorkspace, () => CanUndo);
        AddToSpecialDevicesCommand = new RelayCommand<Fee2ContainerFoundContainerVM>(AddToSpecialDevices,
            item => item is not null && !IsBusy && !string.IsNullOrWhiteSpace(item.LogicDefinitionName));
    }

    private void NotifyWorkflow()
    {
        OnPropertyChanged(nameof(CanReadInterfaces)); OnPropertyChanged(nameof(CanGenerateSignalContainers));
        OnPropertyChanged(nameof(CanAssignSceneObjects)); OnPropertyChanged(nameof(CanUndo));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ReadInterfacesAsync()
    {
        if (!CanReadInterfaces) return;
        BeginOperation(); var token = _operationCancellation!.Token;
        try
        {
            _signalsSnapshot = null;
            ClearDiscoveredRoots(); ObjectRoots.Clear(); Interfaces.Clear(); _undo.Clear(); _lastWorkspaceState = null;
            var snapshot = await _signalsService.ReadInterfacesAsync(token);
            _signalsSnapshot = snapshot;
            foreach (var item in snapshot.Interfaces)
            {
                var choice = new Fee2ContainerInterfaceVM(item) { IsSelected = true };
                choice.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Fee2ContainerInterfaceVM.IsSelected))
                    { ObjectRoots.Clear(); NotifyWorkflow(); if (Roots.Count > 0) StatusText = "Interface-Auswahl geändert. Container aus Signalen erneut erstellen und FEE-Roots neu einlesen; vorhandene Bearbeitungen können rückgängig gemacht werden."; } };
                Interfaces.Add(choice);
            }
            _service.InterfaceNames = snapshot.Interfaces.ToDictionary(item => item.GuidString, item => item.Name, StringComparer.OrdinalIgnoreCase);
            StatusText = $"{Interfaces.Count} Interfaces und {snapshot.Variables.Count} aktuelle Signale gelesen. Auswahl prüfen und Container aus Signalen erstellen.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Interface-Einlesen abgebrochen."; }
        catch (Exception exception) { ReportOperationFailure("Interfaces konnten nicht gelesen werden", exception); }
        finally { EndOperation(); NotifyWorkflow(); }
    }

    private async Task GenerateSignalContainersAsync()
    {
        if (!CanGenerateSignalContainers || _signalsSnapshot is not { } snapshot) return;
        BeginOperation(); var operation = _operationCancellation!; var token = operation.Token;
        var previous = CaptureWorkspace();
        try
        {
            var chosen = Interfaces.Where(item => item.IsSelected).Select(item => item.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var variables = snapshot.Variables.Where(item => chosen.Contains(item.InterfaceGuid)).ToArray();
            var progress = new Progress<Fee2ContainerProgress>(item =>
            {
                if (IsBusy && ReferenceEquals(_operationCancellation, operation) && !operation.IsCancellationRequested)
                { ProgressValue = item.Percent; StatusText = item.Message; }
            });
            var routes = await _signalsService.ReadRoutesAsync(variables, snapshot.ConnectionRevision, token, progress);
            var requirements = UseRequirements && RequirementsProvider.RequirementsFile is { IsInitialized: true } source ? new XDocument(source.Document) : null;
            var root = await Task.Run(() => FeeSignalContainerBuilder.Build(variables, routes.Assignments, requirements, token), token);
            ReplaceWorkspace([new(root, true)]);
            PushUndo(previous); _lastWorkspaceState = CaptureWorkspace();
            Issues.ReplaceWith(routes.Issues); IsIssuesExpanded = Issues.Count > 0;
            StatusText = $"{root.ContainerCount} Container aus {root.UpdatedSignalCount} aktiv verknüpften Signalen erstellt. FEE-Objekte sind noch nicht zugeordnet.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Signal-Erkennung abgebrochen."; }
        catch (Exception exception) { ReportOperationFailure("Signal-Container konnten nicht erstellt werden", exception); }
        finally { EndOperation(); NotifyWorkflow(); }
    }

    private async Task AssignSceneObjectsAsync()
    {
        if (!CanAssignSceneObjects || SelectedRoot is not { } selected) return;
        BeginOperation(); var token = _operationCancellation!.Token;
        using var edit = BeginWorkspaceEdit();
        try
        {
            var workspace = selected.CreateEditedRoot(includeExcluded: true);
            var roots = ObjectRoots.Where(item => item.IsSelected).Select(item => item.Root).ToArray();
            var root = await Task.Run(() => FeeSignalObjectAssigner.Assign(workspace, roots, token), token);
            token.ThrowIfCancellationRequested();
            if (_signalsSnapshot?.ConnectionRevision != Connection.ConnectionRevision)
                throw new InvalidOperationException("Die FEE-Verbindung hat sich geändert. Interfaces und FEE-Roots erneut einlesen.");
            selected.ApplyRoot(root);
            RefreshSelectionDetails(selected); RefreshRequirementDefinitions();
            StatusText = $"FEE-Objekte aus {roots.Length} markierten Roots den bestehenden Signalcontainern zugeordnet. Uneindeutige Objekte bleiben zur Prüfung verfügbar.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Objektzuordnung abgebrochen; der Container-Arbeitsstand wurde nicht ersetzt."; }
        catch (Exception exception) { ReportOperationFailure("FEE-Objekte konnten nicht zugeordnet werden", exception); }
        finally { EndOperation(); NotifyWorkflow(); }
    }

    private void AddToSpecialDevices(Fee2ContainerFoundContainerVM? container)
    {
        if (container is null || SelectedRoot?.Editor is not { } editor || !editor.Containers.Contains(container)) return;
        using var edit = BeginWorkspaceEdit();
        container.IsManuallySpecialDevice = true;
        editor.RefreshLiveMetadata(); SpecialDevicesView.Refresh(); SelectedResultTabIndex = 2;
        StatusText = string.IsNullOrWhiteSpace(container.SpecialDeviceWarning) ? "Container zur SpecialDevices-Übersicht hinzugefügt." : container.SpecialDeviceWarning;
    }

    public IReadOnlyList<VIBN_Tools.SpecialDevices.Fee2SpecialDeviceRoot> CreateSpecialDeviceCandidates(Fee2ContainerFoundContainerVM? onlyContainer = null)
    {
        if (SelectedRoot?.Editor is not { } editor) return [];
        return editor.Containers.Where(container => container.IsSpecialDevice && (onlyContainer is null || container == onlyContainer)).Select(container =>
        {
            var objects = container.AssociatedObjectItems.Select(item => item.ObjectGuid).ToHashSet();
            var owner = container.AssociatedObjectItems.FirstOrDefault(item => item.Role is "Primary" or "SpecialDevice")?.ObjectGuid ??
                container.AssociatedObjectItems.FirstOrDefault()?.ObjectGuid ?? Guid.Empty;
            var signals = container.DataList.Where(item => item.IsIncluded && item.VariableGuid is not null).Select(item => item.VariableGuid!.Value).ToHashSet();
            var knownDevice = editor.SourceRoot.SpecialDevices?.FirstOrDefault(item => item.Guid == owner);
            return VIBN_Tools.SpecialDevices.FeeSpecialDeviceCandidateFactory.Create(container.Component, container.Type,
                container.LogicDefinitionName, container.ParentName, owner, editor.LiveVariables.Where(item => signals.Contains(item.VariableGuid)).ToArray(),
                editor.LiveAssignments.Where(item => signals.Contains(item.VariableGuid) && objects.Contains(item.TargetObjectGuid)).ToArray(),
                knownDevice?.SignalCoverage);
        }).ToArray();
    }

    private FeeWorkspaceUndoState CaptureWorkspace() => new(Roots.Select(item => new FeeWorkspaceUndoRoot(item.CreateEditedRoot(includeExcluded: true), item.IsSelected)).ToArray());
    private void ReplaceWorkspace(IReadOnlyList<FeeWorkspaceUndoRoot> items)
    {
        _restoringWorkspace = true;
        try
        {
            SelectedRoot = null;
            foreach (var root in Roots) root.PropertyChanged -= OnRootSelectionChanged;
            Roots.Clear();
            foreach (var item in items)
            {
                var choice = new Fee2ContainerRootSelectionVM(item.Root) { IsSelected = item.IsSelected };
                choice.PropertyChanged += OnRootSelectionChanged; Roots.Add(choice);
            }
            SelectedRoot = Roots.FirstOrDefault();
            RefreshSelectionDetails(SelectedRoot); RefreshRequirementDefinitions();
        }
        finally { _restoringWorkspace = false; }
    }

    private void TrackWorkspaceChange()
    {
        if (_restoringWorkspace || _workspaceEditDepth > 0) return;
        var current = CaptureWorkspace();
        if (_lastWorkspaceState is { } previous && previous.Fingerprint != current.Fingerprint)
        {
            PushUndo(previous);
        }
        _lastWorkspaceState = current; NotifyWorkflow();
    }
    private void PushUndo(FeeWorkspaceUndoState state)
    {
        if (_undo.Count >= 20) { var keep = _undo.Take(19).Reverse().ToArray(); _undo.Clear(); foreach (var item in keep) _undo.Push(item); }
        _undo.Push(state);
    }

    private IDisposable BeginWorkspaceEdit()
    {
        if (_workspaceEditDepth++ == 0) _lastWorkspaceState = CaptureWorkspace();
        return new WorkspaceEditScope(() => { if (--_workspaceEditDepth == 0) TrackWorkspaceChange(); });
    }
    private void UndoWorkspace()
    {
        if (!CanUndo) return;
        var state = _undo.Pop(); ReplaceWorkspace(state.Roots); _lastWorkspaceState = CaptureWorkspace();
        StatusText = "Letzte Änderung am Container-Arbeitsstand rückgängig gemacht. FEE wurde nicht verändert."; NotifyWorkflow();
    }
    private sealed class WorkspaceEditScope(Action end) : IDisposable { public void Dispose() => end(); }
    private sealed record FeeWorkspaceUndoRoot(Fee2ContainerRoot Root, bool IsSelected);
    private sealed record FeeWorkspaceUndoState(IReadOnlyList<FeeWorkspaceUndoRoot> Roots)
    {
        public string Fingerprint => string.Join("\n", Roots.Select(item =>
        {
            var document = item.Root.Provenance is null ? new XDocument() : new XDocument(item.Root.Provenance.ContainerDocument);
            document.Root?.Attribute("createdAt")?.Remove();
            return item.IsSelected + ":" + document.ToString(SaveOptions.DisableFormatting);
        }));
    }
}

public sealed class Fee2ContainerInterfaceVM(VisualFeeInterface model) : NotifyBase
{
    private bool _isSelected;
    public string Guid => model.GuidString;
    public string Name => model.Name;
    public int SignalCount => model.SignalCount;
    public bool IsSelected { get => _isSelected; set => SetPropertyChange(ref _isSelected, value); }
}

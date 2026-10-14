using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.Core.Collections;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    private Fee2ContainerRootEditor? _detailEditor;
    private bool _showLinkedUnassignedSignals;
    private string? _linkedSignalsContainerId;
    private int _selectedResultTabIndex;
    public int SelectedResultTabIndex
    {
        get => _selectedResultTabIndex;
        set
        {
            if (_selectedResultTabIndex == value) return;
            _selectedResultTabIndex = value;
            OnPropertyChanged();
        }
    }
    public ObservableCollection<Fee2ContainerObjectTypeFilterVM> ObjectTypeFilters { get; } = new();
    public ObservableCollection<Fee2ContainerLinkedSignalVM> LinkedUnassignedSignals { get; } = new RangeObservableCollection<Fee2ContainerLinkedSignalVM>();
    public ICollectionView SpecialDevicesView { get; private set; } = null!;
    public ICommand ShowLinkedSignalsCommand { get; private set; } = null!;
    public ICommand GroupContainersCommand { get; private set; } = null!;
    public ContainerGenerationSettings GroupingSettings { get; } = new() { GroupByComponent = true, GroupByType = true };
    public bool ShowLinkedUnassignedSignals
    {
        get => _showLinkedUnassignedSignals;
        set
        {
            if (_showLinkedUnassignedSignals == value) return;
            _showLinkedUnassignedSignals = value;
            OnPropertyChanged();
        }
    }
    public string LinkedSignalsSummary => $"{LinkedUnassignedSignals.Count} verknüpfte Signale ohne vollständige Containerzuordnung";
    public int LinkedUnassignedSignalCount => CountLinkedUnassignedSignals(SelectedRoot?.Editor, null);

    private void InitializeLiveDetails()
    {
        ShowLinkedSignalsCommand = new RelayCommand<Fee2ContainerFoundContainerVM>(container =>
        {
            _linkedSignalsContainerId = container?.Id;
            RefreshLinkedSignals();
            ShowLinkedUnassignedSignals = true;
            SelectedResultTabIndex = 3;
        }, _ => SelectedRoot is not null && !IsBusy);
        GroupContainersCommand = new RelayCommand(() =>
        {
            if (SelectedRoot is not { } root) return;
            var edit = new Fee2ContainerEditVM(root, SupportedContainerTypes);
            edit.Settings.GroupByComponent = GroupingSettings.GroupByComponent;
            edit.Settings.GroupByType = GroupingSettings.GroupByType;
            edit.Settings.GroupById = GroupingSettings.GroupById;
            edit.Settings.GroupByAddress = GroupingSettings.GroupByAddress;
            edit.Settings.RegexId = GroupingSettings.RegexId;
            edit.Settings.RegexAddress = GroupingSettings.RegexAddress;
            edit.GroupCommand.Execute(null);
            root.ApplyEditor(edit.CreateResult());
            RefreshSelectionDetails(root);
            StatusText = edit.Status;
        }, () => SelectedRoot is not null && !IsBusy);
    }

    private void RefreshLiveDetailViews()
    {
        if (_detailEditor is not null) _detailEditor.ContentChanged -= OnEditorContentChanged;
        _detailEditor = SelectedRoot?.Editor;
        if (_detailEditor is not null) _detailEditor.ContentChanged += OnEditorContentChanged;
        var previous = ObjectTypeFilters.ToDictionary(item => item.FeeType, item => item.IsVisible, StringComparer.OrdinalIgnoreCase);
        foreach (var item in ObjectTypeFilters) item.PropertyChanged -= OnObjectTypeFilterChanged;
        ObjectTypeFilters.Clear();
        foreach (var type in NonContainerObjects.Select(item => item.FeeType)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
        {
            var filter = new Fee2ContainerObjectTypeFilterVM(type, previous.GetValueOrDefault(type, true));
            filter.PropertyChanged += OnObjectTypeFilterChanged;
            ObjectTypeFilters.Add(filter);
        }
        _linkedSignalsContainerId = null;
        RefreshLinkedSignals();
        OnPropertyChanged(nameof(SpecialDevicesView));
    }

    private void OnObjectTypeFilterChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Fee2ContainerObjectTypeFilterVM.IsVisible)) NonContainerObjectsView.Refresh();
    }
    private void OnEditorContentChanged(object? sender, EventArgs args)
    {
        RefreshLinkedSignals();
        SelectedRoot?.NotifyCounts();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private static int CountLinkedUnassignedSignals(Fee2ContainerRootEditor? editor, string? containerId)
    {
        if (editor is null) return 0;
        var assigned = editor.Signals.Where(item => item.IsAssigned && item.VariableGuid is not null)
            .Select(item => item.VariableGuid!.Value).ToHashSet();
        var objects = containerId is null ? null : editor.Containers.FirstOrDefault(item => item.Id == containerId)?
            .AssociatedObjectItems.Select(item => item.ObjectGuid).ToHashSet();
        return editor.LiveAssignments.Where(item => !assigned.Contains(item.VariableGuid) &&
            (containerId is null || objects?.Contains(item.TargetObjectGuid) == true)).Select(item => item.VariableGuid).Distinct().Count();
    }

    private void RefreshLinkedSignals()
    {
        var editor = SelectedRoot?.Editor;
        var rows = new List<Fee2ContainerLinkedSignalVM>();
        if (editor is not null)
        {
            var assigned = editor.Signals.Where(item => item.IsAssigned && item.VariableGuid is not null)
                .Select(item => item.VariableGuid!.Value).ToHashSet();
            var details = editor.ObjectDetails.ToDictionary(item => item.Guid);
            var variables = editor.LiveVariables.ToDictionary(item => item.VariableGuid);
            var objects = _linkedSignalsContainerId is null ? null : editor.Containers.FirstOrDefault(item => item.Id == _linkedSignalsContainerId)?
                .AssociatedObjectItems.Select(item => item.ObjectGuid).ToHashSet();
            foreach (var group in editor.LiveAssignments.Where(item => !assigned.Contains(item.VariableGuid) &&
                (_linkedSignalsContainerId is null || objects?.Contains(item.TargetObjectGuid) == true)).GroupBy(item => item.VariableGuid))
            {
                if (!variables.TryGetValue(group.Key, out var variable)) continue;
                rows.Add(new Fee2ContainerLinkedSignalVM(group.Key, variable.Signal,
                    string.IsNullOrWhiteSpace(variable.Path) ? variable.Address : variable.Path, variable.DataType,
                    string.Join("; ", group.Select(item => $"{details.GetValueOrDefault(item.TargetObjectGuid)?.Name ?? item.TargetObjectGuid.ToString("D")}/{item.TargetSlot}").Distinct())));
            }
        }
        LinkedUnassignedSignals.ReplaceWith(rows.OrderBy(item => item.Signal, StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(LinkedUnassignedSignalCount));
        OnPropertyChanged(nameof(LinkedSignalsSummary));
    }
}

public sealed class Fee2ContainerObjectTypeFilterVM(string feeType, bool isVisible) : NotifyBase
{
    private bool _isVisible = isVisible;
    public string FeeType { get; } = feeType;
    public bool IsVisible { get => _isVisible; set => SetPropertyChange(ref _isVisible, value); }
}

public sealed record Fee2ContainerLinkedSignalVM(Guid Guid, string Signal, string Address, string DataType, string ConnectionText);

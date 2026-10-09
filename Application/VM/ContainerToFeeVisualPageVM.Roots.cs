using System.Collections.ObjectModel;
using System.ComponentModel;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.Application.VM;

public sealed partial class ContainerToFeeVisualPageVM
{
    public ObservableCollection<ContainerToFeeVisualFeeRootVM> AvailableFeeRoots { get; } = new();
    public string SelectedFeeRootsSummary => AvailableFeeRoots.Count == 0 ? "FEE-Roots nach Aktualisierung auswählen"
        : $"{AvailableFeeRoots.Count(root => root.IsSelected)} von {AvailableFeeRoots.Count} FEE-Roots ausgewählt";

    private void RefreshFeeRootProjection()
    {
        var previous = AvailableFeeRoots.ToDictionary(root => root.GuidString, root => root.IsSelected,
            StringComparer.OrdinalIgnoreCase);
        foreach (var root in AvailableFeeRoots) root.PropertyChanged -= OnFeeRootSelectionChanged;
        AvailableFeeRoots.Clear();
        var roots = _planService.DiscoveredTopLevelBasicFrames.ToDictionary(item => item.Key.ToString("D"), item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in _planService.DiscoveredFeeObjects)
            roots.TryAdd(item.RootGuidString, item.RootName);
        foreach (var item in roots.OrderBy(root => root.Value, StringComparer.OrdinalIgnoreCase))
        {
            var root = new ContainerToFeeVisualFeeRootVM(item.Key, item.Value,
                !previous.TryGetValue(item.Key, out var wasSelected) || wasSelected);
            root.PropertyChanged += OnFeeRootSelectionChanged;
            AvailableFeeRoots.Add(root);
        }
        CommitFeeRootSelection();
    }

    private void OnFeeRootSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ContainerToFeeVisualFeeRootVM.IsSelected))
        {
            CommitFeeRootSelection();
            QueueScopeLinkRefresh();
        }
    }

    private void CommitFeeRootSelection()
    {
        _planService.SetSimObjectRoots(AvailableFeeRoots.Where(root => root.IsSelected).Select(root => root.GuidString));
        OnPropertyChanged(nameof(SelectedFeeRootsSummary));
        FeeObjectsView.Refresh();
        ApplyDiscoveredContainerObjectStates(_planService.DiscoveredFeeContainerObjects);
        ApplyDiscoveredSimObjectStates();
        ApplyDiscoveredSignalStates(_planService.DiscoveredFeeSignals);
        RefreshFeeObjectProjection(_planService.DiscoveredFeeObjects);
        RefreshFeeSignalProjection(_planService.DiscoveredFeeSignals);
    }

    private void RefreshCompletedContainerSelection()
    {
        var verified = _planService.FindFullyVerifiedContainerIds();
        _verifiedContainerIds.Clear();
        _verifiedContainerIds.UnionWith(verified);
        _planService.DeselectVerifiedContainers(verified);
        ApplyVerifiedContainerStates(verified);
    }
}

public sealed class ContainerToFeeVisualFeeRootVM(string guidString, string name, bool isSelected) : MvvmBase
{
    private bool _isSelected = isSelected;
    public string GuidString { get; } = guidString;
    public string Name { get; } = name;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }
}

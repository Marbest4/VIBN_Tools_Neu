using VIBN_Tools.Application.Behaviors;
using VIBN_Tools.Core.Collections;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    private static object[] SignalDragItems(object source) => source is IReadOnlyList<object> batch ? batch.ToArray() : [source];
    private bool CanAssignSignals(object source) => SignalDragItems(source) is { Length: > 0 } items &&
        items.All(item => item is Fee2ContainerFoundSignalVM or Fee2ContainerLinkedSignalVM);

    private bool TryAssignDraggedSignals(ContainerToFeeVisualDropRequest request)
    {
        if (request.Target is not Fee2ContainerFoundContainerVM container || !CanAssignSignals(request.Source) ||
            SelectedRoot?.Editor is not { } editor || !editor.Containers.Contains(container)) return false;
        using var change = BeginWorkspaceEdit();
        foreach (var source in SignalDragItems(request.Source))
        {
            Fee2ContainerFoundSignalVM signal;
            if (source is Fee2ContainerFoundSignalVM existing)
            {
                if (!editor.Signals.Contains(existing)) continue;
                if (existing.ContainerId == container.Id) { existing.IsIncluded = true; continue; }
                editor.Signals.Remove(existing);
                signal = new(container.Id, container.Component, container.Type, existing.Signal, existing.Slot,
                    existing.Address, existing.DataType, existing.SignalId, existing.Note, existing.VariableGuid, existing.InterfaceGuid, existing.InterfaceName) { FeeType = existing.FeeType };
            }
            else
            {
                var linked = (Fee2ContainerLinkedSignalVM)source;
                var variable = editor.LiveVariables.FirstOrDefault(item => item.VariableGuid == linked.Guid);
                if (variable is null) continue;
                var objectGuids = container.AssociatedObjectItems.Select(item => item.ObjectGuid).ToHashSet();
                var route = editor.LiveAssignments.FirstOrDefault(item => item.VariableGuid == linked.Guid && objectGuids.Contains(item.TargetObjectGuid)) ??
                    editor.LiveAssignments.FirstOrDefault(item => item.VariableGuid == linked.Guid);
                var slot = route?.TargetSlot ?? "";
                if (editor.Signals.Any(item => item.ContainerId == container.Id && item.VariableGuid == linked.Guid && item.Slot == slot && item.IsIncluded)) continue;
                var address = container.IsSpecialDevice && !string.IsNullOrWhiteSpace(variable.Address) ? variable.Address : linked.Address;
                signal = new(container.Id, container.Component, container.Type, variable.Signal, slot, address,
                    variable.DataType, variable.Comment, "Manuell im ContainerFile zugeordnet; FEE-Verknüpfungen bleiben unverändert.", linked.Guid, variable.InterfaceGuid, variable.InterfaceName);
            }
            editor.AddSignal(signal);
        }
        editor.RefreshLiveMetadata();
        RefreshSignalRows();
        SelectedFoundContainer = container;
        StatusText = "Signalzuordnung im ContainerFile geändert. Slot und Containertyp vor Export prüfen.";
        return true;
    }

    private void RefreshSignalRows()
    {
        FoundSignals.ReplaceWith(SelectedRoot?.Editor.Signals ?? []);
        FoundSignalsView.Refresh();
        RefreshLinkedSignals();
    }
}

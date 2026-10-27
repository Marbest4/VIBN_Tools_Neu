using System.Collections.Specialized;
using System.Reflection;
using System.Windows.Data;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyContainerDropViewUpdates()
    {
        var vm = new ContainerGenerationPageVM();
        vm.Settings.AutoSaveEnabled = false;
        var target = new ContainerData { Component = "Drop target" };
        target.DataList.Add(new ContainerEntry { SignalId = "existing", Signal = "Existing", Slot = "Input" });
        vm.ContainerList.Add(target);
        var incoming = new ContainerEntry { SignalId = "incoming", Signal = "Incoming", Slot = "Input" };
        vm.UnassignedEntries.Add(incoming);
        var containerView = CollectionViewSource.GetDefaultView(vm.ContainerList);
        var views = new[] { containerView, CollectionViewSource.GetDefaultView(vm.UnassignedEntries),
            CollectionViewSource.GetDefaultView(vm.FilteredEntries) };
        var resets = 0;
        foreach (var view in views)
            view.CollectionChanged += (_, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        var run = typeof(ContainerGenerationPageVM).GetMethod("RunWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Apply(string description, Action action) => (bool)run.Invoke(vm, [description, action, null])!;
        if (!Apply("Drop", () => GenerationWorkspaceEditor.MoveToContainerBatch([incoming], target,
                vm.ContainerList, vm.UnassignedEntries, vm.FilteredEntries)) || resets != 0 ||
            target.DataList.Count != 2 || vm.UnassignedEntries.Count != 0)
            throw new InvalidOperationException("Dropping into an unfiltered table rebuilt the complete view or failed to move the signal.");

        // Active search/review filters must still reflect changes after a drop.
        vm.SearchTextContainer = "Drop target";
        typeof(ContainerGenerationPageVM).GetMethod("FilterContainerGrid", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);
        if (!containerView.Contains(target) || !Apply("Rename target", () => target.Component = "Other") ||
            containerView.Contains(target))
            throw new InvalidOperationException("Avoiding unnecessary view resets left an active container filter stale.");
        vm.OnViewUnloaded();
    }
}

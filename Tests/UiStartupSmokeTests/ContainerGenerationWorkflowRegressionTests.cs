using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyContainerGenerationWorkflowRegressions()
    {
        VerifyContainerDropViewUpdates();
        var page = new ContainerGenerationPage();
        var vm = (ContainerGenerationPageVM)page.DataContext;
        vm.Settings.AutoSaveEnabled = false;
        var container = new ContainerData { Component = "Change", ManuallyChecked = true };
        container.DataList.Add(new ContainerEntry
        { Signal = "Changed", Slot = "PLC_IN", Address = "%I0.0", ReviewState = ContainerEntryReviewState.SourceChanged });
        vm.ContainerList.Add(container);
        var window = new Window { Content = page, Width = 1400, Height = 800 };
        try
        {
            window.Show(); PumpDispatcher(TimeSpan.FromMilliseconds(100));
            var grid = FindVisualChildren<DataGrid>(page).Single(item => item.Name == "ContainerListDataGrid");
            var column = grid.Columns.Single(item => Equals(item.Header, "Reimport-Änderung"));
            if (vm.IsReimportDetailsVisible || column.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Reimport details did not start hidden.");
            AssertContainerColor(grid, container, "#FFFFF3CD");
            vm.IsReimportDetailsVisible = true; PumpDispatcher(TimeSpan.FromMilliseconds(40));
            if (column.Visibility != Visibility.Visible) throw new InvalidOperationException("Details checkbox did not reveal its column.");
            vm.ConfirmAppliedChanges.Execute(null); PumpDispatcher(TimeSpan.FromMilliseconds(40));
            if (vm.HasUnconfirmedChanges || container.HasDetectedChanges || !container.DataList.Single().IsChangeAcknowledged)
                throw new InvalidOperationException("The confirm command did not acknowledge the applied change.");
            AssertContainerColor(grid, container, "#FFFFFFFF");
            vm.UndoLastAction.Execute(null); PumpDispatcher(TimeSpan.FromMilliseconds(40));
            if (!vm.HasUnconfirmedChanges) throw new InvalidOperationException("Undo did not restore a confirmation marker.");
            vm.RedoLastAction.Execute(null); PumpDispatcher(TimeSpan.FromMilliseconds(40));
            if (vm.HasUnconfirmedChanges) throw new InvalidOperationException("Redo did not acknowledge the marker again.");
            container = vm.ContainerList.Single();
            var entry = container.DataList.Single();
            entry.Address = "%Q0.0"; vm.ConfirmAppliedChanges.Execute(null);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            AssertContainerColor(grid, container, "#FFFFE0B2");
            entry.ValidationError = "Invalid slot"; PumpDispatcher(TimeSpan.FromMilliseconds(40));
            AssertContainerColor(grid, container, "#FFFFE1E1");

            var checkpoint = container.Component;
            var run = typeof(ContainerGenerationPageVM).GetMethod("RunWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var success = (bool)run.Invoke(vm, ["Injected failure", (Action)(() =>
            { vm.ContainerList.Single().Component = "Partial"; throw new InvalidOperationException("Injected operation error"); }), null])!;
            if (success || vm.ContainerList.Single().Component != checkpoint)
                throw new InvalidOperationException("A recoverable action failure did not restore the workspace.");
            var parent = typeof(ContainerGenerationPageVM).GetMethod("FindAncestor", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(DataGrid));
            _ = parent.Invoke(null, [new Run("inline")]);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var factory = typeof(ContainerGenerationPageVM).GetMethod("GuardedAsyncCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var command = (System.Windows.Input.ICommand)factory.Invoke(vm, [(Func<object, Task>)(_ =>
            { calls++; return pending.Task; }), "Injected async failure"])!;
            command.Execute(null); command.Execute(null);
            if (vm.CanEditWorkspace || vm.ConfirmAppliedChanges.CanExecute(null) || calls != 1)
                throw new InvalidOperationException("Concurrent generator commands were not blocked.");
            pending.SetException(new System.IO.IOException("Injected async error"));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!vm.CanEditWorkspace && DateTime.UtcNow < deadline) PumpDispatcher(TimeSpan.FromMilliseconds(20));
            if (!vm.CanEditWorkspace) throw new InvalidOperationException("An async failure left the workspace disabled.");
        }
        finally { window.Close(); vm.OnViewUnloaded(); }
    }

    private static void AssertContainerColor(DataGrid grid, ContainerData container, string expected)
    {
        grid.UpdateLayout();
        var row = grid.ItemContainerGenerator.ContainerFromItem(container) as DataGridRow;
        if (row?.Background is not SolidColorBrush brush || brush.Color.ToString() != expected)
            throw new InvalidOperationException($"Container color was {row?.Background}, expected {expected}.");
    }
}

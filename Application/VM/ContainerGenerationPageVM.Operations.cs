using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.Application.VM;

public partial class ContainerGenerationPageVM
{
    private bool _isWorkspaceOperationBusy;
    private bool _isWorkspaceBatch;
    public bool CanEditWorkspace => !_isWorkspaceOperationBusy;
    private bool _isReimportDetailsVisible;
    public bool IsReimportDetailsVisible
    {
        get => _isReimportDetailsVisible;
        set { if (_isReimportDetailsVisible == value) return; _isReimportDetailsVisible = value; OnPropertyChanged(); }
    }
    public bool HasUnconfirmedChanges => ContainerList.SelectMany(container => container.DataList)
        .Concat(UnassignedEntries).Concat(FilteredEntries).Any(entry => entry.HasUnconfirmedChange);
    public ICommand ConfirmAppliedChanges => GuardedCommand(ConfirmChanges);

    private void ReplacePendingReimportChanges(IEnumerable<ReimportDifference> differences)
    {
        foreach (var difference in PendingReimportChanges) difference.PropertyChanged -= PendingReimportChange_PropertyChanged;
        PendingReimportChanges.ReplaceRange(differences);
        foreach (var difference in PendingReimportChanges) difference.PropertyChanged += PendingReimportChange_PropertyChanged;
    }

    private ICommand GuardedCommand(Action<object> execute, [CallerMemberName] string operation = "") =>
        new OperationCommand(this, parameter => { ExecuteGuarded(operation, () => execute(parameter)); return Task.CompletedTask; }, false);
    private ICommand GuardedCommand(Action execute, [CallerMemberName] string operation = "") =>
        GuardedCommand(_ => execute(), operation);
    private ICommand GuardedAsyncCommand(Func<object, Task> execute, [CallerMemberName] string operation = "") =>
        new OperationCommand(this, parameter => ExecuteGuardedAsync(operation, () => execute(parameter)), true);

    private bool ExecuteGuarded(string operation, Action action)
    {
        try { action(); return true; }
        catch (OperationCanceledException) { StatusText = "Der Vorgang wurde abgebrochen."; }
        catch (Exception exception) when (ContainerGenerationExceptionPolicy.IsRecoverable(exception))
        { ReportOperationFailure(operation, exception); }
        return false;
    }
    private async Task ExecuteGuardedAsync(string operation, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { StatusText = "Der Vorgang wurde abgebrochen."; }
        catch (Exception exception) when (ContainerGenerationExceptionPolicy.IsRecoverable(exception))
        { ReportOperationFailure(operation, exception); }
    }
    private void ReportOperationFailure(string operation, Exception exception)
    {
        Logger.Error(exception, "ContainerGeneration operation {Operation} failed.", operation);
        StatusText = $"Der Vorgang konnte nicht abgeschlossen werden: {exception.Message}. Details stehen im Protokoll.";
    }
    private sealed class OperationCommand(ContainerGenerationPageVM owner, Func<object, Task> execute, bool asynchronous) : ICommand
    {
        public bool CanExecute(object parameter) => owner.CanEditWorkspace;
        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            if (asynchronous)
            {
                owner._isWorkspaceOperationBusy = true;
                owner.OnPropertyChanged(nameof(CanEditWorkspace));
                CommandManager.InvalidateRequerySuggested();
            }
            try { await execute(parameter); }
            finally
            {
                if (asynchronous)
                {
                    owner._isWorkspaceOperationBusy = false;
                    owner.IsBusyGenerateContainers = false;
                    owner.OnPropertyChanged(nameof(CanEditWorkspace));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    private void ConfirmChanges()
    {
        if (!HasUnconfirmedChanges) return;
        if (!RunWorkspaceAction("Übernommene Änderungen bestätigen", () =>
        {
            foreach (var entry in ContainerList.SelectMany(container => container.DataList)
                         .Concat(UnassignedEntries).Concat(FilteredEntries).Where(entry => entry.HasUnconfirmedChange))
                entry.IsChangeAcknowledged = true;
        })) return;
        StatusText = "Änderungen bestätigt. Fehler und Warnungen bleiben markiert.";
    }

    public void OnViewUnloaded()
    {
        _autoSaveTimer.Stop();
        _debounceTimerContainerData.Stop();
        _debounceTimerUnassignedData.Stop();
        _debounceTimerFilteredData.Stop();
        var target = NLog.LogManager.Configuration?.FindTargetByName<VIBN_Tools.ContainerGeneration.Utils.CustomLoggerTarget>("CustomLog");
        if (target is not null) target.LogReceived -= CustomTarget_LogReceived;
    }

    private static DependencyObject? GetUiParent(DependencyObject current) => current switch
    {
        ContentElement content => ContentOperations.GetParent(content) ??
            (content as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(content),
        Visual or Visual3D => VisualTreeHelper.GetParent(current),
        _ => LogicalTreeHelper.GetParent(current)
    };

    private DataGrid? _dragGrid;
    private Point _dragStart;
    private object? _dragItem;
    public ICommand DataGridMouseDown => GuardedCommand(parameter =>
    {
        _dragGrid = null; _dragItem = null;
        if (parameter is not MouseButtonEventArgs args || args.ChangedButton != MouseButton.Left ||
            args.OriginalSource is not DependencyObject source || FindAncestor<DataGrid>(source) is not { } grid) return;
        var entry = FindDataContext<ContainerEntry>(source);
        if (entry is not null && (FindAncestor<ListBox>(source)?.Tag as string == "SignalIdDragSource" ||
                                  FindAncestor<DataGridRow>(source)?.Item is ContainerEntry))
            _dragItem = entry;
        else if (FindAncestor<TextBox>(source) is null && FindAncestor<ComboBox>(source) is null &&
                 FindAncestor<Button>(source) is null && FindAncestor<DataGridRow>(source)?.Item is ContainerData container)
            _dragItem = container;
        if (_dragItem is null) return;
        _dragGrid = grid; _dragStart = args.GetPosition(grid);
    });
}

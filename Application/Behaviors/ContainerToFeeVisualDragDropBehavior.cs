using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Describes one drag-and-drop request without coupling the view behavior to
/// the Container2FEE plan model.  The target view model remains responsible
/// for type validation and for recording undo/redo history.
/// </summary>
public sealed record ContainerToFeeVisualDropRequest(object Source, object Target);

/// <summary>
/// Small attached WPF behavior used by the visual Container2FEE workspace.
/// It deliberately transports view-model objects only; it never mutates the
/// legacy container model itself.
/// </summary>
public static class ContainerToFeeVisualDragDropBehavior
{
    private const string DataFormat = "VIBN_Tools.ContainerToFeeVisual.Item";
    private static readonly ConditionalWeakTable<UIElement, DragSourceState> DragSourceStates = new();

    public static readonly DependencyProperty IsDragSourceProperty =
        DependencyProperty.RegisterAttached(
            "IsDragSource",
            typeof(bool),
            typeof(ContainerToFeeVisualDragDropBehavior),
            new PropertyMetadata(false, OnIsDragSourceChanged));

    public static readonly DependencyProperty IsDropTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsDropTarget",
            typeof(bool),
            typeof(ContainerToFeeVisualDragDropBehavior),
            new PropertyMetadata(false, OnIsDropTargetChanged));

    public static readonly DependencyProperty DropCommandProperty =
        DependencyProperty.RegisterAttached(
            "DropCommand",
            typeof(ICommand),
            typeof(ContainerToFeeVisualDragDropBehavior));

    public static readonly DependencyProperty DropTargetProperty =
        DependencyProperty.RegisterAttached(
            "DropTarget",
            typeof(object),
            typeof(ContainerToFeeVisualDragDropBehavior));

    public static bool GetIsDragSource(DependencyObject element) =>
        (bool)element.GetValue(IsDragSourceProperty);

    public static void SetIsDragSource(DependencyObject element, bool value) =>
        element.SetValue(IsDragSourceProperty, value);

    public static bool GetIsDropTarget(DependencyObject element) =>
        (bool)element.GetValue(IsDropTargetProperty);

    public static void SetIsDropTarget(DependencyObject element, bool value) =>
        element.SetValue(IsDropTargetProperty, value);

    public static ICommand? GetDropCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(DropCommandProperty);

    public static void SetDropCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(DropCommandProperty, value);

    public static object? GetDropTarget(DependencyObject element) =>
        element.GetValue(DropTargetProperty);

    public static void SetDropTarget(DependencyObject element, object? value) =>
        element.SetValue(DropTargetProperty, value);

    private static void OnIsDragSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
            return;

        if ((bool)args.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            element.PreviewMouseMove += OnPreviewMouseMove;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            element.PreviewMouseMove -= OnPreviewMouseMove;
        }
    }

    private static void OnIsDropTargetChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element)
            return;

        bool enabled = (bool)args.NewValue;
        element.AllowDrop = enabled;
        if (enabled)
        {
            element.DragOver += OnDragOver;
            element.Drop += OnDrop;
        }
        else
        {
            element.DragOver -= OnDragOver;
            element.Drop -= OnDrop;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not UIElement element)
            return;

        var state = DragSourceStates.GetOrCreateValue(element);
        state.Start = args.GetPosition(null);
        // Capture the data item at mouse-down time. A synchronized selection
        // can rebuild or scroll a virtualized list before the drag threshold is
        // crossed; resolving OriginalSource later could therefore pick a
        // recycled container and link the wrong FEE object.
        state.Source = ResolveItem(element, args.OriginalSource as DependencyObject);
        state.Owner = FindAncestor<ListBox>(element) as ItemsControl ?? FindAncestor<DataGrid>(element);
        // The dedicated drag handle keeps an existing batch selected. WPF
        // otherwise reduces DataGrid.SelectedItems to the clicked row before
        // the drag threshold is crossed.
        if (state.Source is object[] && state.Owner is DataGrid grid && Keyboard.Modifiers == ModifierKeys.None)
        {
            grid.Focus();
            args.Handled = true;
        }
        CaptureScrollOffset(state);
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed ||
            sender is not UIElement element ||
            !DragSourceStates.TryGetValue(element, out var state))
            return;

        Point current = args.GetPosition(null);
        if (Math.Abs(current.X - state.Start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - state.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Consume the captured payload once. Do not derive it from the current
        // pointer visual: virtualization may already have reused that visual
        // for a different row after selection synchronization.
        object? source = state.Source;
        state.Source = null;
        if (source is null)
            return;

        var data = new DataObject(DataFormat, source);
        try
        {
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            RestoreScrollOffset(state);
        }
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        args.Effects = args.Data.GetDataPresent(DataFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        args.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs args)
    {
        if (sender is not DependencyObject element ||
            args.Data.GetData(DataFormat) is not object source)
            return;

        object? target = GetDropTarget(element) ?? (element as FrameworkElement)?.DataContext;
        ICommand? command = GetDropCommand(element);
        if (target is null || command is null)
            return;

        var request = new ContainerToFeeVisualDropRequest(source, target);
        if (command.CanExecute(request))
            command.Execute(request);

        args.Handled = true;
    }

    private static object? ResolveItem(UIElement sourceElement, DependencyObject? originalSource)
    {
        object? item = null;
        var sourceDataContext = (sourceElement as FrameworkElement)?.DataContext;
        DependencyObject? current = originalSource;
        while (current is not null && current != sourceElement)
        {
            var dataContext = current switch
            {
                FrameworkElement element => element.DataContext,
                FrameworkContentElement content => content.DataContext,
                _ => null
            };
            if (dataContext is not null && dataContext != sourceDataContext)
            {
                item = dataContext;
                break;
            }

            current = GetSafeParent(current);
        }

        item ??= sourceElement is ListBox sourceListBox
            ? sourceListBox.SelectedItem
            : sourceDataContext;
        if (item is null)
            return null;

        var listBox = FindAncestor<ListBox>(sourceElement);
        if (listBox?.SelectionMode is SelectionMode.Multiple or SelectionMode.Extended &&
            listBox.SelectedItems.Count > 1 && listBox.SelectedItems.Contains(item))
        {
            return listBox.SelectedItems.Cast<object>().ToArray();
        }

        var grid = FindAncestor<DataGrid>(sourceElement);
        if (grid?.SelectionMode == DataGridSelectionMode.Extended &&
            grid.SelectedItems.Count > 1 && grid.SelectedItems.Contains(item))
            return grid.SelectedItems.Cast<object>().ToArray();

        return item;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var current = start; current is not null;
             current = GetSafeParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    internal static DependencyObject? GetSafeParent(DependencyObject current)
    {
        if (current is ContentElement content)
            return ContentOperations.GetParent(content) ??
                (content as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(content);
        if (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
            return System.Windows.Media.VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private static void CaptureScrollOffset(DragSourceState state)
    {
        if (state.Owner is null || FindVisualChild<ScrollViewer>(state.Owner) is not { } viewer)
            return;

        state.VerticalOffset = viewer.VerticalOffset;
        state.HorizontalOffset = viewer.HorizontalOffset;
        state.HasScrollOffset = true;
    }

    private static void RestoreScrollOffset(DragSourceState state)
    {
        if (!state.HasScrollOffset || state.Owner is null)
            return;

        var owner = state.Owner;
        var verticalOffset = state.VerticalOffset;
        var horizontalOffset = state.HorizontalOffset;
        state.HasScrollOffset = false;
        _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (FindVisualChild<ScrollViewer>(owner) is not { } viewer)
                return;
            viewer.ScrollToVerticalOffset(verticalOffset);
            viewer.ScrollToHorizontalOffset(horizontalOffset);
        }));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private sealed class DragSourceState
    {
        public Point Start { get; set; }
        public object? Source { get; set; }
        public ItemsControl? Owner { get; set; }
        public bool HasScrollOffset { get; set; }
        public double VerticalOffset { get; set; }
        public double HorizontalOffset { get; set; }
    }
}

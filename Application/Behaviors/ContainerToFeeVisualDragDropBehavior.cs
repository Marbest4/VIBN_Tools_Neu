using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
    private static Point _dragStart;

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

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args) =>
        _dragStart = args.GetPosition(null);

    private static void OnPreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element)
            return;

        Point current = args.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        object? source = ResolveItem(element, args.OriginalSource as DependencyObject);
        if (source is null)
            return;

        var data = new DataObject(DataFormat, source);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move | DragDropEffects.Link);
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

    private static object? ResolveItem(FrameworkElement sourceElement, DependencyObject? originalSource)
    {
        DependencyObject? current = originalSource;
        while (current is not null && current != sourceElement)
        {
            if (current is FrameworkElement frameworkElement &&
                frameworkElement.DataContext is not null &&
                frameworkElement.DataContext != sourceElement.DataContext)
                return frameworkElement.DataContext;

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        if (sourceElement is ListBox listBox)
            return listBox.SelectedItem;

        return sourceElement.DataContext;
    }
}

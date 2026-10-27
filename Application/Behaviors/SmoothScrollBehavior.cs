using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>Pixel wheel scrolling keeps item recycling and scrollbar dragging intact.</summary>
public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScrollBehavior), new PropertyMetadata(0d, OnOffsetChanged));
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SmoothScrollBehavior), new PropertyMetadata(double.NaN));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not Control control) return;
        if ((bool)args.NewValue)
        {
            control.PreviewMouseWheel += OnWheel;
            control.PreviewMouseDown += CancelScroll;
            control.PreviewKeyDown += CancelScroll;
            control.Unloaded += CancelScroll;
        }
        else
        {
            Cancel(control);
            control.PreviewMouseWheel -= OnWheel;
            control.PreviewMouseDown -= CancelScroll;
            control.PreviewKeyDown -= CancelScroll;
            control.Unloaded -= CancelScroll;
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not Control control ||
            FindViewer(control) is not { } viewer || viewer.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0) return;
        var previous = (double)viewer.GetValue(TargetProperty);
        var start = viewer.VerticalOffset;
        var step = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
        var target = Math.Clamp((double.IsFinite(previous) ? previous : start) - args.Delta / 120d * step, 0d, viewer.ScrollableHeight);
        if (Math.Abs(target - start) < .1) return;
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, start);
        viewer.SetValue(TargetProperty, target);
        var animation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(130))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) =>
        {
            viewer.SetValue(OffsetProperty, target);
            viewer.BeginAnimation(OffsetProperty, null);
            viewer.SetValue(TargetProperty, double.NaN);
        };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        args.Handled = true;
    }

    private static void OnOffsetChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is ScrollViewer viewer && args.NewValue is double offset && double.IsFinite(offset))
            viewer.ScrollToVerticalOffset(Math.Clamp(offset, 0d, Math.Max(0d, viewer.ScrollableHeight)));
    }
    private static void CancelScroll(object sender, RoutedEventArgs args)
    { if (sender is Control control) Cancel(control); }
    private static void Cancel(Control control)
    {
        if (FindViewer(control) is not { } viewer) return;
        var current = viewer.VerticalOffset;
        viewer.BeginAnimation(OffsetProperty, null);
        viewer.SetValue(OffsetProperty, current);
        viewer.SetValue(TargetProperty, double.NaN);
    }
    private static ScrollViewer? FindViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindViewer(child) is { } found) return found;
        }
        return null;
    }
}

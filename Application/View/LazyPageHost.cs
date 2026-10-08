using System.Windows;
using System.Windows.Controls;

namespace VIBN_Tools.Application.View;

/// <summary>
/// Creates a tab page only when its content becomes visible for the first time.
/// The instance is retained afterwards so page state is preserved while users
/// switch between tabs.
/// </summary>
public sealed class LazyPageHost : ContentControl
{
    public static readonly DependencyProperty PageTypeProperty = DependencyProperty.Register(
        nameof(PageType),
        typeof(Type),
        typeof(LazyPageHost),
        new PropertyMetadata(null, OnPageTypeChanged));

    private bool _isCreatingContent;

    public LazyPageHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) => EnsureContent();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
                EnsureContent();
        };
    }

    public Type? PageType
    {
        get => (Type?)GetValue(PageTypeProperty);
        set => SetValue(PageTypeProperty, value);
    }

    internal bool IsPageCreated => Content is not null;

    internal void EnsureContent()
    {
        if (_isCreatingContent || Content is not null || PageType is null || !IsVisible)
            return;

        _isCreatingContent = true;
        try
        {
            Content = Activator.CreateInstance(PageType) as UIElement ??
                      throw new InvalidOperationException($"{PageType.FullName} ist kein WPF-UIElement.");
        }
        catch (Exception exception)
        {
            ApplicationLogService.Instance.Error(
                "Navigation",
                $"Der Reiter {PageType.Name} konnte nicht initialisiert werden.",
                exception);
            Content = new TextBlock
            {
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DarkRed,
                Text = $"Der Reiter konnte nicht geladen werden: {exception.GetBaseException().Message}"
            };
        }
        finally
        {
            _isCreatingContent = false;
        }
    }

    private static void OnPageTypeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not LazyPageHost host)
            return;
        host.Content = null;
        host.EnsureContent();
    }
}

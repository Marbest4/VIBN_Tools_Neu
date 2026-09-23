using System.Windows;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Carries an inherited data context into WPF objects such as DataGridColumn
/// that do not participate in the visual or logical tree.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(object),
        typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

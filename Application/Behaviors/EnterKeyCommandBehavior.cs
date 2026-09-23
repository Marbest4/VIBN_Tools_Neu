using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Executes a bound command when Enter is pressed in a text box. The behavior
/// keeps keyboard handling in XAML without moving board-write logic into the
/// view's code-behind.
/// </summary>
public static class EnterKeyCommandBehavior
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(EnterKeyCommandBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.RegisterAttached(
            "CommandParameter",
            typeof(object),
            typeof(EnterKeyCommandBehavior));

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static object? GetCommandParameter(DependencyObject element) =>
        element.GetValue(CommandParameterProperty);

    public static void SetCommandParameter(DependencyObject element, object? value) =>
        element.SetValue(CommandParameterProperty, value);

    private static void OnCommandChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBox textBox)
            return;

        textBox.PreviewKeyDown -= OnPreviewKeyDown;
        if (args.NewValue is ICommand)
            textBox.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || sender is not TextBox textBox)
            return;

        // Commit the current edit before the asynchronous board command reads
        // the field view models.
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var command = GetCommand(textBox);
        var parameter = GetCommandParameter(textBox);
        if (command?.CanExecute(parameter) != true)
            return;

        args.Handled = true;
        command.Execute(parameter);
    }
}

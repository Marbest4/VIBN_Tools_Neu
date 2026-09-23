using System.Windows.Input;

namespace VIBN_Tools.SharedWpf.Commands;

/// <summary>Reusable WPF command for synchronous view-model actions.</summary>
public sealed class RelayCommand(
    Action execute,
    Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>Reusable WPF command whose parameter is reference-typed.</summary>
public sealed class RelayCommand<T>(
    Action<T?> execute,
    Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter as T) ?? true;

    public void Execute(object? parameter) => execute(parameter as T);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

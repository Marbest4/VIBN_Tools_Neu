using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VIBN_Tools.GlobalClasses;

/// <summary>
/// Minimal observable base shared by feature libraries and WPF presentation
/// models. It deliberately has no dependency on FEE or application services.
/// </summary>
public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetPropertyChange<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public virtual void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

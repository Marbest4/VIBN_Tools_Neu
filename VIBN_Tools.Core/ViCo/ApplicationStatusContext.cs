using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VIBN_Tools.Core.ViCo;

/// <summary>
/// Application-wide display context for the workstation selected in the
/// Rechnerübersicht. It deliberately stores no credentials or domain model.
/// </summary>
public sealed class ApplicationStatusContext : INotifyPropertyChanged
{
    public static ApplicationStatusContext Instance { get; } = new();

    private string? _selectedWorkstationName;

    private ApplicationStatusContext()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? SelectedWorkstationName
    {
        get => _selectedWorkstationName;
        set
        {
            if (string.Equals(_selectedWorkstationName, value, StringComparison.Ordinal))
                return;
            _selectedWorkstationName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedWorkstationDisplay));
        }
    }

    public string SelectedWorkstationDisplay =>
        string.IsNullOrWhiteSpace(SelectedWorkstationName)
            ? "kein Rechner ausgewählt"
            : SelectedWorkstationName;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

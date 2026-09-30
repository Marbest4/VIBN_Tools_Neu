using System.Security.Principal;
using System.Windows.Input;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

/// <summary>
/// Application-wide startup state.  It loads the dynamic workstation directory
/// and the same central role list that is used by ViCo administration.
/// </summary>
public sealed class MainWindowVM : MvvmBase
{
    private bool _canUseLevel7Features;
    private bool _canUseLevel8Features;
    private bool _canUseLevel9Features;
    private string _currentLevel = "Nicht erkannt";
    private readonly INavigationPreferenceStore _navigationPreferences;
    private bool _isNavigationExpanded = true;

    public MainWindowVM(INavigationPreferenceStore? navigationPreferences = null)
    {
        _navigationPreferences = navigationPreferences ?? new JsonNavigationPreferenceStore();
        _isNavigationExpanded = _navigationPreferences.LoadExpanded();
        ToggleNavigationCommand = GetCommandBinding(ToggleNavigation);
    }

    public FeeConnectionService Connection => Services.Connection;

    public string BuildInformation => ApplicationBuildInformation.DisplayText;

    public ICommand ToggleNavigationCommand { get; }

    public bool IsNavigationExpanded
    {
        get => _isNavigationExpanded;
        private set
        {
            if (_isNavigationExpanded == value)
                return;
            _isNavigationExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NavigationToggleText));
        }
    }

    public string NavigationToggleText => IsNavigationExpanded
        ? "_Navigation einklappen"
        : "_Navigation ausklappen";

    /// <summary>CAD Wizard, Container Generation and Container2Fee.</summary>
    public bool CanUseLevel7Features
    {
        get => _canUseLevel7Features;
        private set
        {
            _canUseLevel7Features = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Kanbanize card workspace.</summary>
    public bool CanUseLevel8Features
    {
        get => _canUseLevel8Features;
        private set
        {
            _canUseLevel8Features = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Administration plus diagnostic/reverse-generation tools which expose
    /// or mutate project-wide FEE state.
    /// </summary>
    public bool CanUseLevel9Features
    {
        get => _canUseLevel9Features;
        private set
        {
            _canUseLevel9Features = value;
            OnPropertyChanged();
        }
    }

    public string CurrentLevel
    {
        get => _currentLevel;
        private set
        {
            _currentLevel = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        var workstationsTask = InitializeWorkstationsAsync();
        var rolesTask = InitializeRolesAsync();
        await Task.WhenAll(workstationsTask, rolesTask);
    }

    private static async Task InitializeWorkstationsAsync()
    {
        try
        {
            await ViCoFeatureBootstrapper.InitializeWorkstationDirectoryAsync();
            ApplicationLogService.Instance.Information(
                "Arbeitsstationen",
                $"{ViCoFeatureBootstrapper.WorkstationDirectory.Entries.Count - 1} PCs aus dem ViCo-Cache geladen.");
        }
        catch (Exception exception)
        {
            ApplicationLogService.Instance.Error(
                "Arbeitsstationen",
                "Die gemeinsame PC-Liste konnte beim Start nicht geladen werden.",
                exception);
        }
    }

    private async Task InitializeRolesAsync()
    {
        var currentUser = WindowsIdentity.GetCurrent().Name;
        try
        {
            var roles = ViCoFeatureBootstrapper.UserRoleStore.IsConfigured
                ? await ViCoFeatureBootstrapper.UserRoleStore.LoadAsync()
                : ViCoRolePolicy.ApplyMandatoryRoles(Array.Empty<ViCoUserRole>());
            var persistedLevel = roles.FirstOrDefault(role =>
                WindowsUserIdentity.Equals(role.UserName, currentUser))?.Level;
            ApplyRole(ViCoRolePolicy.GetEffectiveLevel(currentUser, persistedLevel));
        }
        catch (Exception exception)
        {
            // The mandatory system administrator remains usable even while a
            // shared drive is temporarily unavailable; every other user gets
            // no privileged navigation until the role store can be read again.
            ApplyRole(ViCoRolePolicy.GetEffectiveLevel(currentUser, null));
            ApplicationLogService.Instance.Error(
                "Rollenverwaltung",
                "Die zentrale Rollenliste konnte beim Start nicht geladen werden.",
                exception);
        }
    }

    private void ApplyRole(string level)
    {
        CurrentLevel = level;
        CanUseLevel7Features = ViCoRolePolicy.HasMinimumLevel(level, 7);
        CanUseLevel8Features = ViCoRolePolicy.HasMinimumLevel(level, 8);
        CanUseLevel9Features = ViCoRolePolicy.HasMinimumLevel(level, 9);
    }

    private void ToggleNavigation()
    {
        IsNavigationExpanded = !IsNavigationExpanded;
        try
        {
            _navigationPreferences.SaveExpanded(IsNavigationExpanded);
        }
        catch (Exception exception)
        {
            ApplicationLogService.Instance.Error(
                "Navigation",
                "Die Navigationsbreite konnte nicht als Benutzerpräferenz gespeichert werden.",
                exception);
        }
    }
}

using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;

namespace VIBN_Tools.Application.VM;

public sealed partial class Fee2ContainerPageVM
{
    public bool HasRequiredRequirements => RequirementsProvider.RequirementsFile is { IsInitialized: true };
    public string RefreshRootsHelp => CanRefresh ? "Liest den aktuellen Szenenbestand und die Signalrouten für die gewählten Interfaces. Anschließend Roots markieren und FEE-Objekte separat zuordnen. Ersetzt den Container-Arbeitsstand nicht." : RefreshUnavailableReason;
    public string ReadInterfacesHelp => CanReadInterfaces
        ? "Liest aktuelle Interfaces und Signale. Beginnt einen neuen Arbeitsstand und leert die Rückgängig-Historie. Voraussetzung: Verbindung und Update Objects."
        : RefreshUnavailableReason;
    public string GenerateSignalContainersHelp => IsBusy ? "Ein FEE2Container-Vorgang läuft bereits."
        : !HasRequiredRequirements ? "Zuerst eine gültige Req.xml laden oder in ContainerGeneration einlesen. Die Req.xml ist erforderlich."
        : !Connection.CanUseFeeFeatures || !Connection.AreModelValidationObjectsCurrent ? RefreshUnavailableReason
        : _signalsSnapshot is null || _signalsSnapshot.ConnectionRevision != Connection.ConnectionRevision ? "Zuerst Interfaces für die aktuelle FEE-Verbindung einlesen."
        : !Interfaces.Any(item => item.IsSelected) ? "Mindestens ein Interface unter Suchen auswählen."
        : "Erstellt Container aus aktiven Signalverbindungen der ausgewählten Interfaces und der Req.xml. Objektzuordnungen erfolgen erst im zweiten Schritt; der vorherige Stand ist rückgängig machbar.";
    public string AssignSceneObjectsHelp => IsBusy ? "Ein FEE2Container-Vorgang läuft bereits."
        : !HasRequiredRequirements ? "Eine gültige Req.xml ist erforderlich."
        : !Connection.CanUseFeeFeatures || !Connection.AreModelValidationObjectsCurrent ? RefreshUnavailableReason
        : SelectedRoot is null ? "Zuerst Container aus den ausgewählten Signal-Interfaces erstellen."
        : !GeneratedScopeMatchesSelection ? "Die Interface-Auswahl hat sich geändert. Container aus Signalen erneut erstellen und FEE-Roots neu einlesen."
        : !ObjectRoots.Any(item => item.IsSelected) ? "FEE-Roots einlesen und mindestens einen Root unter Zuordnen markieren."
        : "Ordnet aktuelle Logiken, SimObjects und Cabinets aus den markierten FEE-Roots zu. Signal-GUIDs und aktuelle Slotverbindungen begründen die Zuordnung. FEE selbst wird nicht verändert.";
    public string UndoHelp => IsBusy ? "Während eines laufenden Vorgangs ist Rückgängig gesperrt."
        : !CanUndo ? "Noch keine rückgängig machbare Änderung vorhanden. Ein erneutes Interface-Einlesen leert die Historie."
        : "Nimmt die letzte Änderung am Container-Arbeitsstand zurück, einschließlich Signalverschieben, Entfernen, Zuordnung und Grouping. Bis zu 20 Stände; keine Änderung an FEE.";

    private void NotifyButtonHelp()
    {
        OnPropertyChanged(nameof(HasRequiredRequirements)); OnPropertyChanged(nameof(ReadInterfacesHelp)); OnPropertyChanged(nameof(RefreshRootsHelp));
        OnPropertyChanged(nameof(GenerateSignalContainersHelp)); OnPropertyChanged(nameof(AssignSceneObjectsHelp)); OnPropertyChanged(nameof(UndoHelp));
        OnPropertyChanged(nameof(CanExport)); OnPropertyChanged(nameof(ExportUnavailableReason));
    }
}

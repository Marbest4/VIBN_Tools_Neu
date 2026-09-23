using System.Collections.ObjectModel;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>
/// A fast, on-demand quality check of the complete editable workspace.
/// It deliberately performs no continuous background scans while the user edits.
/// </summary>
public sealed record WorkspaceValidationSummary(
    int TotalContainers,
    int AssignedSignals,
    int UnassignedSignals,
    int FilteredSignals,
    int InvalidContainers,
    int ContainersToReview,
    int UncheckedContainers,
    int MissingSignalNames,
    int MissingSlots,
    int UnknownSlots,
    int DuplicateSlots,
    int DuplicateSignalIds,
    IReadOnlyList<string> Details)
{
    public bool HasBlockingIssues =>
        InvalidContainers > 0 ||
        MissingSignalNames > 0 ||
        MissingSlots > 0 ||
        UnknownSlots > 0 ||
        DuplicateSlots > 0 ||
        DuplicateSignalIds > 0;

    public bool HasWarnings =>
        HasBlockingIssues ||
        ContainersToReview > 0 ||
        UncheckedContainers > 0 ||
        UnassignedSignals > 0;

    public string ToStatusText() =>
        $"Prüfung: {TotalContainers} Container, {AssignedSignals} zugeordnet, " +
        $"{InvalidContainers} ungültig, {ContainersToReview} zu prüfen, " +
        $"{UnassignedSignals} nicht zugeordnet.";

    public string ToDisplayText(int maximumDetails = 15)
    {
        var lines = new List<string>
        {
            "Prüfzusammenfassung",
            string.Empty,
            $"Container: {TotalContainers}",
            $"Zugeordnete Signale: {AssignedSignals}",
            $"Nicht zugeordnete Signale: {UnassignedSignals}",
            $"Gefilterte Signale: {FilteredSignals}",
            string.Empty,
            $"Ungültige Container: {InvalidContainers}",
            $"Container mit Prüfbedarf: {ContainersToReview}",
            $"Noch nicht fachlich abgehakt: {UncheckedContainers}",
            $"Fehlende Signalnamen: {MissingSignalNames}",
            $"Fehlende Slots: {MissingSlots}",
            $"Nicht vorhandene Slots: {UnknownSlots}",
            $"Doppelt belegte Slots: {DuplicateSlots}",
            $"Doppelte Signal-IDs: {DuplicateSignalIds}"
        };

        if (Details.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Konkrete Hinweise:");
            lines.AddRange(Details.Take(maximumDetails).Select(detail => $"• {detail}"));
            if (Details.Count > maximumDetails)
                lines.Add($"• … und {Details.Count - maximumDetails} weitere Hinweise");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("Es wurden keine technischen oder fachlichen Prüfhinweise erkannt.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public static class WorkspaceValidationAnalyzer
{
    public static WorkspaceValidationSummary Analyze(
        IEnumerable<ContainerData> containers,
        IEnumerable<ContainerEntry> unassigned,
        IEnumerable<ContainerEntry> filtered,
        BusinessLogic.RequirementsXml.IRequirementsXml? requirements = null)
    {
        var containerList = containers.ToList();
        var unassignedList = unassigned.ToList();
        var filteredList = filtered.ToList();

        foreach (var container in containerList)
            container.Validate();

        var assigned = containerList.SelectMany(container => container.DataList).ToList();
        var allEntries = assigned.Concat(unassignedList).Concat(filteredList).ToList();
        var details = new List<string>();

        foreach (var entry in allEntries)
        {
            entry.EnsureSignalId();
            entry.ValidationError = string.Empty;
        }

        var invalidContainers = containerList.Where(container => !container.IsValid).ToList();
        var containersToReview = containerList.Where(container => container.RequiresReview).ToList();
        var uncheckedContainers = containerList
            .Where(container =>
                !container.ManuallyChecked &&
                (!container.IsValid || container.HasDetectedChanges))
            .ToList();
        var missingSignalNames = allEntries.Count(entry => string.IsNullOrWhiteSpace(entry.Signal));
        var missingSlots = assigned.Count(entry => string.IsNullOrWhiteSpace(entry.Slot));
        var unknownSlots = 0;
        var duplicateSlots = 0;

        foreach (var container in containerList)
        {
            var duplicateGroups = container.DataList
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Slot))
                .GroupBy(entry => entry.Slot, StringComparer.OrdinalIgnoreCase)
                .Where(group => ContainerSlotMultiplicityPolicy.GetDuplicateError(
                    group.Key,
                    group.Count()) is not null)
                .ToList();
            duplicateSlots += duplicateGroups.Sum(group => group.Count() - 1);

            if (!container.IsValid)
            {
                details.Add(
                    $"Container „{DisplayContainer(container)}“ ist ungültig: " +
                    $"{DisplayReason(container.ValidationError)}");
            }

            foreach (var group in duplicateGroups)
            {
                foreach (var entry in group)
                    AddEntryError(
                        entry,
                        ContainerSlotMultiplicityPolicy.GetDuplicateError(group.Key, group.Count())!);
                details.Add(
                    $"Container „{DisplayContainer(container)}“ verwendet Slot " +
                    $"„{group.Key}“ {group.Count()}-mal.");
            }
        }

        foreach (var entry in allEntries.Where(entry => string.IsNullOrWhiteSpace(entry.Signal)))
        {
            AddEntryError(entry, "Signalname fehlt.");
            details.Add($"Signal-ID {entry.SignalId}: Signalname fehlt.");
        }

        foreach (var entry in assigned.Where(entry => string.IsNullOrWhiteSpace(entry.Slot)))
        {
            AddEntryError(entry, "Slot fehlt.");
            details.Add($"Signal „{DisplaySignal(entry)}“: Slot fehlt.");
        }

        if (requirements?.IsInitialized == true)
        {
            foreach (var container in containerList)
            {
                var allowedSlots = requirements.GetSlotNames(container.Type)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in container.DataList.Where(entry =>
                             !string.IsNullOrWhiteSpace(entry.Slot) &&
                             !allowedSlots.Contains(entry.Slot)))
                {
                    unknownSlots++;
                    var expected = allowedSlots.Count == 0
                        ? "Der Containertyp ist in AutoCreate_Master.xml nicht definiert."
                        : $"Zulässige Slots: {string.Join(", ", allowedSlots.OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase))}.";
                    var message = $"Slot „{entry.Slot}“ existiert für Typ „{container.Type}“ nicht. {expected} " +
                                  "Bitte die Zuordnung vor Export anpassen.";
                    AddEntryError(entry, message);
                    details.Add($"Container „{DisplayContainer(container)}“, Signal „{DisplaySignal(entry)}“: {message}");
                }
            }
        }

        var duplicateSignalIdGroups = allEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SignalId))
            .GroupBy(entry => entry.SignalId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        var duplicateSignalIds = duplicateSignalIdGroups.Sum(group => group.Count() - 1);
        if (duplicateSignalIds > 0)
        {
            foreach (var group in duplicateSignalIdGroups)
            foreach (var entry in group)
                AddEntryError(entry, $"Interne Signal-ID „{group.Key}“ ist nicht eindeutig.");
            details.Add($"{duplicateSignalIds} doppelte interne Signal-ID(s) erkannt.");
        }

        foreach (var container in containersToReview.Where(container => container.IsValid))
        {
            details.Add(
                $"Container „{DisplayContainer(container)}“ enthält Änderungen oder " +
                "Prüfhinweise und ist noch nicht abgehakt.");
        }

        return new WorkspaceValidationSummary(
            containerList.Count,
            assigned.Count,
            unassignedList.Count,
            filteredList.Count,
            invalidContainers.Count,
            containersToReview.Count,
            uncheckedContainers.Count,
            missingSignalNames,
            missingSlots,
            unknownSlots,
            duplicateSlots,
            duplicateSignalIds,
            details.Distinct(StringComparer.Ordinal).ToList());
    }

    private static string DisplayContainer(ContainerData container) =>
        !string.IsNullOrWhiteSpace(container.Component)
            ? container.Component
            : !string.IsNullOrWhiteSpace(container.Id)
                ? container.Id
                : "ohne Namen";

    private static string DisplaySignal(ContainerEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Signal)
            ? entry.Signal
            : !string.IsNullOrWhiteSpace(entry.ID)
                ? entry.ID
                : entry.SignalId;

    private static string DisplayReason(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "keine Detailangabe"
            : reason.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();

    private static void AddEntryError(ContainerEntry entry, string error)
    {
        entry.ValidationError = string.IsNullOrWhiteSpace(entry.ValidationError)
            ? error
            : entry.ValidationError + Environment.NewLine + error;
    }
}

/// <summary>
/// Adds an explicit, schema-compatible quarantine container when a user
/// deliberately exports an invalid workspace. Downstream tools can detect the
/// marker without parsing a comment or relying on a file name.
/// </summary>
public static class WorkspaceValidationOverrideMarker
{
    public const string ContainerId = "VIBN-VALIDATION-ERRORS";
    public const string Component = "Fehler";
    public const string Type = "Fehler";

    public static ComponentContainer Create(WorkspaceValidationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var messages = summary.Details.Count > 0
            ? summary.Details
            : new[] { summary.ToStatusText() };
        return new ComponentContainer
        {
            Id = ContainerId,
            Component = Component,
            Type = Type,
            DataList = new ObservableCollection<ContainerEntry>(messages.Select((message, index) =>
                new ContainerEntry
                {
                    ID = $"ERROR-{index + 1:000}",
                    Address = string.Empty,
                    DataType = "String",
                    Signal = "VALIDATION_ERROR",
                    Slot = $"ERROR_{index + 1:000}",
                    Note = message
                }))
        };
    }
}

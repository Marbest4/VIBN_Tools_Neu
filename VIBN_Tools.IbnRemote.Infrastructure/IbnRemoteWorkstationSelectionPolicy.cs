using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.IbnRemote.Infrastructure;

/// <summary>
/// Applies the deployment filter exclusively to active "In Arbeit" project
/// cards. Workstation metadata and planning/completed cards cannot cause a hit.
/// </summary>
public static class IbnRemoteWorkstationSelectionPolicy
{
    public static IReadOnlyList<ViCoWorkstation> Select(
        IEnumerable<ViCoWorkstation> workstations,
        string? inWorkFilter)
    {
        ArgumentNullException.ThrowIfNull(workstations);
        var term = inWorkFilter?.Trim() ?? string.Empty;
        return workstations
            .Where(workstation => term.Length == 0 || workstation.WorkingProjects.Any(project =>
                project.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(workstation => workstation.PcName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

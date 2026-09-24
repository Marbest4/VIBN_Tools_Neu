namespace VIBN_Tools.Rockwell;

/// <summary>
/// Defines the explicit, reviewable transformation stages supported for a
/// customer standard. A standard must be registered here before it appears in
/// the desktop UI; silently applying GCCS rules to another standard is avoided.
/// </summary>
public sealed record RockwellStandardDefinition(
    string Id,
    string DisplayName,
    string Description)
{
    public RockwellEditResult ApplyStage(RockwellProjectEditor editor, int stage) => stage switch
    {
        1 => editor.EnsureSimulationBasics(),
        2 => editor.EnsureInputSimulation(safety: false),
        3 => editor.EnsureInputSimulation(safety: true),
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown Rockwell stage."),
    };
}

public static class RockwellStandardCatalog
{
    public static IReadOnlyList<RockwellStandardDefinition> All { get; } =
    [
        new(
            "GCCS",
            "GCCS",
            "Ergänzt die drei geprüften GCCS-Schritte: Simulationsbasis, A001 Standard und A001 Safety."),
    ];
}

namespace VIBN_Tools.Rockwell;

/// <summary>
/// Defines the explicit, reviewable transformation stages supported for a
/// customer standard. A standard must be registered here before it appears in
/// the desktop UI; silently applying GCCS rules to another standard is avoided.
/// </summary>
public sealed record RockwellStandardDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool SupportsGeneration = true)
{
    public RockwellEditResult ApplyStage(RockwellProjectEditor editor, int stage)
    {
        if (!SupportsGeneration)
            throw new InvalidOperationException(
                "Es ist bewusst kein Simulationsstandard ausgewählt. Die drei L5X-Änderungsschritte sind daher deaktiviert; Interface-Export und reine Analyse bleiben verfügbar.");
        return stage switch
        {
            1 => editor.EnsureSimulationBasics(),
            2 => editor.EnsureInputSimulation(safety: false),
            3 => editor.EnsureInputSimulation(safety: true),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown Rockwell stage."),
        };
    }
}

public static class RockwellStandardCatalog
{
    public static IReadOnlyList<RockwellStandardDefinition> All { get; } =
    [
        new(
            "GCCS",
            "GCCS",
            "Ergänzt die drei geprüften GCCS-Schritte: Simulationsbasis, A001 Standard und A001 Safety."),
        new(
            "NONE",
            "Kein Standard",
            "Nimmt keine Änderung an der L5X vor. Der Allen-Bradley-Interface-Export und die Projektübersicht bleiben verwendbar.",
            SupportsGeneration: false),
    ];
}

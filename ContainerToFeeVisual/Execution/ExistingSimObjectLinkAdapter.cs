using System.Reflection;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Settings;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Links already existing simulation objects to already existing FEE logic
/// objects. It deliberately performs no object, container, interface or signal
/// creation.
/// </summary>
internal sealed class ExistingSimObjectLinkAdapter(IVisualPlanLogger logger)
{
    public async Task<VisualExecutionResult> ExecuteAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        CancellationToken cancellationToken)
    {
        if (Services.Connection?.CanUseFeeFeatures != true)
            return Failure(FeeConnectionService.MissingConnectionMessage, "FEE_NOT_CONNECTED");

        var modelObjects = Services.FeeObjects?.AllFeeObjects;
        if (modelObjects is null || modelObjects.Count == 0)
        {
            return Failure(
                "Es wurden noch keine vollständigen FEE-Modelldaten gelesen. Zuerst unter " +
                "Model Validation 'Update Objects' ausführen.",
                "FEE_MODEL_CACHE_EMPTY");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = RuntimeVisualPlanBinder.Bind(plan, runtimeObjects);
            if (!binding.Success)
                return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);

            var logicObjects = modelObjects.OfType<FeeLogic>().ToArray();
            var work = new List<(ILogicSimObjectOwner Owner, FeeLogic Logic, string ContainerName)>();
            foreach (var bound in binding.Containers.Where(item =>
                         plan.IsGenerationSelected(item.PlanNode.Id) &&
                         plan.Assignments.Any(assignment =>
                             plan.FindTarget(assignment.TargetId)?.ContainerId == item.PlanNode.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bound.RuntimeContainer is not ILogicSimObjectOwner owner)
                {
                    return Failure(
                        $"Container '{bound.PlanNode.Name}' unterstützt keine reine " +
                        "SimObject-zu-Logik-Verknüpfung.",
                        "LINK_ONLY_CONTAINER_UNSUPPORTED",
                        bound.PlanNode.Id);
                }

                var matchingLogics = logicObjects
                    .Where(logic => string.Equals(
                        logic.Name,
                        bound.RuntimeContainer.ComponentName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingLogics.Length == 0)
                {
                    return Failure(
                        $"Für '{bound.PlanNode.Name}' wurde kein bestehendes FEE-LogicObject mit " +
                        "identischem Namen gefunden. Model Validation aktualisieren oder den " +
                        "Komponentennamen prüfen.",
                        "EXISTING_LOGIC_NOT_FOUND",
                        bound.PlanNode.Id);
                }
                if (matchingLogics.Length > 1)
                {
                    return Failure(
                        $"Für '{bound.PlanNode.Name}' existieren mehrere gleichnamige FEE-LogicObjects. " +
                        "Die Zuordnung ist nicht eindeutig.",
                        "EXISTING_LOGIC_AMBIGUOUS",
                        bound.PlanNode.Id);
                }

                var logicProperty = FindLogicProperty(bound.RuntimeContainer.GetType());
                if (logicProperty is null)
                {
                    return Failure(
                        $"Die bestehende Logikreferenz für '{bound.PlanNode.Name}' konnte nicht gesetzt werden.",
                        "EXISTING_LOGIC_PROPERTY_NOT_FOUND",
                        bound.PlanNode.Id);
                }
                logicProperty.SetValue(bound.RuntimeContainer, matchingLogics[0]);
                work.Add((owner, matchingLogics[0], bound.PlanNode.Name));
            }

            if (work.Count == 0)
            {
                return Failure(
                    "Für die ausgewählten Container ist keine FEE-SimObject-Zuordnung vorhanden.",
                    "NO_LINK_ASSIGNMENTS_SELECTED");
            }

            // Link writes are serialized because all operations share the FEE
            // SDK session and a partial parallel burst is hard to diagnose.
            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await item.Owner.AssignSimObjectsAsync();
            }

            var message = $"{work.Count} bestehende SimObject-Verknüpfung(en) wurden aktualisiert; " +
                          "es wurden keine Container neu erzeugt.";
            logger.Information(message);
            return new VisualExecutionResult(true, message, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Error("Reine SimObject-Verknüpfung ist fehlgeschlagen.", exception);
            return new VisualExecutionResult(
                false,
                "Verknüpfung fehlgeschlagen. Details stehen im Protokoll.",
                [new VisualIssue(VisualIssueSeverity.Error, "LINK_ONLY_EXECUTION_FAILED", exception.Message)]);
        }
    }

    private static PropertyInfo? FindLogicProperty(Type containerType)
    {
        var properties = containerType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.PropertyType == typeof(FeeLogic))
            .ToArray();
        return properties.Length == 1 ? properties[0] : null;
    }

    private static VisualExecutionResult Failure(string message, string code, string? nodeId = null) =>
        new(false, message, [new VisualIssue(VisualIssueSeverity.Error, code, message, nodeId)]);
}

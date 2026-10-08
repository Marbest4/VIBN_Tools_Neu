using System.Reflection;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.ContainerToFee;
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
    public Task<VisualExecutionResult> ExecuteAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyList<FeeAbstractObject> sceneObjects,
        CancellationToken cancellationToken) => FeeMutationScope.RunAsync(
        () => ExecuteCoreAsync(plan, runtimeObjects, sceneObjects, cancellationToken), cancellationToken);

    private async Task<VisualExecutionResult> ExecuteCoreAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyList<FeeAbstractObject> sceneObjects,
        CancellationToken cancellationToken)
    {
        var selectedIds = RuntimeVisualPlanBinder.SelectedContainerIds(plan);
        if (selectedIds.Count == 0)
            return new VisualExecutionResult(true, "Keine Container ausgewählt; keine SimObject-Verknüpfungen geändert.", []);
        if (Services.Connection?.CanUseFeeFeatures != true)
            return Failure(FeeConnectionService.MissingConnectionMessage, "FEE_NOT_CONNECTED");

        var modelObjects = sceneObjects;
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
            var binding = RuntimeVisualPlanBinder.Bind(plan, runtimeObjects,
                includedContainerIds: selectedIds, includeSignals: false);
            if (!binding.Success)
                return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);

            var logicObjects = modelObjects.OfType<FeeLogic>().ToArray();
            var work = new List<(FeeLogic Logic, string ContainerName, IReadOnlyList<RequiredSimObjectLink> Links)>();
            var issues = new List<VisualIssue>();
            foreach (var bound in binding.Containers.Where(item =>
                         plan.IsGenerationSelected(item.PlanNode.Id) &&
                         plan.Assignments.Any(assignment =>
                             plan.FindTarget(assignment.TargetId)?.ContainerId == item.PlanNode.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bound.RuntimeContainer is not ILogicSimObjectOwner)
                {
                    issues.Add(new VisualIssue(VisualIssueSeverity.Info, "LINK_ONLY_NOT_REQUIRED",
                        $"'{bound.PlanNode.Name}' besitzt keine Logik; seine SimObject-Zuordnung benötigt keine SimObject-zu-Logik-Verknüpfung.", bound.PlanNode.Id));
                    continue;
                }

                var matchingLogics = logicObjects
                    .Where(logic => string.Equals(
                        logic.Name,
                        bound.RuntimeContainer.ComponentName,
                        StringComparison.OrdinalIgnoreCase) &&
                        ContainerMetadataCatalog.TryGet(bound.PlanNode.TypeName, out var descriptor) &&
                        ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, logic.LogicDefinitionName))
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
                var selectable = (ISimObjectFindOrSelect)bound.RuntimeContainer;
                var required = selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects().SelectMany((item, index) =>
                    SimObjectLinkMap.Create(bound.PlanNode.TypeName, item, index))).ToArray();
                if (required.Length == 0)
                {
                    issues.Add(new VisualIssue(VisualIssueSeverity.Warning, "SIMOBJECT_LINK_ENDPOINTS_MISSING",
                        $"{bound.PlanNode.Name}: keine unterstützten SimObject-Endpunkte; mit Warnung übersprungen.", bound.PlanNode.Id));
                    continue;
                }
                work.Add((matchingLogics[0], bound.PlanNode.Name, required));
            }

            if (work.Count == 0)
            {
                return new VisualExecutionResult(true,
                    "Keine zusätzlichen SimObject-zu-Logik-Verknüpfungen erforderlich; vorhandene Zuordnungen bleiben erhalten.", issues);
            }

            // Link writes are serialized because all operations share the FEE
            // SDK session and a partial parallel burst is hard to diagnose.
            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var joint in item.Links.Where(link => link.ObjectSlot == "InTarget")
                             .Select(link => sceneObjects.FirstOrDefault(scene => scene.Guid == link.ObjectGuid)).OfType<FeeJoint>().DistinctBy(joint => joint.Guid))
                    await ContainerSlotLinkService.EnsurePositionControlAsync(joint, cancellationToken);
                foreach (var floor in item.Links.Where(link => link.ObjectSlot == "Collision")
                             .Select(link => sceneObjects.FirstOrDefault(scene => scene.Guid == link.ObjectGuid)).OfType<FeeFloor>())
                    if (!floor.UseCollisionSlot)
                        await ContainerSlotLinkService.EnsureFloorCollisionSlotEnabledAsync(floor, item.ContainerName, cancellationToken);
                foreach (var group in item.Links.GroupBy(link => link.LogicSlot, StringComparer.OrdinalIgnoreCase))
                    await ContainerSlotLinkService.AssignAndVerifyAsync(
                        new[] { (item.Logic.Guid, group.Key) }.Concat(group.Select(link => (link.ObjectGuid, link.ObjectSlot))).ToArray(),
                        $"{item.ContainerName}: {group.Key}", cancellationToken);
            }

            var message = $"{work.Count} bestehende SimObject-Verknüpfung(en) wurden aktualisiert; " +
                          "es wurden keine Container neu erzeugt.";
            logger.Information(message);
            return new VisualExecutionResult(true, message, issues);
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

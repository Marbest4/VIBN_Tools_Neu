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
    internal IReadOnlyList<VisualFeeObjectLink> ConfirmedLinks { get; private set; } = [];
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
        ConfirmedLinks = [];
        var selectedIds = RuntimeVisualPlanBinder.SelectedContainerIds(plan).Where(id => plan.Assignments.Any(assignment =>
            plan.FindTarget(assignment.TargetId)?.ContainerId == id && runtimeObjects.ContainsKey(assignment.FeeObjectId)))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
            return new VisualExecutionResult(true, "Keine SimObject-Zuordnungen in den ausgewählten Roots; keine Verknüpfungen geändert.", []);
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
                includedContainerIds: selectedIds, includeSignals: false,
                includedFeeObjectIds: runtimeObjects.Keys.ToHashSet(StringComparer.Ordinal));
            if (!binding.Success)
                return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);

            var logicResolver = new ExistingContainerLogicResolver(modelObjects);
            var byGuid = modelObjects.DistinctBy(item => item.Guid).ToDictionary(item => item.Guid);
            var confirmed = new List<VisualFeeObjectLink>();
            ConfirmedLinks = confirmed;
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

                var selectable = (ISimObjectFindOrSelect)bound.RuntimeContainer;
                var assigned = selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).ToArray();
                var matchingLogics = logicResolver.Find(bound.PlanNode, assigned);
                if (matchingLogics.Count == 0)
                {
                    return Failure(
                        $"Für '{bound.PlanNode.Name}' wurde kein bestehendes FEE-LogicObject mit " +
                        "identischem Namen gefunden. Model Validation aktualisieren oder den " +
                        "Komponentennamen prüfen.",
                        "EXISTING_LOGIC_NOT_FOUND",
                        bound.PlanNode.Id);
                }
                if (matchingLogics.Count > 1)
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
                logger.Information($"{bound.PlanNode.Name}: bestehende Logik '{matchingLogics[0].Name}', " +
                    $"Objekt-GUID {matchingLogics[0].Guid:D}, Definition '{matchingLogics[0].LogicDefinitionName}' gebunden.");
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
                             .Select(link => byGuid.GetValueOrDefault(link.ObjectGuid)).OfType<FeeJoint>().DistinctBy(joint => joint.Guid))
                    await ContainerSlotLinkService.EnsurePositionControlAsync(joint, cancellationToken);
                foreach (var floor in item.Links.Where(link => link.ObjectSlot == "Collision")
                             .Select(link => byGuid.GetValueOrDefault(link.ObjectGuid)).OfType<FeeFloor>())
                    if (!floor.UseCollisionSlot)
                        await ContainerSlotLinkService.EnsureFloorCollisionSlotEnabledAsync(floor, item.ContainerName, cancellationToken);
                foreach (var group in item.Links.GroupBy(link => link.LogicSlot, StringComparer.OrdinalIgnoreCase))
                {
                    await ContainerSlotLinkService.AssignAndVerifyAsync(
                        SimObjectLinkMap.Endpoints(item.Logic.Guid, group.ToArray()),
                        $"{item.ContainerName}: {group.Key}", cancellationToken);
                    confirmed.AddRange(group.Select(link => new VisualFeeObjectLink(link.ObjectGuid.ToString("D"),
                        link.ObjectSlot, item.Logic.GuidString, link.LogicSlot)));
                }
            }

            var message = $"{confirmed.Count} bestehende SimObject-Verknüpfung(en) bestätigt; " +
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

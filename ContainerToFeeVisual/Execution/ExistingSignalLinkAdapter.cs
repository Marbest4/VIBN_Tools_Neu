using FS.SDK.Scene.Objects;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Links variables from explicitly selected existing interfaces to already
/// existing container objects. Missing technical routing helpers may be created;
/// existing variables and primary objects are preserved.
/// </summary>
internal sealed class ExistingSignalLinkAdapter(IVisualPlanLogger logger)
{
    internal IReadOnlyList<VisualFeeSignalLink> KnownLinks { get; private set; } = [];
    internal IReadOnlyList<string> ReadSignalGuids { get; private set; } = [];
    internal IReadOnlyList<(FeeAbstractObject Object, string ContainerId)> CreatedObjects { get; private set; } = [];
    public Task<VisualExecutionResult> ExecuteAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyDictionary<string, FeeInterface> runtimeInterfaces,
        IReadOnlyList<FeeAbstractObject> sceneObjects,
        IReadOnlyList<VisualFeeContainerObject> containerObjects,
        FeeAbstractObject? helperParent,
        bool helperParentIsAmbiguous,
        CancellationToken cancellationToken) => FeeMutationScope.RunAsync(
        () => ExecuteCoreAsync(plan, runtimeObjects, runtimeInterfaces, sceneObjects, containerObjects,
            helperParent, helperParentIsAmbiguous, cancellationToken), cancellationToken);

    private async Task<VisualExecutionResult> ExecuteCoreAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyDictionary<string, FeeInterface> runtimeInterfaces,
        IReadOnlyList<FeeAbstractObject> sceneObjects,
        IReadOnlyList<VisualFeeContainerObject> containerObjects,
        FeeAbstractObject? helperParent,
        bool helperParentIsAmbiguous,
        CancellationToken cancellationToken)
    {
        KnownLinks = [];
        ReadSignalGuids = [];
        CreatedObjects = [];
        var selectedIds = RuntimeVisualPlanBinder.SelectedContainerIds(plan);
        if (selectedIds.Count == 0)
            return new VisualExecutionResult(true, "Keine Container ausgewählt; keine Signalverknüpfungen geändert.", []);
        var selectedInterfaceGuids = plan.ExistingInterfaceSelections.Select(item => item.InterfaceGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedInterfaces = runtimeInterfaces.Values
            .Where(item => selectedInterfaceGuids.Contains(item.GuidString)).ToArray();
        if (selectedInterfaces.Length == 0)
            return Failure("Bitte mindestens ein vorhandenes Interface auswählen und FEE aktualisieren.", "SIGNAL_LINK_INTERFACE_REQUIRED");
        cancellationToken.ThrowIfCancellationRequested();
        var binding = RuntimeVisualPlanBinder.Bind(plan, runtimeObjects, includedContainerIds: selectedIds, bindSimObjects: false);
        if (!binding.Success) return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);
        var usedNodes = new HashSet<string>(StringComparer.Ordinal);
        var requests = binding.Containers.SelectMany(item => item.RuntimeContainer.EnumerateAssignedSignals().Select(signal =>
            LegacyContainerToFeeExecutionAdapter.CreateSignalResolutionRequest(plan, item.PlanNode, signal, usedNodes))).ToArray();
        var signalPlan = SignalResolutionPlanner.Build(requests, selectedInterfaces, plan.SignalAssignments);
        if (!signalPlan.IsValid)
            return new VisualExecutionResult(false, "Signale konnten nicht eindeutig aufgelöst werden.", signalPlan.Issues);
        if (signalPlan.MissingSignals.Count > 0)
            return new VisualExecutionResult(false, "Nicht alle ausgewählten Signale sind in den Interfaces vorhanden.",
                signalPlan.MissingSignals.Select(missing => new VisualIssue(VisualIssueSeverity.Error, "EXISTING_SIGNAL_MISSING",
                    $"Signal '{missing.Signal.Tag}' ist in den ausgewählten Interfaces nicht vorhanden. Es wurde nichts erzeugt.",
                    missing.NodeId ?? missing.ContainerId)).ToArray());
        signalPlan.ApplyExistingBindings();
        var liveSignals = signalPlan.ExistingBindings.Select(item => new VisualFeeSignal(item.ExistingSignal.GuidString,
            item.ExistingInterface.GuidString, item.ExistingInterface.Name ?? "", item.ExistingSignal.Tag ?? "",
            item.ExistingSignal.Address ?? "", item.ExistingSignal.Path ?? "", item.ExistingSignal.IOTypeString ?? "", item.ExistingSignal.UsageString ?? ""));
        var discovery = await new FeeSignalLinkDiscovery(logger).DiscoverAsync(liveSignals, cancellationToken);
        if (discovery.FailedSignalCount > 0)
            return new VisualExecutionResult(true, "Signalrouten konnten nicht vollständig gelesen werden; Reparatur mit Warnung übersprungen.",
                [new VisualIssue(VisualIssueSeverity.Warning, "SIGNAL_LINK_READ_INCOMPLETE",
                    "FEE aktualisieren und erneut versuchen. Es wurden keine bestehenden Routen verändert oder Hilfsobjekte erzeugt.")]);
        var variableGuids = runtimeInterfaces.Values.SelectMany(item => item.Signals ?? []).Select(item => item.Guid).ToHashSet();
        var linker = new ExistingSignalEndpointLinker(discovery.Links, sceneObjects, containerObjects, variableGuids, logger);
        ReadSignalGuids = signalPlan.ExistingBindings.Select(item => item.ExistingSignal.GuidString).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var logicResolver = new ExistingContainerLogicResolver(sceneObjects);
        var requestsByContainer = requests.ToLookup(request => request.ContainerId, StringComparer.Ordinal);
        var issues = new List<VisualIssue>();
        foreach (var bound in binding.Containers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containerRequests = requestsByContainer[bound.PlanNode.Id].ToArray();
            if (containerRequests.Length == 0) continue;
            try
            {
                IReadOnlyList<FeeAbstractObject> targets;
                var container = bound.RuntimeContainer;
                if (container is ContainerToFee.General.SimpleNot_Container or ContainerToFee.General.SimpleMove_Container)
                {
                    var helper = await linker.ResolvePrimaryHelperAsync(bound, containerRequests, helperParent,
                        helperParentIsAmbiguous, issues, cancellationToken);
                    if (helper is null) continue;
                    targets = [helper];
                }
                else if (container is ILogicOwner or ILogicSimObjectOwner)
                {
                    var assigned = plan.Assignments.Where(assignment => plan.FindTarget(assignment.TargetId)?.ContainerId == bound.PlanNode.Id)
                        .Select(assignment => runtimeObjects.GetValueOrDefault(assignment.FeeObjectId))
                        .Where(item => item is not null).Cast<FeeAbstractObject>();
                    var matches = logicResolver.Find(bound.PlanNode, assigned);
                    if (matches.Count != 1)
                    {
                        issues.Add(new VisualIssue(VisualIssueSeverity.Error, "EXISTING_LOGIC_NOT_UNIQUE",
                            $"Logik '{container.ComponentName}' fehlt oder ist nicht eindeutig; keine Signalrouten geändert.", bound.PlanNode.Id));
                        continue;
                    }
                    targets = matches;
                }
                else if (container is ICabinetElementOwner)
                {
                    var expected = ContainerExistingObjectReuse.GetExpectedCabinetElementType(container);
                    var matches = sceneObjects.OfType<FeeCabinetElement>().Where(element =>
                        string.Equals(element.Name, container.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(element.ElementType, expected, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1)
                    {
                        issues.Add(new VisualIssue(VisualIssueSeverity.Error, "EXISTING_CABINET_ELEMENT_NOT_UNIQUE",
                            $"CabinetElement '{container.ComponentName}' fehlt oder ist nicht eindeutig; keine Signalrouten geändert.", bound.PlanNode.Id));
                        continue;
                    }
                    targets = matches;
                }
                else if (container is ISimObjectFindOrSelect selectable)
                {
                    var issue = BindSignalOwner(plan, bound.PlanNode, selectable, runtimeObjects);
                    if (issue is not null) { issues.Add(issue); continue; }
                    targets = selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).DistinctBy(item => item.Guid).ToArray();
                }
                else continue; // SensorX has no object-side endpoint.
                await linker.LinkAsync(bound, containerRequests, targets, issues, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                var helperFailure = bound.RuntimeContainer is ContainerToFee.General.SimpleNot_Container or ContainerToFee.General.SimpleMove_Container ||
                    exception is NullReferenceException;
                var severity = helperFailure ? VisualIssueSeverity.Warning : VisualIssueSeverity.Error;
                var message = $"{bound.PlanNode.Name}: Signalreparatur übersprungen: {exception.Message}";
                issues.Add(new VisualIssue(severity, helperFailure ? "TECHNICAL_HELPER_SKIPPED" : "EXISTING_SIGNAL_LINK_FAILED", message, bound.PlanNode.Id));
                if (helperFailure) logger.Warning(message); else logger.Error(message, exception);
            }
        }
        KnownLinks = linker.KnownLinks;
        CreatedObjects = linker.CreatedObjects.ToArray();
        return new VisualExecutionResult(issues.All(issue => issue.Severity != VisualIssueSeverity.Error),
            $"{linker.LinkedCount} fehlende Signalverknüpfung(en) ergänzt; {linker.SkippedCount} vorhandene Verknüpfung(en) übersprungen. " +
            "Nur erforderliche technische Hilfsobjekte werden erzeugt; vorhandene Variablen bleiben erhalten.", issues);
    }

    private static VisualIssue? BindSignalOwner(VisualPlan plan, VisualNode container,
        ISimObjectFindOrSelect owner, IReadOnlyDictionary<string, FeeAbstractObject> objects)
    {
        var targets = plan.Targets.Where(item => item.ContainerId == container.Id).ToArray();
        var runtimeTargets = owner.GetSimObjectTargets().ToArray();
        if (targets.Length != runtimeTargets.Length)
            return new VisualIssue(VisualIssueSeverity.Error, "SIGNAL_OWNER_TARGET_MISMATCH", "Signalziel passt nicht zum Containertyp.", container.Id);
        for (var index = 0; index < targets.Length; index++)
        {
            var explicitAssignments = plan.Assignments.Where(item => item.TargetId == targets[index].Id).ToArray();
            var scopedAssignments = explicitAssignments.Where(item => objects.ContainsKey(item.FeeObjectId)).ToArray();
            if (explicitAssignments.Length > 0 && scopedAssignments.Length == 0)
                return new VisualIssue(VisualIssueSeverity.Warning, "SIGNAL_OWNER_OUTSIDE_SELECTED_ROOTS",
                    $"Signalziel '{container.Name}' liegt nicht in den ausgewählten FEE-Roots; übersprungen.", container.Id);
            var candidates = explicitAssignments.Length > 0
                ? scopedAssignments.Select(item => objects[item.FeeObjectId]).DistinctBy(item => item.Guid).ToArray()
                : objects.Values.Where(item => string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    runtimeTargets[index].AllowedType.IsInstanceOfType(item)).ToArray();
            if (candidates.Length == 0 ||
                !targets[index].AllowMultiSelect && candidates.Length > 1 || candidates.Any(item => !runtimeTargets[index].AllowedType.IsInstanceOfType(item)))
                return new VisualIssue(VisualIssueSeverity.Error, "EXISTING_SIGNAL_OWNER_NOT_UNIQUE",
                    $"Vorhandenes Signalziel '{container.Name}' ({runtimeTargets[index].AllowedType.Name}) fehlt oder ist nicht eindeutig. Einen konkreten Treffer zuordnen.", container.Id);
            runtimeTargets[index].AssignObjects(candidates.ToList());
        }
        return null;
    }

    internal static async Task<IReadOnlyList<FeeLogic>> ReadExistingLogicsAsync(CancellationToken cancellationToken)
    {
        var guidStrings = (await Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(nameof(LogicObject)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (guidStrings.Length == 0)
            return [];

        var guids = guidStrings.Select(Guid.Parse).ToArray();
        var names = (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Name")).ToArray();
        var xml = (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(guidStrings)).ToArray();
        var definitions = await Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var definitionNames = definitions.ToDictionary(
            definition => Guid.Parse(definition.Guid),
            definition => definition.Name ?? string.Empty);

        var result = new List<FeeLogic>(guids.Length);
        for (var index = 0; index < guids.Length; index++)
        {
            var element = System.Xml.Linq.XElement.Parse(xml[index]);
            var persisted = Guid.TryParse(
                ReadXmlValue(element, "PersistedLogicGuid"),
                out var definitionGuid)
                ? definitionGuid
                : Guid.Empty;
            result.Add(new FeeLogic
            {
                Guid = guids[index],
                Name = Services.ApiInstance.XmlHelper.ConvertToString(names[index]),
                LogicDefinitionGuid = persisted,
                LogicDefinitionName = definitionNames.GetValueOrDefault(persisted, string.Empty),
            });
        }
        return result;
    }

    private static string? ReadXmlValue(System.Xml.Linq.XElement root, string name)
    {
        var attribute = root.DescendantsAndSelf().SelectMany(item => item.Attributes())
            .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(attribute?.Value))
            return attribute.Value.Trim();
        return root.DescendantsAndSelf()
            .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
    }

    internal static async Task<IReadOnlyList<FeeCabinetElement>> ReadExistingCabinetElementsAsync(
        CancellationToken cancellationToken)
    {
        var guidStrings = (await Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync("CabinetElement"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (guidStrings.Length == 0)
            return [];
        var names = (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Name")).ToArray();
        var definitions = (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Definition")).ToArray();
        return guidStrings.Select((guid, index) => new FeeCabinetElement
        {
            Guid = Guid.Parse(guid),
            Name = Services.ApiInstance.XmlHelper.ConvertToString(names[index]),
            ElementType = Services.ApiInstance.XmlHelper.ConvertToString(definitions[index]),
        }).ToArray();
    }

    private static VisualExecutionResult Failure(string message, string code) =>
        new(false, message, [new VisualIssue(VisualIssueSeverity.Error, code, message)]);
}

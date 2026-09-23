using FS.SDK.Scene.Objects;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Links variables from one explicitly selected existing interface to already
/// existing container objects. It never creates FEE objects or variables.
/// </summary>
internal sealed class ExistingSignalLinkAdapter(IVisualPlanLogger logger)
{
    public async Task<VisualExecutionResult> ExecuteAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyDictionary<string, FeeInterface> runtimeInterfaces,
        CancellationToken cancellationToken)
    {
        var selection = plan.ExistingInterfaceSelection;
        if (selection is null || !runtimeInterfaces.TryGetValue(selection.InterfaceGuid, out var selectedInterface))
            return Failure("Bitte ein vorhandenes Interface auswählen und FEE aktualisieren.", "SIGNAL_LINK_INTERFACE_REQUIRED");

        cancellationToken.ThrowIfCancellationRequested();
        var binding = RuntimeVisualPlanBinder.Bind(plan, runtimeObjects);
        if (!binding.Success)
            return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);

        var selectedBindings = binding.Containers
            .Where(item => plan.IsGenerationSelected(item.PlanNode.Id))
            .ToArray();
        var requests = selectedBindings
            .SelectMany(item => item.RuntimeContainer.EnumerateAssignedSignals().Select(signal =>
                new SignalResolutionRequest(item.PlanNode.Id, item.PlanNode.Name, signal)))
            .Concat(binding.UnknownSignals.Select(signal =>
                new SignalResolutionRequest("unknown-signals", "Unbekannte Signale", signal)))
            .ToArray();
        var signalPlan = SignalResolutionPlanner.Build(requests, [selectedInterface], plan.SignalAssignments);
        if (!signalPlan.IsValid)
            return new VisualExecutionResult(false, "Signale konnten nicht eindeutig aufgelöst werden.", signalPlan.Issues);
        if (signalPlan.MissingSignals.Count > 0)
        {
            var missingIssues = signalPlan.MissingSignals.Select(missing => new VisualIssue(
                VisualIssueSeverity.Error,
                "EXISTING_SIGNAL_MISSING",
                $"Signal '{missing.Signal.Tag}' ist im ausgewählten Interface nicht vorhanden. Es wurde nichts erzeugt.",
                missing.ContainerId)).ToArray();
            return new VisualExecutionResult(false, "Nicht alle Signale sind im ausgewählten Interface vorhanden.", missingIssues);
        }

        signalPlan.ApplyExistingBindings();
        var logicLookup = await ReadExistingLogicsAsync(cancellationToken);
        var cabinetLookup = await ReadExistingCabinetElementsAsync(cancellationToken);
        var issues = new List<VisualIssue>();
        var linkedContainers = 0;
        foreach (var item in selectedBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = item.RuntimeContainer;
            if (container is ILogicOwner or ILogicSimObjectOwner)
            {
                var expectedLogic = plan.Nodes.FirstOrDefault(node =>
                    node.ContainerId == item.PlanNode.Id && node.Kind == VisualNodeKind.Logic)?.Name;
                var candidates = logicLookup.Where(logic =>
                        string.Equals(logic.Name, container.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(expectedLogic) ||
                         string.Equals(logic.LogicDefinitionName, expectedLogic, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (candidates.Length != 1)
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        candidates.Length == 0 ? "EXISTING_LOGIC_MISSING" : "EXISTING_LOGIC_AMBIGUOUS",
                        candidates.Length == 0
                            ? $"Vorhandene Logik '{container.ComponentName}' ({expectedLogic}) wurde nicht gefunden."
                            : $"Logik '{container.ComponentName}' ({expectedLogic}) ist {candidates.Length}-mal vorhanden.",
                        item.PlanNode.Id));
                    continue;
                }

                if (!ContainerExistingObjectReuse.TryAssignLogic(container, candidates[0]))
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        "EXISTING_LOGIC_BIND_FAILED",
                        $"Vorhandene Logik '{container.ComponentName}' konnte dem Container nicht zugeordnet werden.",
                        item.PlanNode.Id));
                    continue;
                }
            }
            else if (container is ICabinetElementOwner)
            {
                var expectedType = ContainerExistingObjectReuse.GetExpectedCabinetElementType(container);
                var candidates = cabinetLookup.Where(element =>
                        string.Equals(element.Name, container.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(element.ElementType, expectedType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (candidates.Length != 1 ||
                    !ContainerExistingObjectReuse.TryAssignCabinetElement(container, candidates[0]))
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        candidates.Length == 0 ? "EXISTING_CABINET_ELEMENT_MISSING" : "EXISTING_CABINET_ELEMENT_AMBIGUOUS",
                        candidates.Length == 0
                            ? $"Vorhandenes CabinetElement '{container.ComponentName}' ({expectedType}) wurde nicht gefunden."
                            : $"CabinetElement '{container.ComponentName}' ({expectedType}) ist nicht eindeutig.",
                        item.PlanNode.Id));
                    continue;
                }
            }

            try
            {
                switch (container)
                {
                    case ILogicSimObjectOwner full:
                        await full.AssignSignalsAsync(selectedInterface);
                        break;
                    case ILogicOwner logic:
                        await logic.AssignSignalsAsync(selectedInterface);
                        break;
                    case ISimObjectOwner simObject:
                        await simObject.AssignSignalsAsync(selectedInterface);
                        break;
                    default:
                        // Known signal-only containers (SensorX) require no
                        // object-side slot assignment.
                        break;
                }
                linkedContainers++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Error($"Signale für '{container.ComponentName}' konnten nicht verknüpft werden.", exception);
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "EXISTING_SIGNAL_LINK_FAILED",
                    $"{container.ComponentName}: {exception.Message}",
                    item.PlanNode.Id));
            }
        }

        var success = issues.All(issue => issue.Severity != VisualIssueSeverity.Error);
        return new VisualExecutionResult(
            success,
            success
                ? $"Vorhandene Signale wurden für {linkedContainers} Container verknüpft; es wurden keine FEE-Objekte erzeugt."
                : "Einige vorhandene Signale konnten nicht verknüpft werden. Details stehen in der Validierung.",
            issues);
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

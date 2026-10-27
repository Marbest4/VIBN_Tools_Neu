using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Repairs individual endpoints without rerunning legacy object creation or parameter assignment.</summary>
internal sealed class ExistingSignalEndpointLinker(
    IReadOnlyList<VisualFeeSignalLink> links,
    IReadOnlyList<FeeAbstractObject> sceneObjects,
    IReadOnlyList<VisualFeeContainerObject> containerObjects,
    IReadOnlySet<Guid> variableGuids,
    IVisualPlanLogger logger)
{
    private readonly HashSet<(Guid Variable, Guid Object, string Slot)> _completed = new();
    private readonly HashSet<VisualFeeSignalLink> _knownLinks = new(links);
    private readonly Dictionary<string, HashSet<VisualFeeSignalLink>> _directLinksByVariable = links.Where(link => !link.IsIndirect)
        .GroupBy(link => link.SignalGuidString, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToHashSet(), StringComparer.OrdinalIgnoreCase);
    private readonly List<(FeeAbstractObject Object, string ContainerId)> _createdObjects = [];
    private readonly HashSet<string> _scopedObjectGuids = sceneObjects.Select(item => item.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<(Guid ObjectGuid, string[] SlotNames)>> _variableAssignments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(Guid Variable, Guid Object, string Slot)> _linkedEndpoints = links
        .Where(link => Guid.TryParse(link.SignalGuidString, out _) && Guid.TryParse(link.ObjectGuidString, out _))
        .Select(link => (Guid.Parse(link.SignalGuidString), Guid.Parse(link.ObjectGuidString), link.SlotName.ToUpperInvariant())).ToHashSet();
    internal IReadOnlyList<VisualFeeSignalLink> KnownLinks => _knownLinks.ToArray();
    internal IReadOnlyList<(FeeAbstractObject Object, string ContainerId)> CreatedObjects => _createdObjects;
    public int LinkedCount { get; private set; }
    public int SkippedCount { get; private set; }

    internal async Task<FeeAbstractObject?> ResolvePrimaryHelperAsync(
        BoundVisualContainer bound, IReadOnlyList<SignalResolutionRequest> requests,
        FeeAbstractObject? parent, bool parentIsAmbiguous, List<VisualIssue> issues, CancellationToken cancellationToken)
    {
        var type = bound.RuntimeContainer is ContainerToFee.General.SimpleNot_Container ? typeof(FeeSimpleNot) : typeof(FeeSimpleMove);
        var requestGuids = requests.Select(request => request.Signal.Guid.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectedGuids = requestGuids.SelectMany(DirectLinks)
            .Select(link => link.ObjectGuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connected = sceneObjects.Where(item => type.IsInstanceOfType(item) && connectedGuids.Contains(item.GuidString)).ToArray();
        var identified = containerObjects.Where(item => item.Kind == VisualFeeContainerObjectKind.TechnicalHelper &&
                VisualFeeTechnicalHelperResolver.MatchesIdentity(item, bound.PlanNode))
            .Select(item => item.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (connected.Any(item => !identified.Contains(item.GuidString) && !string.IsNullOrWhiteSpace(item.Name) &&
                !string.Equals(item.Name, bound.PlanNode.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Warn(issues, bound.PlanNode.Id, "TECHNICAL_HELPER_SKIPPED",
                $"{bound.PlanNode.Name}: vorhandene Signalroute führt zu einem anders benannten Hilfsobjekt; mit Warnung übersprungen und nicht umgehängt.");
            return null;
        }
        var candidates = connected.Length > 0 ? connected : sceneObjects.Where(item => type.IsInstanceOfType(item) &&
            (identified.Contains(item.GuidString) || string.Equals(item.Name, bound.PlanNode.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1 || parentIsAmbiguous)
        {
            Warn(issues, bound.PlanNode.Id, "TECHNICAL_HELPER_SKIPPED",
                $"{bound.PlanNode.Name}: technisches Hilfsobjekt oder Ziel-Root ist nicht eindeutig; mit Warnung übersprungen. Bitte Zuordnung bzw. FEE-Root prüfen.");
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        FeeAbstractObject helper = type == typeof(FeeSimpleNot) ? new FeeSimpleNot() : new FeeSimpleMove();
        helper.Name = bound.PlanNode.Name;
        helper.Parent = parent;
        if (!await helper.CreateAsync() || !await helper.SendAndWaitAsync())
        {
            Warn(issues, bound.PlanNode.Id, "TECHNICAL_HELPER_SKIPPED",
                $"{bound.PlanNode.Name}: FEE hat das neue Hilfsobjekt nicht bestätigt; mit Warnung übersprungen.");
            return null;
        }
        _createdObjects.Add((helper, bound.PlanNode.Id));
        _scopedObjectGuids.Add(helper.GuidString);
        await ContainerObjectProvenance.WriteNewObjectAsync(helper, bound.RuntimeContainer);
        logger.Information($"Fehlendes technisches Hilfsobjekt {helper.FeeType} für '{bound.PlanNode.Name}' erzeugt.");
        return helper;
    }

    internal async Task LinkAsync(BoundVisualContainer bound, IReadOnlyList<SignalResolutionRequest> requests,
        IReadOnlyList<FeeAbstractObject> targets, List<VisualIssue> issues, CancellationToken cancellationToken)
    {
        var groups = requests.Where(request => !string.IsNullOrWhiteSpace(request.SlotName))
            .GroupBy(request => ContainerSignalSlotMap.RuntimeSlot(bound.RuntimeContainer, bound.PlanNode.TypeName, request.SlotName!),
                StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var entries = group.DistinctBy(request => request.Signal.Guid).ToArray();
            var fanIn = targets.Any(target => target is FeeLogic) && (entries.Length > 1 || entries.Any(request =>
                bound.RuntimeContainer.SlotAssignment.TryGetValue(request.SlotName!, out var property) &&
                property?.PropertyType == typeof(List<FeeInterfaceSignal>)));
            foreach (var target in targets)
                foreach (var request in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (request.Signal.Guid, target.Guid, group.Key.ToUpperInvariant());
                    if (_completed.Contains(key) || _linkedEndpoints.Contains(key))
                    { SkippedCount++; continue; }
                    if (target.Slots?.TryGetValue(group.Key, out var occupiedGuid) == true &&
                        variableGuids.Contains(occupiedGuid) && occupiedGuid != request.Signal.Guid)
                    {
                        Warn(issues, request.NodeId ?? bound.PlanNode.Id, "EXISTING_SIGNAL_SLOT_OCCUPIED",
                            $"{bound.PlanNode.Name}/{group.Key}: bereits mit einer anderen Variable verbunden; vorhandene Verbindung bleibt erhalten.");
                        continue;
                    }
                    if (fanIn)
                    {
                        if (!await LinkFanInAsync(bound, request, target, group.Key, issues, cancellationToken)) continue;
                    }
                    else
                        await ContainerSlotLinkService.AssignVariableAndVerifyAsync(target.Guid, group.Key, request.Signal.Guid,
                            $"{bound.PlanNode.Name}: {group.Key}", cancellationToken, CurrentAssignments(request.Signal.GuidString));
                    _completed.Add(key);
                    _linkedEndpoints.Add(key);
                    Remember(new VisualFeeSignalLink(request.Signal.GuidString, target.GuidString,
                        target.FeeType ?? "", group.Key, fanIn));
                    LinkedCount++;
                }
        }
    }

    private async Task<bool> LinkFanInAsync(BoundVisualContainer bound, SignalResolutionRequest request,
        FeeAbstractObject target, string slot, List<VisualIssue> issues, CancellationToken cancellationToken)
    {
        var outputs = DirectLinks(request.Signal.GuidString).Where(link => _scopedObjectGuids.Contains(link.ObjectGuidString) &&
                string.Equals(link.SlotName, "Output 01", StringComparison.OrdinalIgnoreCase) &&
                VisualFeeTechnicalHelperResolver.IsExpectedType("Move-Bit-Hilfslogik", link.ObjectType))
            .Select(link => Guid.Parse(link.ObjectGuidString)).Distinct().ToArray();
        if (outputs.Length > 1)
        {
            Warn(issues, request.NodeId ?? bound.PlanNode.Id, "TECHNICAL_HELPER_SKIPPED",
                $"{request.Signal.Tag}: mehrere MoveBit-Routen vorhanden; mit Warnung übersprungen.");
            return false;
        }
        Guid moveGuid;
        if (outputs.Length == 1) moveGuid = outputs[0];
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = new FeeSimpleMove { Name = $"{bound.PlanNode.Name} {slot} {request.Signal.Tag}", Parent = target.Parent };
            if (!await move.CreateAsync() || !await move.SendAndWaitAsync())
            {
                Warn(issues, bound.PlanNode.Id, "TECHNICAL_HELPER_SKIPPED",
                    $"{bound.PlanNode.Name}: FEE hat die neue MoveBit-Route nicht bestätigt; mit Warnung übersprungen.");
                return false;
            }
            _createdObjects.Add((move, bound.PlanNode.Id));
            _scopedObjectGuids.Add(move.GuidString);
            await ContainerObjectProvenance.WriteNewObjectAsync(move, bound.RuntimeContainer);
            await ContainerSlotLinkService.AssignVariableAndVerifyAsync(move.Guid, "Output 01", request.Signal.Guid,
                $"{bound.PlanNode.Name}: MoveBit Output 01", cancellationToken, CurrentAssignments(request.Signal.GuidString));
            Remember(new VisualFeeSignalLink(request.Signal.GuidString, move.GuidString,
                move.FeeType ?? "MoveBit", "Output 01", false));
            moveGuid = move.Guid;
        }
        await ContainerSlotLinkService.AssignAndVerifyAsync(
            [(target.Guid, slot), (moveGuid, "Input 01")], $"{bound.PlanNode.Name}: fehlende MoveBit-Route {slot}", cancellationToken);
        return true;
    }

    private void Remember(VisualFeeSignalLink link)
    {
        _knownLinks.Add(link);
        if (!link.IsIndirect)
        {
            if (!_directLinksByVariable.TryGetValue(link.SignalGuidString, out var direct))
                _directLinksByVariable[link.SignalGuidString] = direct = [];
            direct.Add(link);
            _variableAssignments.Remove(link.SignalGuidString);
        }
    }

    private IEnumerable<VisualFeeSignalLink> DirectLinks(string variableGuid) =>
        _directLinksByVariable.TryGetValue(variableGuid, out var direct) ? direct : [];

    private IReadOnlyList<(Guid ObjectGuid, string[] SlotNames)> CurrentAssignments(string variableGuid) =>
        _variableAssignments.TryGetValue(variableGuid, out var cached) ? cached :
        _variableAssignments[variableGuid] = DirectLinks(variableGuid).Where(link => Guid.TryParse(link.ObjectGuidString, out _))
            .GroupBy(link => Guid.Parse(link.ObjectGuidString))
            .Select(group => (group.Key, group.Select(link => link.SlotName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();

    private void Warn(List<VisualIssue> issues, string nodeId, string code, string message)
    {
        issues.Add(new VisualIssue(VisualIssueSeverity.Warning, code, message, nodeId));
        logger.Warning(message);
    }
}

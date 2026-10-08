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
    public int LinkedCount { get; private set; }
    public int SkippedCount { get; private set; }

    internal async Task<FeeAbstractObject?> ResolvePrimaryHelperAsync(
        BoundVisualContainer bound, IReadOnlyList<SignalResolutionRequest> requests,
        FeeAbstractObject? parent, bool parentIsAmbiguous, List<VisualIssue> issues, CancellationToken cancellationToken)
    {
        var type = bound.RuntimeContainer is ContainerToFee.General.SimpleNot_Container ? typeof(FeeSimpleNot) : typeof(FeeSimpleMove);
        var requestGuids = requests.Select(request => request.Signal.Guid.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectedGuids = links.Where(link => requestGuids.Contains(link.SignalGuidString) && !link.IsIndirect)
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
        await helper.CreateAsync();
        await helper.SendAndWaitAsync();
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
                    if (_completed.Contains(key) || links.Any(link =>
                            string.Equals(link.SignalGuidString, request.Signal.GuidString, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(link.ObjectGuidString, target.GuidString, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(link.SlotName, group.Key, StringComparison.OrdinalIgnoreCase)))
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
                            $"{bound.PlanNode.Name}: {group.Key}", cancellationToken);
                    _completed.Add(key);
                    LinkedCount++;
                }
        }
    }

    private async Task<bool> LinkFanInAsync(BoundVisualContainer bound, SignalResolutionRequest request,
        FeeAbstractObject target, string slot, List<VisualIssue> issues, CancellationToken cancellationToken)
    {
        var outputs = links.Where(link => !link.IsIndirect &&
                string.Equals(link.SignalGuidString, request.Signal.GuidString, StringComparison.OrdinalIgnoreCase) &&
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
            var move = new FeeSimpleMove { Name = $"{bound.PlanNode.Name} {slot} {request.Signal.Tag}", Parent = target.Parent };
            await move.CreateAsync();
            await move.SendAndWaitAsync();
            await ContainerObjectProvenance.WriteNewObjectAsync(move, bound.RuntimeContainer);
            await ContainerSlotLinkService.AssignVariableAndVerifyAsync(move.Guid, "Output 01", request.Signal.Guid,
                $"{bound.PlanNode.Name}: MoveBit Output 01", cancellationToken);
            moveGuid = move.Guid;
        }
        await ContainerSlotLinkService.AssignAndVerifyAsync(
            [(target.Guid, slot), (moveGuid, "Input 01")], $"{bound.PlanNode.Name}: fehlende MoveBit-Route {slot}", cancellationToken);
        return true;
    }

    private void Warn(List<VisualIssue> issues, string nodeId, string code, string message)
    {
        issues.Add(new VisualIssue(VisualIssueSeverity.Warning, code, message, nodeId));
        logger.Warning(message);
    }
}

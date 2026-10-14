using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.Core.Containers;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeContainerLiveObject(
    Guid Guid,
    string Name,
    string FeeType,
    string? LogicDefinitionName = null,
    string? CabinetDefinition = null,
    string? Label = null,
    string? ProvenanceContainerId = null,
    string? ProvenanceContainerType = null,
    Guid? ParentGuid = null,
    IReadOnlyList<Guid>? LinkedObjectGuids = null,
    bool IsSpecialDevice = false);

public sealed record FeeContainerLiveVariable(
    Guid VariableGuid,
    string Signal,
    string Address,
    string Path,
    string DataType,
    string Comment,
    string Usage = "", string InterfaceGuid = "", string InterfaceName = "");

public sealed record FeeContainerLiveAssignment(
    Guid VariableGuid,
    Guid TargetObjectGuid,
    string TargetSlot);

public sealed record FeeContainerReconstructionIssue(
    Guid? ObjectGuid,
    string Message);

public sealed record FeeContainerUnmappedObject(
    Guid Guid,
    string Name,
    string FeeType,
    string Reason);

public sealed record FeeContainerObjectAssociation(
    Guid ObjectGuid,
    string ObjectName,
    string ObjectType,
    Guid ContainerObjectGuid,
    string ContainerId,
    string Reason,
    string Role = "SimObject",
    string ProvenanceContainerId = "",
    bool IsManual = false,
    string ParentName = "",
    string LogicDefinitionName = "",
    string ConnectionText = "");

public sealed record FeeContainerReconstructionResult(
    FeeContainerProvenanceSnapshot Snapshot,
    int InspectedObjectCount,
    int IgnoredObjectCount,
    IReadOnlyList<FeeContainerReconstructionIssue> Issues,
    IReadOnlyList<FeeContainerUnmappedObject> UnmappedObjects,
    IReadOnlyList<FeeContainerObjectAssociation> ObjectAssociations);

/// <summary>
/// Reconstructs the container schema from a bounded FEE subtree. Exact
/// Current definitions, names and endpoints are authoritative. Provenance only
/// disambiguates compatible type aliases; it cannot resurrect old signals.
/// </summary>
public static class FeeContainerLiveReconstructor
{
    /// <summary>
    /// Complete XML type set understood by the same catalog that drives
    /// Container2FEE Visual. FEE2Container uses this as its round-trip
    /// contract, including signal-only types retained by root provenance.
    /// </summary>
    public static IReadOnlyList<string> SupportedContainerTypes =>
        ContainerMetadataCatalog.SupportedXmlTypes.Concat(["N/A"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static FeeContainerReconstructionResult Reconstruct(
        Guid rootGuid,
        string rootName,
        IEnumerable<FeeContainerLiveObject> objects,
        IEnumerable<FeeContainerLiveVariable> variables,
        IEnumerable<FeeContainerLiveAssignment> assignments,
        ContainerRequirementsCatalog? requirements = null, bool requireConnectedSignal = false)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(assignments);

        var sourceObjects = objects.Where(item => item.Guid != Guid.Empty && !FeeContainerReadPolicy.IsIgnored(item.FeeType, item.Name))
            .DistinctBy(item => item.Guid).ToArray();
        var variableByGuid = variables
            .Where(item => item.VariableGuid != Guid.Empty)
            .GroupBy(item => item.VariableGuid)
            .ToDictionary(group => group.Key, group => group.First());
        var assignmentsByObject = assignments
            .Where(item => item.TargetObjectGuid != Guid.Empty && item.VariableGuid != Guid.Empty)
            .GroupBy(item => item.TargetObjectGuid)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var issues = new List<FeeContainerReconstructionIssue>();
        var candidates = new List<ContainerCandidate>();
        var candidateWarnings = new Dictionary<Guid, string>();
        var relevantObjectGuids = new HashSet<Guid>();

        foreach (var item in sourceObjects)
        {
            // Cabinets stay members. Connected elementary operators are
            // classified separately below, without guessing a business type.
            if (EndsWithType(item.FeeType, "Cabinet") || IsHelper(item.FeeType) && !EndsWithType(item.FeeType, "BoolNot"))
                continue;
            if (item.IsSpecialDevice && !string.IsNullOrWhiteSpace(item.LogicDefinitionName))
            {
                candidates.Add(new ContainerCandidate(item, item.LogicDefinitionName, item.LogicDefinitionName, item.Name));
                relevantObjectGuids.Add(item.Guid);
                continue;
            }
            var liveSlots = (assignmentsByObject.GetValueOrDefault(item.Guid) ?? []).Select(assignment => assignment.TargetSlot).ToArray();
            var liveSignals = (assignmentsByObject.GetValueOrDefault(item.Guid) ?? []).Select(assignment => variableByGuid.GetValueOrDefault(assignment.VariableGuid)?.Signal)
                .Where(signal => signal is not null).Cast<string>().ToArray();
            if (!TryCreateCandidate(item, liveSlots, liveSignals, requirements, out var candidate, out var ambiguity))
                continue;
            candidates.Add(candidate!);
            relevantObjectGuids.Add(item.Guid);
            if (!string.IsNullOrWhiteSpace(ambiguity))
                candidateWarnings[item.Guid] = ambiguity;
        }

        // Historical owner IDs must never collapse primary objects that have
        // since been renamed or changed type. Live logic/cabinet/button GUIDs
        // remain distinct; same-name ancillary sensors belong to their logic.
        var byGuid = sourceObjects.ToDictionary(item => item.Guid);
        // A directly connected, otherwise standalone operator is a signal
        // container. Operators already routed to another primary remain members.
        var primaryGuids = candidates.Select(item => item.Object.Guid).ToHashSet();
        foreach (var item in sourceObjects.Where(item => IsHelper(item.FeeType) && !primaryGuids.Contains(item.Guid) &&
            (assignmentsByObject.GetValueOrDefault(item.Guid)?.Length ?? 0) > 0))
        {
            var pending = new Queue<Guid>(); pending.Enqueue(item.Guid);
            var visited = new HashSet<Guid>(); var attached = false;
            while (pending.TryDequeue(out var guid) && !attached)
            {
                if (!visited.Add(guid) || !byGuid.TryGetValue(guid, out var current)) continue;
                foreach (var peer in current.LinkedObjectGuids ?? [])
                    if (primaryGuids.Contains(peer)) { attached = true; break; }
                    else if (byGuid.TryGetValue(peer, out var neighbour) && IsHelper(neighbour.FeeType)) pending.Enqueue(peer);
            }
            var ownVariables = assignmentsByObject[item.Guid].Select(route => route.VariableGuid).ToHashSet();
            attached |= candidates.Any(candidate => (assignmentsByObject.GetValueOrDefault(candidate.Object.Guid) ?? []).Any(route => ownVariables.Contains(route.VariableGuid)));
            if (!attached) candidates.Add(new(item, "N/A", "N/A", item.Name));
        }
        candidates = candidates.Where(item => !IsRedundantLegacySimObject(item, candidates, byGuid)).ToList();
        issues.AddRange(candidates.Where(candidate => candidateWarnings.ContainsKey(candidate.Object.Guid))
            .Select(candidate => new FeeContainerReconstructionIssue(candidate.Object.Guid, candidateWarnings[candidate.Object.Guid])));

        var objectAssociations = ResolveAllObjectAssociations(sourceObjects, candidates, assignmentsByObject, issues)
            .Select(item => byGuid.GetValueOrDefault(item.ContainerObjectGuid)?.IsSpecialDevice == true && item.ObjectGuid == item.ContainerObjectGuid
                ? item with { Role = "SpecialDevice" } : item).ToArray();
        relevantObjectGuids.Clear();
        relevantObjectGuids.UnionWith(objectAssociations.Select(item => item.ObjectGuid));

        var containerElements = new List<XElement>();
        var bindings = new List<FeeContainerSignalBinding>();
        foreach (var candidate in candidates
                     .OrderBy(item => item.ComponentName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.XmlType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Object.Guid))
        {
            var members = objectAssociations.Where(item => item.ContainerObjectGuid == candidate.Object.Guid).ToArray();
            // A cabinet's displayed label or a technical helper's name can
            // differ from the component. They still carry real signal routes,
            // but that does not authorize a differently named SimObject link.
            var signalObjects = members.Select(item => item.ObjectGuid).ToHashSet();
            signalObjects.Add(candidate.Object.Guid);
            var objectAssignments = signalObjects.SelectMany(guid => assignmentsByObject.GetValueOrDefault(guid) ?? [])
                .Distinct().ToArray();
            var primaryVariables = objectAssignments.Where(assignment => assignment.TargetObjectGuid == candidate.Object.Guid)
                .Select(assignment => assignment.VariableGuid).ToHashSet();
            var resolved = objectAssignments.Where(assignment => assignment.TargetObjectGuid == candidate.Object.Guid ||
                    !primaryVariables.Contains(assignment.VariableGuid) || !IsHelper(byGuid.GetValueOrDefault(assignment.TargetObjectGuid)?.FeeType ?? ""))
                .Select(assignment => TryCreateEntry(candidate, assignment, variableByGuid, requirements, issues))
                .Where(item => item is not null)
                .Cast<ResolvedEntry>()
                .GroupBy(item => (item.Variable.VariableGuid, item.XmlSlot))
                .Select(group => group.First())
                .OrderBy(item => item.XmlSlot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Variable.Signal, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var componentName = candidate.ComponentName;
            if (requireConnectedSignal && resolved.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = resolved.FirstOrDefault()?.Variable.Signal;
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = $"FEE-Objekt {candidate.Object.Guid:D}";

            if (resolved.Length == 0)
            {
                issues.Add(new FeeContainerReconstructionIssue(
                    candidate.Object.Guid,
                    $"'{componentName}' wurde als {candidate.XmlType} erkannt, besitzt aber keine " +
                    "eindeutig rücklesbare Variablenzuordnung. Der Container wird mit einem deutlich " +
                    "markierten, unzugeordneten Prüfeintrag exportiert."));
            }

            var dataList = new XElement("DataList");
            if (requirements?.Contains(candidate.XmlType) == true)
            {
                var limits = requirements.SignalLimits(candidate.XmlType);
                if (limits.Min is { } min && resolved.Length < min || limits.Max is { } max && resolved.Length > max)
                    issues.Add(new FeeContainerReconstructionIssue(candidate.Object.Guid,
                        $"'{componentName}' besitzt {resolved.Length} Signalzuordnung(en); Req-XML erwartet {limits.Min?.ToString() ?? "0"} bis {limits.Max?.ToString() ?? "unbegrenzt"}."));
            }
            var containerIndex = containerElements.Count;
            if (resolved.Length == 0)
            {
                // CAAResult.xsd requires at least one Entry. A placeholder keeps
                // an older, structurally recognized FEE container visible for
                // comparison without inventing a signal or slot assignment.
                dataList.Add(new XElement("Entry",
                    new XElement("ID", $"FEE-UNASSIGNED-{candidate.Object.Guid:D}"),
                    new XElement("Address", string.Empty),
                    new XElement("DataType", string.Empty),
                    new XElement("Signal", string.Empty),
                    new XElement("Slot", string.Empty),
                    new XElement("Note",
                        $"PRÜFEN: Aus FEE rekonstruiert; Objekt {candidate.Object.Guid:D} besitzt keine rücklesbare Signalverknüpfung.")));
            }
            foreach (var entry in resolved)
            {
                var variable = entry.Variable;
                dataList.Add(new XElement("Entry",
                    new XElement("ID", variable.Comment ?? string.Empty),
                    new XElement("Address", candidate.Object.IsSpecialDevice && !string.IsNullOrWhiteSpace(variable.Address)
                        ? variable.Address
                        : string.IsNullOrWhiteSpace(variable.Path)
                        ? variable.Address ?? string.Empty
                        : variable.Path),
                    new XElement("DataType", variable.DataType ?? string.Empty),
                    new XElement("Signal", variable.Signal ?? string.Empty),
                    new XElement("Slot", entry.XmlSlot),
                    new XElement("Note", $"Aus FEE rekonstruiert; Objekt {candidate.Object.Guid:D}."),
                    new XAttribute("feeGuid", variable.VariableGuid.ToString("D"))));
                bindings.Add(new FeeContainerSignalBinding(
                    containerIndex,
                    dataList.Elements("Entry").Count() - 1,
                    variable.VariableGuid));
            }

            containerElements.Add(new XElement("Container",
                new XAttribute("id", $"fee:{candidate.Object.Guid:D}"),
                new XElement("Component", componentName),
                new XElement("Type", candidate.XmlType),
                dataList,
                new XElement("SimObjects", members.Select(item => ContainerFileXml.Object(
                    item.ObjectGuid.ToString("D"), item.ObjectName, item.ObjectType,
                    item.Role, assignmentKind: ContainerFileXml.IsStructuralObjectAssociation(item.ObjectType, item.Role) ? "Linked" : "Automatic",
                    logicDefinitionName: byGuid[item.ObjectGuid].LogicDefinitionName ?? "")))));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CAAMergeResult",
                new XAttribute("version", "1.0.0.0"),
                new XAttribute("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new XAttribute("autoCreateFile", string.Empty),
                new XAttribute("zuli", string.Empty),
                new XElement("ContainerList", containerElements)));
        var snapshot = new FeeContainerProvenanceSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            document,
            bindings,
            containerElements.Count,
            bindings.Count,
            string.Empty);
        var unmapped = sourceObjects
            .Where(item => !relevantObjectGuids.Contains(item.Guid) && !EndsWithType(item.FeeType, "Cabinet"))
            .Select(item => new FeeContainerUnmappedObject(
                item.Guid,
                item.Name,
                item.FeeType,
                item.IsSpecialDevice ? "SpecialDevice: separat in FEE2SpecialDevices einlesen." :
                    "Kein eindeutig gleichnamiger Container erkennbar; manuelle Zuordnung möglich."))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FeeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new FeeContainerReconstructionResult(
            snapshot,
            sourceObjects.Length,
            unmapped.Length,
            issues,
            unmapped,
            objectAssociations);
    }

    private static FeeContainerObjectAssociation[] ResolveAllObjectAssociations(
        IReadOnlyList<FeeContainerLiveObject> objects, IReadOnlyList<ContainerCandidate> candidates,
        IReadOnlyDictionary<Guid, FeeContainerLiveAssignment[]> assignments,
        ICollection<FeeContainerReconstructionIssue> issues)
    {
        var result = new List<FeeContainerObjectAssociation>();
        var byName = candidates.ToLookup(candidate => candidate.ComponentName.Trim(), StringComparer.OrdinalIgnoreCase);
        var byGuid = objects.ToDictionary(item => item.Guid);
        var byCandidateGuid = candidates.ToDictionary(item => item.Object.Guid);
        foreach (var item in objects)
        {
            if (EndsWithType(item.FeeType, "Cabinet") || IsHelper(item.FeeType) && !byCandidateGuid.ContainsKey(item.Guid))
                continue;
            ContainerCandidate[] matches = string.IsNullOrWhiteSpace(item.Name) ? [] : byName[item.Name.Trim()].ToArray();
            // Provenance disambiguates equal names but never invents an automatic
            // association between differently named physical scene objects.
            ContainerCandidate[] own = byCandidateGuid.TryGetValue(item.Guid, out var primary) ? [primary] : [];
            if (own.Length == 1) matches = own;
            else if (matches.Length > 1)
            {
                var related = matches.Where(candidate => item.LinkedObjectGuids?.Contains(candidate.Object.Guid) == true ||
                    IsDescendantOf(item, candidate.Object.Guid, byGuid)).ToArray();
                if (related.Length == 1) matches = related;
            }
            if (matches.Length == 1)
            {
                var container = matches[0];
                result.Add(new FeeContainerObjectAssociation(item.Guid, item.Name, item.FeeType,
                    container.Object.Guid, $"fee:{container.Object.Guid:D}",
                    item.Guid == container.Object.Guid ? "Primäres Containerobjekt" :
                    IsHelper(item.FeeType) ? "Technisches Hilfsobjekt" : "Automatisch über eindeutigen gleichen Namen zugeordnet",
                    item.Guid == container.Object.Guid ? "Primary" : IsHelper(item.FeeType) ? "TechnicalHelper" : "SimObject",
                    container.Object.ProvenanceContainerId ?? ""));
            }
            else if (matches.Length > 1)
                issues.Add(new FeeContainerReconstructionIssue(item.Guid,
                    $"'{item.Name}' ({item.FeeType}) passt zu mehreren Containern und bleibt zur manuellen Zuordnung verfügbar."));
        }

        // Traverse only helper chains. Do not walk through a container into
        // unrelated containers or infer physical SimObject ownership from links.
        var edges = objects.ToDictionary(item => item.Guid, _ => new HashSet<Guid>());
        foreach (var item in objects)
            foreach (var peer in item.LinkedObjectGuids ?? [])
                if (peer != item.Guid && edges.ContainsKey(peer))
                { edges[item.Guid].Add(peer); edges[peer].Add(item.Guid); }
        var ownersByObject = result.ToLookup(item => item.ObjectGuid, item => item.ContainerObjectGuid);
        var ownersByVariable = result.SelectMany(association => (assignments.GetValueOrDefault(association.ObjectGuid) ?? [])
                .Select(assignment => (assignment.VariableGuid, association.ContainerObjectGuid)))
            .ToLookup(item => item.VariableGuid, item => item.ContainerObjectGuid);
        var processed = new HashSet<Guid>();
        foreach (var helper in objects.Where(item => IsHelper(item.FeeType) && !byCandidateGuid.ContainsKey(item.Guid)))
        {
            if (processed.Contains(helper.Guid)) continue;
            var chain = new HashSet<Guid>();
            var owners = new HashSet<Guid>();
            var pending = new Queue<Guid>(); pending.Enqueue(helper.Guid);
            while (pending.TryDequeue(out var current))
            {
                if (!chain.Add(current)) continue;
                foreach (var peer in edges[current])
                {
                    if (IsHelper(byGuid[peer].FeeType) && !byCandidateGuid.ContainsKey(peer)) pending.Enqueue(peer);
                    else owners.UnionWith(ownersByObject[peer]);
                }
            }
            processed.UnionWith(chain);
            if (owners.Count == 0)
                foreach (var guid in chain)
                    foreach (var assignment in assignments.GetValueOrDefault(guid) ?? [])
                        owners.UnionWith(ownersByVariable[assignment.VariableGuid]);
            var linked = owners.Count > 0;
            foreach (var guid in chain)
            {
                var item = byGuid[guid];
                // Preserve the existing same-name fallback for older helpers.
                if (owners.Count == 0 && (EndsWithType(item.FeeType, "MoveBit") || EndsWithType(item.FeeType, "BoolNot")) &&
                    byName[item.Name.Trim()].ToArray() is { Length: 1 } named)
                    owners.Add(named[0].Object.Guid);
                foreach (var owner in owners)
                    AddMember(item, byCandidateGuid[owner], linked ? "Technisches Hilfsobjekt über aktuelle FEE-Verknüpfungen zugeordnet" :
                        "Älteres Hilfsobjekt über gleichen Containernamen zugeordnet");
            }
        }
        var cabinetOwners = new HashSet<(Guid Cabinet, Guid Owner)>();
        foreach (var association in result)
        {
            var visited = new HashSet<Guid>();
            var parent = byGuid[association.ObjectGuid].ParentGuid;
            while (parent.HasValue && visited.Add(parent.Value) && byGuid.TryGetValue(parent.Value, out var ancestor))
            {
                if (EndsWithType(ancestor.FeeType, "Cabinet")) cabinetOwners.Add((ancestor.Guid, association.ContainerObjectGuid));
                parent = ancestor.ParentGuid;
            }
        }
        foreach (var (cabinet, owner) in cabinetOwners)
            AddMember(byGuid[cabinet], byCandidateGuid[owner], "Cabinet als technisches Hilfsobjekt über die aktuelle Hierarchie zugeordnet");
        return result.ToArray();

        void AddMember(FeeContainerLiveObject item, ContainerCandidate owner, string reason) =>
            result.Add(new FeeContainerObjectAssociation(item.Guid, item.Name, item.FeeType, owner.Object.Guid,
                $"fee:{owner.Object.Guid:D}", reason, "TechnicalHelper", owner.Object.ProvenanceContainerId ?? ""));
    }

    private static bool IsDescendantOf(FeeContainerLiveObject item, Guid candidateGuid,
        IReadOnlyDictionary<Guid, FeeContainerLiveObject> byGuid)
    {
        var visited = new HashSet<Guid>();
        for (var parent = item.ParentGuid; parent.HasValue && visited.Add(parent.Value);)
        {
            if (parent.Value == candidateGuid) return true;
            parent = byGuid.GetValueOrDefault(parent.Value)?.ParentGuid;
        }
        return false;
    }

    private static bool IsHelper(string type) => !EndsWithType(type, "Cabinet") && ContainerFileXml.IsTechnicalHelperType(type);

    private static bool TryCreateCandidate(
        FeeContainerLiveObject item,
        IReadOnlyList<string> liveSlots,
        IReadOnlyList<string> liveSignals,
        ContainerRequirementsCatalog? requirements,
        out ContainerCandidate? candidate,
        out string? ambiguity)
    {
        candidate = null;
        ambiguity = null;
        var feeType = item.FeeType ?? string.Empty;
        var hint = item.ProvenanceContainerType switch
        { "Switch" => "CabinetSwitch", "Fuse" => "CabinetFuse", var type => type };
        string? xmlType = null;
        var logicMatches = ContainerMetadataCatalog.FindXmlTypesByLogicName(item.LogicDefinitionName);
        var configuredLogics = logicMatches.Select(type => requirements?.ConfiguredName(type)).Where(type => type is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (configuredLogics.Length > 0) logicMatches = configuredLogics;
        IReadOnlyList<string> signalMatches = logicMatches.Count > 1 ? requirements?.TypesMatchingSignals(liveSignals, logicMatches.ToArray()) ?? [] : [];
        var resolvedLogics = logicMatches.Where(type => signalMatches.Contains(type, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (resolvedLogics.Length == 1) logicMatches = resolvedLogics;
        if (logicMatches.Count > 0)
        {
            xmlType = logicMatches.FirstOrDefault(type => string.Equals(type, hint, StringComparison.OrdinalIgnoreCase)) ?? logicMatches[0];
            if (logicMatches.Count > 1 && !logicMatches.Contains(hint ?? "", StringComparer.OrdinalIgnoreCase))
                ambiguity = $"Die aktuelle Logik '{item.LogicDefinitionName}' passt zu {string.Join(" oder ", logicMatches)}; '{xmlType}' bitte prüfen.";
            else if (!string.IsNullOrWhiteSpace(hint) && !logicMatches.Contains(hint, StringComparer.OrdinalIgnoreCase))
                ambiguity = $"Veralteter Provenienztyp '{hint}' passt nicht zur aktuellen Logik; '{xmlType}' wird aus FEE rekonstruiert.";
        }
        else if (EndsWithType(feeType, "BoolNot"))
        {
            var compatibleTypes = new[] { "ReturnCircuit", "SafeArea" };
            var configured = compatibleTypes.Where(type => requirements?.Contains(type) == true).ToArray();
            if (configured.Length > 0) compatibleTypes = configured;
            IReadOnlyList<string> helperSignalMatches = compatibleTypes.Length > 1
                ? requirements?.TypesMatchingSignals(liveSignals, compatibleTypes) ?? [] : [];
            var resolvedHelpers = compatibleTypes.Where(type => helperSignalMatches.Contains(type, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (resolvedHelpers.Length == 1) compatibleTypes = resolvedHelpers;
            var compatibleHint = compatibleTypes.FirstOrDefault(type =>
                string.Equals(type, hint, StringComparison.OrdinalIgnoreCase));
            xmlType = compatibleHint ?? compatibleTypes[0];
            if (compatibleHint is null && compatibleTypes.Length > 1)
                ambiguity = "Ein BoolNot ist ohne passende Typinformation nicht sicher als ReturnCircuit oder SafeArea unterscheidbar; ReturnCircuit bitte prüfen.";
        }
        else if (xmlType is null && EndsWithType(feeType, "Button"))
        {
            xmlType = "Button";
        }
        else if (xmlType is null && EndsWithType(feeType, "SegmentedLamp"))
        {
            xmlType = "Stacklight";
        }
        else if (xmlType is null && EndsWithType(feeType, "CabinetElement"))
        {
            xmlType = MapCabinetType(item.CabinetDefinition);
            if (xmlType is null) xmlType = "N/A";
        }
        else if (xmlType is null &&
                 (EndsWithType(feeType, "Sensor") || EndsWithType(feeType, "SafetySensor")))
        {
            xmlType = "Sensor";
            ambiguity = "Älteres Sensorobjekt ohne Container-Provenienz wurde anhand seines FEE-Typs als Sensor-Container rekonstruiert.";
        }
        else if (xmlType is null && EndsWithType(feeType, "Floor"))
        {
            xmlType = "Stop";
            ambiguity = "Älteres Floor-Objekt ohne Container-Provenienz wurde als Stop-Container rekonstruiert. " +
                        "Die Zuordnung ist zu prüfen, falls das Floor keiner Stopperlogik angehört.";
        }

        var runtimeType = xmlType;
        if (xmlType is not null && requirements?.ConfiguredName(xmlType) is { } configuredName)
            xmlType = configuredName;
        else if (requirements is not null)
        {
            IReadOnlyList<string> slotMatches = EndsWithType(feeType, "CabinetElement") && xmlType is not null ? [] : requirements.TypesMatchingSlots(liveSlots);
            if (slotMatches.Count == 1)
            {
                xmlType = slotMatches[0];
                ambiguity = $"'{item.Name}' wurde anhand aktueller FEE-Slots als Req-XML-Typ '{xmlType}' erkannt; Definition und Zuordnung bitte prüfen.";
            }
            else if (xmlType is not null)
                ambiguity = $"Typ '{xmlType}' ist nicht in der geladenen Req-XML definiert; Erkennung über die aktuelle FEE-Definition bitte prüfen.";
        }
        if (xmlType is null && EndsWithType(feeType, "LogicObject") &&
            (!string.IsNullOrWhiteSpace(item.LogicDefinitionName) && !string.Equals(item.LogicDefinitionName, "UNDEFINED", StringComparison.OrdinalIgnoreCase) || liveSlots.Count > 0))
        {
            xmlType = "N/A"; runtimeType = "N/A";
            ambiguity = $"Die aktuelle Logik '{item.LogicDefinitionName}' besitzt noch keine Containertypdefinition. Typ N/A erhält das LogicObject und seine aktuellen Signalverknüpfungen.";
        }
        if (xmlType is null || xmlType != "N/A" && !EndsWithType(feeType, "CabinetElement") &&
            !ContainerMetadataCatalog.TryGet(runtimeType ?? xmlType, out _) && requirements?.Contains(xmlType) != true)
            return false;
        var componentName = EndsWithType(feeType, "CabinetElement")
            ? FirstNotBlank(item.Label, item.Name?.Split(';')[0])
            : item.Name;
        candidate = new ContainerCandidate(item, xmlType, runtimeType ?? xmlType, componentName ?? string.Empty);
        return true;
    }

    private static ResolvedEntry? TryCreateEntry(
        ContainerCandidate candidate,
        FeeContainerLiveAssignment assignment,
        IReadOnlyDictionary<Guid, FeeContainerLiveVariable> variables,
        ContainerRequirementsCatalog? requirements,
        ICollection<FeeContainerReconstructionIssue> issues)
    {
        if (!variables.TryGetValue(assignment.VariableGuid, out var variable))
            return null;
        var runtimeSlot = assignment.TargetSlot?.Trim() ?? "";
        if (candidate.Object.IsSpecialDevice || candidate.XmlType == "N/A")
            return runtimeSlot.Length == 0 ? null : new ResolvedEntry(variable, runtimeSlot);
        var allowed = requirements?.Slots(candidate.XmlType) ?? [];
        var slot = allowed.FirstOrDefault(slot => string.Equals(slot, runtimeSlot, StringComparison.OrdinalIgnoreCase)) ??
            MapRuntimeSlot(candidate.RuntimeType, runtimeSlot);
        if (slot is not null && allowed.Count > 0 && !allowed.Contains(slot, StringComparer.OrdinalIgnoreCase) &&
            ContainerMetadataCatalog.TryGet(candidate.RuntimeType, out var descriptor))
        {
            var runtime = descriptor.Factory();
            var mapped = ContainerSignalSlotMap.RuntimeSlot(runtime, candidate.RuntimeType, slot);
            slot = allowed.FirstOrDefault(configured => string.Equals(
                ContainerSignalSlotMap.RuntimeSlot(runtime, candidate.RuntimeType, configured), mapped, StringComparison.OrdinalIgnoreCase)) ?? slot;
        }
        if (string.IsNullOrWhiteSpace(slot) && !string.IsNullOrWhiteSpace(assignment.TargetSlot))
        {
            slot = assignment.TargetSlot.Trim();
            issues.Add(new FeeContainerReconstructionIssue(candidate.Object.Guid,
                $"Signal '{variable.Signal}': tatsächlicher FEE-Slot '{slot}' ist für '{candidate.XmlType}' nicht eindeutig abbildbar; Originalzuordnung bleibt zur Prüfung erhalten."));
        }
        if (requirements?.Contains(candidate.XmlType) == true && slot is not null && !allowed.Contains(slot, StringComparer.OrdinalIgnoreCase))
            issues.Add(new FeeContainerReconstructionIssue(candidate.Object.Guid,
                $"Signal '{variable.Signal}': Slot '{slot}' ist nicht in der Req-XML für '{candidate.XmlType}' definiert; aktuelle FEE-Verknüpfung bitte prüfen."));
        return string.IsNullOrWhiteSpace(slot) ? null : new ResolvedEntry(variable, slot);
    }

    internal static string? MapRuntimeSlot(string xmlType, string runtimeSlot)
    {
        if (string.IsNullOrWhiteSpace(runtimeSlot))
            return null;
        if (!ContainerMetadataCatalog.TryGet(xmlType, out var descriptor)) return null;
        var directSlot = descriptor.Slots.FirstOrDefault(slot =>
            string.Equals(slot, runtimeSlot, StringComparison.OrdinalIgnoreCase));
        if (directSlot is not null)
            return directSlot;

        var normalizedSlot = runtimeSlot.Trim().ToUpperInvariant();

        return xmlType switch
        {
            "Button" => normalizedSlot switch
            {
                "PRESSED" => "PLC_IN_NO",
                "PRESSEDINVERTED" => "PLC_IN_NC",
                _ => null,
            },
            "Stacklight" => normalizedSlot switch
            {
                "RED" => "PLC_NO_Red",
                "YELLOW" => "PLC_NO_Yellow",
                "GREEN" => "PLC_NO_Green",
                "BLUE" => "PLC_NO_Blue",
                "WHITE" => "PLC_NO_White",
                _ => null,
            },
            "ReturnCircuit" or "SafeArea" => normalizedSlot switch
            {
                "INPUT 01" => "PLC_OUT_Signal",
                "OUTPUT 01" => "PLC_IN_Signal",
                _ => null,
            },
            "Switch" or "CabinetSwitch" or "EStop" => normalizedSlot switch
            {
                "NO1" => "PLC_IN_NO1",
                "NO2" => "PLC_IN_NO2",
                "NC1" => "PLC_IN_NC1",
                "NC2" => "PLC_IN_NC2",
                _ => null,
            },
            "Fuse" or "CabinetFuse" => normalizedSlot switch
            {
                "NO" => "PLC_IN_NO",
                "NC" => "PLC_IN_NC",
                _ => null,
            },
            "CabinetLamp" when normalizedSlot == "ON" => "PLC_OUT_ON",
            "Sensor" => normalizedSlot switch
            {
                "CHANNEL1" => "PLC_IN_PartPresent",
                "CHANNEL2" => "PLC_IN_NoPartPresent",
                _ => null,
            },
            _ => null,
        };
    }

    private static bool IsRedundantLegacySimObject(
        ContainerCandidate candidate,
        IEnumerable<ContainerCandidate> allCandidates,
        IReadOnlyDictionary<Guid, FeeContainerLiveObject> byGuid)
    {
        if (!EndsWithType(candidate.Object.FeeType, "Sensor") &&
            !EndsWithType(candidate.Object.FeeType, "SafetySensor") &&
            !EndsWithType(candidate.Object.FeeType, "Floor") && !IsHelper(candidate.Object.FeeType))
            return false;
        return allCandidates.Any(other =>
            other.Object.Guid != candidate.Object.Guid &&
            !IsHelper(other.Object.FeeType) &&
            (candidate.Object.LinkedObjectGuids?.Contains(other.Object.Guid) == true &&
                (!IsHelper(candidate.Object.FeeType) || ContainerFileXml.HasMatchingObjectName(candidate.ComponentName, other.ComponentName)) ||
             IsDescendantOf(candidate.Object, other.Object.Guid, byGuid) ||
             !IsHelper(candidate.Object.FeeType) && !string.IsNullOrWhiteSpace(other.Object.LogicDefinitionName) &&
             ContainerFileXml.HasMatchingObjectName(other.ComponentName, candidate.ComponentName)));
    }

    private static string? MapCabinetType(string? definition)
    {
        var normalized = NormalizeToken(definition);
        if (normalized.Contains("GROBNOTAUS", StringComparison.Ordinal))
            return "EStop";
        if (normalized.Contains("FUSE", StringComparison.Ordinal))
            return "CabinetFuse";
        if (normalized.Contains("LAMP", StringComparison.Ordinal))
            return "CabinetLamp";
        if (normalized.Contains("GROB2POSITIONSWITCH", StringComparison.Ordinal) ||
            normalized.Contains("GROB3POSITIONSWITCH", StringComparison.Ordinal) ||
            normalized.Contains("POSITIONSWITCH2", StringComparison.Ordinal) ||
            normalized.Contains("2POSITIONSWITCH", StringComparison.Ordinal) ||
            normalized.Contains("TWOPOSITIONSWITCH", StringComparison.Ordinal) ||
            normalized == "CABINETSWITCH")
            return "CabinetSwitch";
        if (normalized.Contains("BUTTON", StringComparison.Ordinal)) return "CabinetButton";
        if (normalized.Contains("MAINSWITCH", StringComparison.Ordinal)) return "CabinetMainSwitch";
        if (normalized.Contains("POSITIONSWITCH", StringComparison.Ordinal) || normalized.EndsWith("SWITCH", StringComparison.Ordinal)) return "CabinetSwitch";
        var actual = definition?.Trim().Replace('/', '\\').Split('\\').Last();
        if (string.IsNullOrWhiteSpace(actual) || Guid.TryParse(actual, out _)) return null;
        return actual.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? actual[..^4] : actual;
    }

    private static bool EndsWithType(string value, string typeName) =>
        NormalizeToken(value).EndsWith(NormalizeToken(typeName), StringComparison.Ordinal);

    private static string NormalizeToken(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static string? FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record ContainerCandidate(
        FeeContainerLiveObject Object,
        string XmlType,
        string RuntimeType,
        string ComponentName);

    private sealed record ResolvedEntry(FeeContainerLiveVariable Variable, string XmlSlot);
}

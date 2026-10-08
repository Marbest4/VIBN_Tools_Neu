using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;

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
    IReadOnlyList<Guid>? LinkedObjectGuids = null);

public sealed record FeeContainerLiveVariable(
    Guid VariableGuid,
    string Signal,
    string Address,
    string Path,
    string DataType,
    string Comment);

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
    bool IsManual = false);

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
        ContainerMetadataCatalog.SupportedXmlTypes;

    public static FeeContainerReconstructionResult Reconstruct(
        Guid rootGuid,
        string rootName,
        IEnumerable<FeeContainerLiveObject> objects,
        IEnumerable<FeeContainerLiveVariable> variables,
        IEnumerable<FeeContainerLiveAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(assignments);

        var sourceObjects = objects.Where(item => item.Guid != Guid.Empty && !EndsWithType(item.FeeType, "Decoration"))
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
        var relevantObjectGuids = new HashSet<Guid>();

        foreach (var item in sourceObjects)
        {
            if (!TryCreateCandidate(item, out var candidate, out var ambiguity))
                continue;
            candidates.Add(candidate!);
            relevantObjectGuids.Add(item.Guid);
            if (!string.IsNullOrWhiteSpace(ambiguity))
                issues.Add(new FeeContainerReconstructionIssue(item.Guid, ambiguity));
        }

        // Historical owner IDs must never collapse primary objects that have
        // since been renamed or changed type. Live logic/cabinet/button GUIDs
        // remain distinct; same-name ancillary sensors belong to their logic.
        candidates = candidates.Where(item => !IsRedundantLegacySimObject(item, candidates)).ToList();

        var objectAssociations = ResolveAllObjectAssociations(sourceObjects, candidates, issues);
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
            var resolved = objectAssignments
                .Select(assignment => TryCreateEntry(candidate, assignment, variableByGuid))
                .Where(item => item is not null)
                .Cast<ResolvedEntry>()
                .GroupBy(item => (item.Variable.VariableGuid, item.XmlSlot))
                .Select(group => group.First())
                .OrderBy(item => item.XmlSlot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Variable.Signal, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var componentName = candidate.ComponentName;
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
                    new XElement("Address", string.IsNullOrWhiteSpace(variable.Path)
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
                    item.ObjectGuid == candidate.Object.Guid ? "Primary" : IsHelper(item.ObjectType) ? "TechnicalHelper" : "SimObject")))));
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
            .Where(item => !relevantObjectGuids.Contains(item.Guid))
            .Select(item => new FeeContainerUnmappedObject(
                item.Guid,
                item.Name,
                item.FeeType,
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
        ICollection<FeeContainerReconstructionIssue> issues)
    {
        var result = new List<FeeContainerObjectAssociation>();
        foreach (var item in objects)
        {
            var matches = candidates.Where(candidate =>
                ContainerFileXml.HasMatchingObjectName(item.Name, candidate.ComponentName)).ToArray();
            // Provenance disambiguates equal names but never invents an automatic
            // association between differently named physical scene objects.
            var own = matches.Where(candidate => candidate.Object.Guid == item.Guid).ToArray();
            if (own.Length == 1) matches = own;
            else if (matches.Length > 1)
            {
                var related = matches.Where(candidate => item.LinkedObjectGuids?.Contains(candidate.Object.Guid) == true ||
                    IsDescendantOf(item, candidate.Object.Guid, objects)).ToArray();
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
        return result.ToArray();
    }

    private static bool IsDescendantOf(FeeContainerLiveObject item, Guid candidateGuid,
        IReadOnlyList<FeeContainerLiveObject> objects)
    {
        var byGuid = objects.ToDictionary(value => value.Guid);
        var visited = new HashSet<Guid>();
        for (var parent = item.ParentGuid; parent.HasValue && visited.Add(parent.Value);)
        {
            if (parent.Value == candidateGuid) return true;
            parent = byGuid.GetValueOrDefault(parent.Value)?.ParentGuid;
        }
        return false;
    }

    private static bool IsHelper(string type) => new[] { "BoolNot", "MoveBit", "BoolAnd", "BoolOr" }
        .Any(value => EndsWithType(type, value));

    private static bool TryCreateCandidate(
        FeeContainerLiveObject item,
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
            var compatibleHint = new[] { "ReturnCircuit", "SafeArea" }.FirstOrDefault(type =>
                string.Equals(type, hint, StringComparison.OrdinalIgnoreCase));
            xmlType = compatibleHint ?? "ReturnCircuit";
            if (compatibleHint is null)
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
            if (xmlType is null)
                return false;
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

        if (xmlType is null || !ContainerMetadataCatalog.TryGet(xmlType, out var descriptor))
            return false;
        var componentName = EndsWithType(feeType, "CabinetElement")
            ? FirstNotBlank(item.Label, item.Name?.Split(';')[0])
            : item.Name;
        candidate = new ContainerCandidate(item, xmlType, descriptor, componentName ?? string.Empty);
        return true;
    }

    private static ResolvedEntry? TryCreateEntry(
        ContainerCandidate candidate,
        FeeContainerLiveAssignment assignment,
        IReadOnlyDictionary<Guid, FeeContainerLiveVariable> variables)
    {
        if (!variables.TryGetValue(assignment.VariableGuid, out var variable))
            return null;
        var slot = MapSlot(candidate, assignment.TargetSlot);
        return string.IsNullOrWhiteSpace(slot) ? null : new ResolvedEntry(variable, slot);
    }

    private static string? MapSlot(ContainerCandidate candidate, string runtimeSlot)
        => MapRuntimeSlot(candidate.XmlType, runtimeSlot);

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
        IEnumerable<ContainerCandidate> allCandidates)
    {
        if (!EndsWithType(candidate.Object.FeeType, "Sensor") &&
            !EndsWithType(candidate.Object.FeeType, "SafetySensor") &&
            !EndsWithType(candidate.Object.FeeType, "Floor"))
            return false;
        return allCandidates.Any(other =>
            other.Object.Guid != candidate.Object.Guid &&
            !string.IsNullOrWhiteSpace(other.Object.LogicDefinitionName) &&
            string.Equals(other.ComponentName, candidate.ComponentName, StringComparison.OrdinalIgnoreCase));
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
            normalized.Contains("POSITIONSWITCH2", StringComparison.Ordinal) ||
            normalized.Contains("2POSITIONSWITCH", StringComparison.Ordinal) ||
            normalized.Contains("TWOPOSITIONSWITCH", StringComparison.Ordinal))
            return "CabinetSwitch";
        return null;
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
        ContainerDescriptor Descriptor,
        string ComponentName);

    private sealed record ResolvedEntry(FeeContainerLiveVariable Variable, string XmlSlot);
}

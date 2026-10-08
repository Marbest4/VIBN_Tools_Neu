using FS.SDK.Components;
using FS.SDK.Scene.Objects;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Settings;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record Fee2ContainerRoot(
    Guid Guid,
    string Name,
    FeeContainerProvenanceSnapshot? Provenance,
    int UpdatedSignalCount,
    int MissingSignalCount,
    int UpdatedSlotCount,
    int UnresolvedSlotCount,
    bool UsesExactProvenance = true,
    int InspectedObjectCount = 0,
    int IgnoredObjectCount = 0,
    IReadOnlyList<FeeContainerReconstructionIssue>? ReconstructionIssues = null,
    IReadOnlyList<FeeContainerUnmappedObject>? NonContainerObjects = null,
    IReadOnlyList<FeeContainerObjectAssociation>? ObjectAssociations = null)
{
    public bool HasProvenance => Provenance is not null && UsesExactProvenance;
    public string SourceKind => HasProvenance ? "Container2FEE-Provenienz" : "FEE-Struktur (rekonstruiert)";
    public int ContainerCount => Provenance?.ContainerCount ?? 0;
    public int SignalCount => Provenance?.SignalCount ?? 0;
}

public sealed record Fee2ContainerExportResult(
    FeeContainerProvenanceSnapshot Snapshot,
    bool UsedProvenance,
    int InspectedObjectCount,
    int IgnoredObjectCount,
    IReadOnlyList<FeeContainerReconstructionIssue> Issues);

public sealed record Fee2ContainerDiscoveryIssue(Guid? Guid, string RootName, string Message);

public sealed record Fee2ContainerDiscoveryResult(
    IReadOnlyList<Fee2ContainerRoot> Roots,
    int IgnoredWithoutProvenance,
    IReadOnlyList<Fee2ContainerDiscoveryIssue> Issues);

public sealed record Fee2ContainerProgress(int Percent, string Message);

/// <summary>
/// Lists only top-level BasicFrames as selectable scopes. The interactive reverse workflow reconstructs every root from current scene
/// objects and live assignments; provenance is only a type-disambiguation hint.
/// </summary>
public sealed class Fee2ContainerService
{
    public async Task<Fee2ContainerDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default,
        bool reconstructLegacyRoots = true,
        IProgress<Fee2ContainerProgress>? progress = null,
        IReadOnlyDictionary<Guid, string>? knownTopLevelRoots = null)
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);

        if (reconstructLegacyRoots)
            return await DiscoverFromLiveSceneAsync(progress, cancellationToken);

        var roots = new List<Fee2ContainerRoot>();
        var issues = new List<Fee2ContainerDiscoveryIssue>();
        var ignored = 0;
        IReadOnlyList<Guid> topLevelRoots;
        if (knownTopLevelRoots is not null)
        {
            topLevelRoots = knownTopLevelRoots.Keys.ToArray();
        }
        else
        {
            var guidValues = await Services.ApiInstance.Object
                .GetSceneObjectGuidsOfTypeAsync(nameof(BasicFrame)) ?? [];
            var topLevel = await FeeTopLevelBasicFrameDiscovery.DiscoverAsync(guidValues, cancellationToken);
            issues.AddRange(topLevel.Issues.Select(message =>
                new Fee2ContainerDiscoveryIssue(null, string.Empty, message)));
            topLevelRoots = topLevel.Roots;
        }
        var currentVariables = (await Services.ApiInstance.Interface.GetAllVariablesAsync() ?? [])
            .Select(variable => new FeeContainerVariableState(
                variable.VariableGuid,
                variable.Tag ?? string.Empty,
                variable.Address ?? string.Empty,
                variable.Path ?? string.Empty,
                variable.Type.ToString(),
                variable.Comment ?? string.Empty))
            .ToArray();

        foreach (var guid in topLevelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = knownTopLevelRoots?.GetValueOrDefault(guid) ?? guid.ToString("D");
            try
            {
                if (knownTopLevelRoots is null)
                {
                    var nameXml = await Services.ApiInstance.Object.GetPropertyAsync(
                        guid,
                        nameof(FS.SDK.SceneObject.Name));
                    name = Services.ApiInstance.XmlHelper.ConvertToString(nameXml);
                }

                var tags = await ReadOptionalTagsAsync(guid);
                if (!tags.ContainsKey(FeeContainerProvenanceCodec.SchemaKey))
                {
                    ignored++;
                    if (!reconstructLegacyRoots)
                        continue;

                    var reconstructed = await ReconstructAsync(guid, name, cancellationToken);
                    roots.Add(new Fee2ContainerRoot(
                        guid,
                        name,
                        reconstructed.Snapshot,
                        0,
                        0,
                        0,
                        0,
                        UsesExactProvenance: false,
                        reconstructed.InspectedObjectCount,
                        reconstructed.IgnoredObjectCount,
                        reconstructed.Issues));
                    issues.AddRange(reconstructed.Issues.Select(issue =>
                        new Fee2ContainerDiscoveryIssue(issue.ObjectGuid, name, issue.Message)));
                    continue;
                }
                if (!FeeContainerProvenanceCodec.TryRead(tags, out var provenance, out var error))
                {
                    issues.Add(new Fee2ContainerDiscoveryIssue(guid, name, error));
                    if (!reconstructLegacyRoots)
                    {
                        ignored++;
                        continue;
                    }

                    var reconstructed = await ReconstructAsync(guid, name, cancellationToken);
                    roots.Add(new Fee2ContainerRoot(
                        guid,
                        name,
                        reconstructed.Snapshot,
                        0,
                        0,
                        0,
                        0,
                        UsesExactProvenance: false,
                        reconstructed.InspectedObjectCount,
                        reconstructed.IgnoredObjectCount,
                        reconstructed.Issues));
                    issues.AddRange(reconstructed.Issues.Select(issue =>
                        new Fee2ContainerDiscoveryIssue(issue.ObjectGuid, name, issue.Message)));
                    continue;
                }

                var slotResolutions = await ResolveSlotsAsync(
                    guid,
                    provenance!.SignalBindings.Select(binding => binding.VariableGuid),
                    cancellationToken);
                var projection = FeeContainerVariableProjector.Apply(
                    provenance,
                    currentVariables,
                    slotResolutions
                        .Where(result => result.Slot is not null)
                        .ToDictionary(result => result.VariableGuid, result => result.Slot!));
                if (projection.MissingVariableGuids.Count > 0)
                {
                    issues.Add(new Fee2ContainerDiscoveryIssue(
                        guid,
                        name,
                        $"{projection.MissingVariableGuids.Count} in der Provenienz referenzierte " +
                        "FEE-Variablen fehlen; für diese Einträge bleibt der Generierungsstand erhalten."));
                }
                roots.Add(new Fee2ContainerRoot(
                    guid,
                    name,
                    projection.Snapshot,
                    projection.UpdatedEntries,
                    projection.MissingVariableGuids.Count,
                    projection.UpdatedSlots,
                    projection.UnresolvedSlotVariableGuids.Count));
                foreach (var resolution in slotResolutions.Where(result => result.Issue is not null))
                {
                    issues.Add(new Fee2ContainerDiscoveryIssue(
                        guid,
                        name,
                        resolution.Issue!));
                }
            }
            catch (Exception exception)
            {
                issues.Add(new Fee2ContainerDiscoveryIssue(
                    guid,
                    name,
                    $"Der FEE-Root konnte nicht gelesen werden: {exception.Message}"));
            }
        }

        return new Fee2ContainerDiscoveryResult(
            roots.OrderBy(root => root.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ignored,
            issues);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadOptionalTagsAsync(Guid guid)
    {
        try
        {
            return await FeeTagPropertyStore.ReadAsync(guid);
        }
        catch
        {
            // Older/manual roots do not necessarily expose a TagComponent.
            // They are still valid selectable scopes for live reconstruction.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public async Task<Fee2ContainerExportResult> CreateExportAsync(
        Fee2ContainerRoot root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Provenance is not null)
        {
            return new Fee2ContainerExportResult(
                root.Provenance,
                root.UsesExactProvenance,
                root.InspectedObjectCount,
                root.IgnoredObjectCount,
                root.ReconstructionIssues ?? []);
        }

        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);
        return await ReconstructAsync(root.Guid, root.Name, cancellationToken);
    }

    public async Task<Fee2ContainerExportResult> CreateCombinedExportAsync(
        IEnumerable<Fee2ContainerRoot> selectedRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedRoots);
        var roots = selectedRoots.DistinctBy(root => root.Guid).ToArray();
        if (roots.Length == 0)
            throw new InvalidOperationException("Es wurde kein FEE-Root für den Export ausgewählt.");
        if (roots.Length == 1)
            return await CreateExportAsync(roots[0], cancellationToken);

        var containerElements = new List<XElement>();
        var bindings = new List<FeeContainerSignalBinding>();
        var issues = new List<FeeContainerReconstructionIssue>();
        var containerOffset = 0;
        var signalCount = 0;
        var inspected = 0;
        var ignored = 0;
        var usedProvenance = true;
        var inventoryObjects = new List<XElement>();
        var inventorySignals = new List<XElement>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = await CreateExportAsync(root, cancellationToken);
            inventoryObjects.AddRange(export.Snapshot.ContainerDocument.Root?.Element("FeeInventory")?.Element("SimObjects")?.Elements("SimObject") ?? []);
            inventorySignals.AddRange(export.Snapshot.ContainerDocument.Root?.Element("FeeInventory")?.Element("Signals")?.Elements("Signal") ?? []);
            var rootContainers = export.Snapshot.ContainerDocument.Descendants("Container").ToArray();
            foreach (var element in rootContainers)
            {
                var clone = new XElement(element);
                var sourceId = clone.Attribute("id")?.Value ?? "container";
                clone.SetAttributeValue("id", $"fee-root:{root.Guid:D}:{sourceId}");
                containerElements.Add(clone);
            }
            bindings.AddRange(export.Snapshot.SignalBindings.Select(binding => binding with
            {
                ContainerIndex = binding.ContainerIndex + containerOffset,
            }));
            containerOffset += rootContainers.Length;
            signalCount += export.Snapshot.SignalCount;
            inspected += export.InspectedObjectCount;
            ignored += export.IgnoredObjectCount;
            usedProvenance &= export.UsedProvenance;
            issues.AddRange(export.Issues.Select(issue => issue with
            {
                Message = $"{root.Name}: {issue.Message}",
            }));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CAAMergeResult",
                new XAttribute("version", "1.0.0.0"),
                new XAttribute("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                new XAttribute("autoCreateFile", string.Empty),
                new XAttribute("zuli", string.Empty),
                new XElement("ContainerList", containerElements),
                new XElement("FeeInventory",
                    new XElement("SimObjects", inventoryObjects.DistinctBy(item => item.Element("Guid")?.Value).Select(item => new XElement(item))),
                    new XElement("Signals", inventorySignals.DistinctBy(item => item.Element("Guid")?.Value).Select(item => new XElement(item))))));
        var snapshot = new FeeContainerProvenanceSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            document,
            bindings,
            containerElements.Count,
            signalCount,
            string.Join("+", roots.Select(root => root.Provenance?.SourceFingerprint)
                .Where(value => !string.IsNullOrWhiteSpace(value))));
        return new Fee2ContainerExportResult(snapshot, usedProvenance, inspected, ignored, issues);
    }

    private static async Task<Fee2ContainerExportResult> ReconstructAsync(
        Guid rootGuid,
        string rootName,
        CancellationToken cancellationToken)
    {
        var issues = new List<FeeContainerReconstructionIssue>();
        var guidTexts = (await Services.ApiInstance!.Object
                .GetAllChildrenFromSceneObjectAsync(rootGuid.ToString()) ?? [])
            .Where(value => Guid.TryParse(value, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var xmlTexts = guidTexts.Length == 0
            ? []
            : (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(guidTexts) ?? []).ToArray();
        var objectTags = await ReadObjectTagsAsync(guidTexts, cancellationToken);
        var logicDefinitions = await Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync() ?? [];
        var logicNames = logicDefinitions
            .Where(item => Guid.TryParse(item.Guid, out _))
            .GroupBy(item => Guid.Parse(item.Guid))
            .ToDictionary(group => group.Key, group => group.First().Name ?? string.Empty);
        var objects = new List<FeeContainerLiveObject>();
        for (var index = 0; index < Math.Min(guidTexts.Length, xmlTexts.Length); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guid = Guid.Parse(guidTexts[index]);
            try
            {
                var xml = XElement.Parse(xmlTexts[index]);
                var type = xml.Attribute("Type")?.Value ?? xml.Name.LocalName;
                var name = xml.Attribute("Name")?.Value ?? string.Empty;
                var logicGuidText = ReadXmlValue(xml, "PersistedLogicGuid");
                if (string.IsNullOrWhiteSpace(logicGuidText) && IsLogicObject(type))
                {
                    logicGuidText = await ReadOptionalObjectPropertyAsync(
                        guid,
                        "PersistedLogicGuid");
                }
                var logicName = Guid.TryParse(logicGuidText, out var logicGuid) &&
                                logicNames.TryGetValue(logicGuid, out var resolvedLogicName)
                    ? resolvedLogicName
                    : null;
                var cabinetDefinition = ReadXmlValue(xml, "Definition", "ElementType");
                var label = ReadXmlValue(xml, "Label");
                if (IsCabinetElement(type))
                {
                    // Several FEE/SDK versions do not serialize these dynamic
                    // Cabinet properties into GetSceneObjectsAsXmlAsync. Query
                    // them explicitly so Switch/Fuse/EStop/Lamp are not lost.
                    if (string.IsNullOrWhiteSpace(cabinetDefinition))
                        cabinetDefinition = await ReadOptionalObjectPropertyAsync(guid, "Definition");
                    if (string.IsNullOrWhiteSpace(cabinetDefinition))
                        cabinetDefinition = await ReadOptionalObjectPropertyAsync(guid, "ElementType");
                    if (string.IsNullOrWhiteSpace(label))
                        label = await ReadOptionalObjectPropertyAsync(guid, "Label");
                }
                var marks = (xml.Element("MarkComponent")?.Element("Mark")?.Value ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var provenance = ContainerObjectProvenance.Read(
                    objectTags.GetValueOrDefault(guid),
                    marks);
                objects.Add(new FeeContainerLiveObject(
                    guid,
                    name,
                    type,
                    logicName,
                    cabinetDefinition,
                    label,
                    provenance.ContainerId,
                    provenance.ContainerType));
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
            {
                issues.Add(new FeeContainerReconstructionIssue(
                    guid,
                    $"FEE-Objekt konnte nicht ausgewertet werden: {exception.Message}"));
            }
        }

        if (xmlTexts.Length != guidTexts.Length)
        {
            issues.Add(new FeeContainerReconstructionIssue(
                null,
                $"FEE lieferte für {guidTexts.Length} untergeordnete Objekte nur {xmlTexts.Length} XML-Datensätze."));
        }

        var apiVariables = (await Services.ApiInstance.Interface.GetAllVariablesAsync() ?? []).ToArray();
        var currentVariables = apiVariables
            .Select(variable => new FeeContainerLiveVariable(
                variable.VariableGuid,
                variable.Tag ?? string.Empty,
                variable.Address ?? string.Empty,
                variable.Path ?? string.Empty,
                variable.Type.ToString(),
                variable.Comment ?? string.Empty))
            .ToArray();
        var scopedObjects = objects.Select(item => item.Guid).ToHashSet();
        var assignmentRead = await ReadAssignmentsAsync(
            apiVariables
                .Where(variable => variable.References > 0)
                .Select(variable => variable.VariableGuid),
            scopedObjects,
            cancellationToken);
        issues.AddRange(assignmentRead.Issues);

        var reconstruction = FeeContainerLiveReconstructor.Reconstruct(
            rootGuid,
            rootName,
            objects,
            currentVariables,
            assignmentRead.Assignments);
        return new Fee2ContainerExportResult(
            reconstruction.Snapshot,
            false,
            reconstruction.InspectedObjectCount,
            reconstruction.IgnoredObjectCount,
            issues.Concat(reconstruction.Issues).ToArray());
    }

    internal static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> ReadObjectTagsAsync(
        IEnumerable<string> guidTexts,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        // The vendor client is stateful. Parallel GetProperty calls can block
        // each other in larger projects, so FEE2Container deliberately reads
        // metadata serially just like the stable ModelValidation snapshot.
        foreach (var guidText in guidTexts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(guidText, out var guid))
                continue;
            result[guid] = await ReadOptionalTagsAsync(guid);
        }
        return result;
    }

    private static async Task<Fee2ContainerDiscoveryResult> DiscoverFromLiveSceneAsync(
        IProgress<Fee2ContainerProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new Fee2ContainerProgress(5, "Aktuelle FEE-Struktur wird als frischer Snapshot eingelesen …"));
        if (Services.FeeObjects is null)
            throw new InvalidOperationException("Der FEE-Dienst ist nicht initialisiert.");
        var connectionRevision = Services.Connection!.ConnectionRevision;
        var allObjects = (await Fee2ContainerReadPhase.ReadAsync("Aktuelle FEE-Szene lesen",
                () => Services.FeeObjects.ReadFeeSceneObjectsForDiscoveryAsync(cancellationToken), cancellationToken))
            .Where(item => !FeeSceneObjectReadPolicy.IsIgnoredObject(item)).DistinctBy(item => item.Guid).ToArray();
        EnsureLiveConnection(connectionRevision);
        var roots = allObjects.OfType<FeeBasicFrame>().Where(IsTopLevelInSnapshot)
            .OrderBy(frame => frame.Name, StringComparer.OrdinalIgnoreCase).ThenBy(frame => frame.Guid).ToArray();
        var ownerByObject = allObjects.ToDictionary(item => item.Guid, item =>
            Guid.TryParse(FeeSimObjectDiscovery.ResolveRoot(item).GuidString, out var guid) ? guid : Guid.Empty);
        var rootGuids = roots.Select(root => root.Guid).ToHashSet();
        var scopedByRoot = allObjects.Where(item => !rootGuids.Contains(item.Guid))
            .GroupBy(item => ownerByObject[item.Guid]).ToDictionary(group => group.Key, group => group.ToArray());
        if (scopedByRoot.ContainsKey(Guid.Empty))
            roots = roots.Append(new FeeBasicFrame { Guid = Guid.Empty, Name = "Projektobjekte ohne BasicFrame" }).ToArray();
        progress?.Report(new Fee2ContainerProgress(12, "Aktuelle Interface-Variablen werden gelesen …"));
        // Reconstruction needs the current variable snapshot, not the interface
        // properties. Avoid the extra parallel SDK request per interface.
        var liveVariables = await Fee2ContainerReadPhase.ReadAsync("Aktuelle FEE-Variablen lesen", async () =>
        {
            var variables = await Services.ApiInstance!.Interface.GetAllVariablesAsync() ?? [];
            return variables.Select(signal => new FeeContainerLiveVariable(signal.VariableGuid,
                signal.Tag ?? "", signal.Address ?? "", signal.Path ?? "", signal.Type.ToString(), signal.Comment ?? ""))
                .DistinctBy(signal => signal.VariableGuid).ToArray();
        }, cancellationToken);
        EnsureLiveConnection(connectionRevision);
        foreach (var cabinet in allObjects.OfType<FeeCabinetElement>().Where(item => string.IsNullOrWhiteSpace(item.ElementType)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            cabinet.ElementType = await ReadOptionalObjectPropertyAsync(cabinet.Guid, "Definition") ??
                await ReadOptionalObjectPropertyAsync(cabinet.Guid, "ElementType") ?? "";
            if (string.IsNullOrWhiteSpace(cabinet.Label))
                cabinet.Label = await ReadOptionalObjectPropertyAsync(cabinet.Guid, "Label") ?? "";
        }
        progress?.Report(new Fee2ContainerProgress(18, "Aktuelle Variablen- und Hilfslogikrouten werden einmalig gelesen …"));
        var assignmentRead = await Fee2ContainerReadPhase.ReadAsync("Aktuelle FEE-Signalzuordnungen lesen",
            () => ReadAssignmentsAsync(liveVariables.Select(signal => signal.VariableGuid),
                allObjects.Select(item => item.Guid).ToHashSet(), cancellationToken, progress), cancellationToken);
        var assignmentsByRoot = assignmentRead.Assignments.GroupBy(assignment =>
                ownerByObject.GetValueOrDefault(assignment.TargetObjectGuid))
            .ToDictionary(group => group.Key, group => group.ToArray());
        // Provenance can resolve type aliases such as Cylinder/FeedSafetyDoor
        // or ReturnCircuit/SafeArea. It never supplies signal entries or object
        // ownership; both are rebuilt from the current scene and API endpoints.
        var hintObjects = allObjects.Where(item => item is FeeSimpleNot || item is FeeLogic logic &&
            ContainerMetadataCatalog.FindXmlTypesByLogicName(logic.LogicDefinitionName).Count > 1).ToArray();
        var hints = await ReadContainerObjectPropertiesAsync(hintObjects, cancellationToken);
        var resultRoots = new List<Fee2ContainerRoot>();
        var resultIssues = new List<Fee2ContainerDiscoveryIssue>();
        foreach (var issue in assignmentRead.Issues)
            resultIssues.Add(new Fee2ContainerDiscoveryIssue(issue.ObjectGuid, "Signalrouten", issue.Message));
        for (var index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLiveConnection(connectionRevision);
            var root = roots[index];
            progress?.Report(new Fee2ContainerProgress(roots.Length == 0 ? 90 : 30 + index * 65 / roots.Length,
                $"Root {index + 1} von {roots.Length} wird live rekonstruiert: {root.Name}"));
            var scoped = scopedByRoot.GetValueOrDefault(root.Guid) ?? [];
            var assignments = assignmentsByRoot.GetValueOrDefault(root.Guid) ?? [];
            try
            {
                var liveObjects = scoped.Select(item => ToLiveObject(item, hints.GetValueOrDefault(item.Guid))).ToArray();
                var reconstructed = FeeContainerLiveReconstructor.Reconstruct(root.Guid, root.Name, liveObjects, liveVariables, assignments);
                var associations = FeeContainerAssociationProjection.Apply(reconstructed.Snapshot, reconstructed, scoped);
                AttachInventory(reconstructed.Snapshot, scoped, liveVariables);
                var rootIssues = reconstructed.Issues.Concat(assignmentRead.Issues).ToArray();
                resultRoots.Add(new Fee2ContainerRoot(root.Guid, root.Name, reconstructed.Snapshot,
                    reconstructed.Snapshot.SignalCount, 0, reconstructed.Snapshot.SignalCount, 0,
                    UsesExactProvenance: false, reconstructed.InspectedObjectCount, reconstructed.IgnoredObjectCount,
                    rootIssues, reconstructed.UnmappedObjects, associations));
                resultIssues.AddRange(reconstructed.Issues.Select(issue =>
                    new Fee2ContainerDiscoveryIssue(issue.ObjectGuid, root.Name, issue.Message)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                resultIssues.Add(new Fee2ContainerDiscoveryIssue(root.Guid, root.Name,
                    $"Die aktuelle Struktur dieses Roots konnte nicht rekonstruiert werden: {exception.Message}"));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLiveConnection(connectionRevision);
        progress?.Report(new Fee2ContainerProgress(100, "FEE-Roots und aktuelle Container vollständig ausgewertet."));
        return new Fee2ContainerDiscoveryResult(resultRoots, resultRoots.Count, resultIssues);
    }

    /// <summary>
    /// A selectable reverse-export root must not have a BasicFrame anywhere
    /// above it. Looking only at the immediate parent incorrectly exposed
    /// deeper frames below intermediary scene objects as additional roots.
    /// </summary>
    public static bool IsTopLevelInSnapshot(FeeBasicFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var visited = new HashSet<Guid> { frame.Guid };
        for (var parent = frame.Parent; parent is not null; parent = parent.Parent)
        {
            if (!visited.Add(parent.Guid))
                return false;
            if (parent is FeeBasicFrame)
                return false;
        }

        return true;
    }

    private static bool IsWithinRoot(FeeAbstractObject item, FeeBasicFrame root)
    {
        var current = item;
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Guid))
        {
            if (current.Guid == root.Guid)
                return true;
            current = current.Parent;
        }
        return false;
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>>
        ReadContainerObjectPropertiesAsync(
            IReadOnlyCollection<FeeAbstractObject> objects,
            CancellationToken cancellationToken)
    {
        var candidates = objects.Where(CanCarryContainerProvenance).ToArray();
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        // Do not issue concurrent calls against the shared FEE ObjectApi. In
        // practice that made a root scan appear to hang although the snapshot
        // itself had already completed successfully.
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = await ReadOptionalTagsAsync(item.Guid);
            if (item.Guid != Guid.Empty && properties.Count > 0)
                result[item.Guid] = properties;
        }
        return result;
    }

    private static void AttachInventory(FeeContainerProvenanceSnapshot snapshot, IReadOnlyList<FeeAbstractObject> objects,
        IReadOnlyList<FeeContainerLiveVariable> variables)
    {
        foreach (var binding in snapshot.SignalBindings)
        {
            var container = snapshot.ContainerDocument.Descendants("Container").ElementAtOrDefault(binding.ContainerIndex);
            var entry = container?.Descendants("Entry").ElementAtOrDefault(binding.EntryIndex);
            entry?.SetAttributeValue("feeGuid", binding.VariableGuid.ToString("D"));
        }
        snapshot.ContainerDocument.Root?.Element("FeeInventory")?.Remove();
        snapshot.ContainerDocument.Root?.Add(new XElement("FeeInventory",
            new XElement("SimObjects", objects.Select(item => ContainerFileXml.Object(item.GuidString,
                item.Name ?? "", item.FeeType ?? "", "Available", "", item.TypeName))),
            new XElement("Signals", variables.Select(item => new XElement("Signal",
                new XElement("Guid", item.VariableGuid.ToString("D")), new XElement("InterfaceGuid", ""),
                new XElement("InterfaceName", ""), new XElement("Tag", item.Signal),
                new XElement("Address", item.Address), new XElement("Path", item.Path), new XElement("DataType", item.DataType))))));
    }

    private static bool CanCarryContainerProvenance(FeeAbstractObject item) => item is
        FeeLogic or
        FeeCabinetElement or
        FeeButton or
        FeeSegmentedLamp or
        FeeSensor or
        FeeFloor or
        FeeJoint or
        FeeSurface or
        FeePickAndPlace or
        FeeSimpleNot or
        FeeSimpleMove or FeeSimpleAnd or FeeSimpleOr;

    private static FeeContainerLiveObject ToLiveObject(
        FeeAbstractObject item,
        IReadOnlyDictionary<string, string>? properties)
    {
        var provenance = ContainerObjectProvenance.Read(
            properties,
            item.Marks ?? []);
        var cabinet = item as FeeCabinetElement;
        return new FeeContainerLiveObject(
            item.Guid,
            item.Name ?? string.Empty,
            item.FeeType ?? item.GetType().Name,
            (item as FeeLogic)?.LogicDefinitionName,
            cabinet?.ElementType,
            cabinet?.Label,
            provenance.ContainerId,
            provenance.ContainerType,
            item.Parent?.Guid,
            item.Slots?.Values.Where(guid => guid != Guid.Empty).Distinct().ToArray());
    }

    private static IReadOnlyDictionary<Guid, string> ResolveSlotsFromSnapshot(
        FeeContainerProvenanceSnapshot provenance,
        IReadOnlyList<FeeContainerLiveAssignment> assignments)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var variableGuid in provenance.SignalBindings.Select(item => item.VariableGuid).Distinct())
        {
            var slots = assignments
                .Where(item => item.VariableGuid == variableGuid &&
                               item.TargetSlot.StartsWith("PLC_", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.TargetSlot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (slots.Length == 1)
                result[variableGuid] = slots[0];
        }
        return result;
    }

    /// <summary>
    /// FEE versions serialize some Cabinet/Logic properties either directly,
    /// below a component element, or as an attribute. Read all compatible
    /// representations without depending on one SDK XML layout.
    /// </summary>
    private static string? ReadXmlValue(XElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var attribute = root.DescendantsAndSelf().Attributes()
                .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(attribute?.Value))
                return attribute.Value.Trim();

            var element = root.DescendantsAndSelf()
                .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(element?.Value))
                return element.Value.Trim();
        }
        return null;
    }

    private static async Task<string?> ReadOptionalObjectPropertyAsync(Guid guid, string propertyName)
    {
        try
        {
            var value = await Services.ApiInstance!.Object.GetPropertyAsync(guid, propertyName);
            var converted = Services.ApiInstance.XmlHelper.ConvertToString(value);
            return string.IsNullOrWhiteSpace(converted) ? null : converted.Trim();
        }
        catch
        {
            // Dynamic properties are version/type specific. Their absence is
            // a normal discriminator, not a reason to abort the whole root.
            return null;
        }
    }

    private static bool IsCabinetElement(string? type) =>
        NormalizeToken(type).EndsWith("CABINETELEMENT", StringComparison.Ordinal);

    private static bool IsLogicObject(string? type) =>
        NormalizeToken(type).Contains("LOGIC", StringComparison.Ordinal);

    private static string NormalizeToken(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static async Task<VariableAssignmentRead> ReadAssignmentsAsync(
        IEnumerable<Guid> variableGuids,
        IReadOnlySet<Guid> scopedObjects,
        CancellationToken cancellationToken,
        IProgress<Fee2ContainerProgress>? progress = null)
    {
        var connectionRevision = Services.Connection!.ConnectionRevision;
        var helperRoutes = new Dictionary<Guid, IReadOnlyList<(Guid ObjectGuid, string SlotName)>>();
        var failedHelpers = new HashSet<Guid>();
        var matches = new List<FeeContainerLiveAssignment>();
        var issues = new List<FeeContainerReconstructionIssue>();
        var variables = variableGuids.Distinct().ToArray();
        async Task<IReadOnlyList<(Guid ObjectGuid, string SlotName)>> ReadHelperRouteAsync(Guid guid)
        {
            if (helperRoutes.TryGetValue(guid, out var cached))
                return cached;
            cancellationToken.ThrowIfCancellationRequested();
            var links = await Services.ApiInstance!.Interface.GetSlotSlotAssignmentAsync(guid, "Input 01") ?? [];
            cancellationToken.ThrowIfCancellationRequested();
            var result = links.SelectMany(link => Guid.TryParse(link.SceneObjectGuid, out var linkedGuid)
                    ? (link.SlotNames ?? []).Select(slot => (linkedGuid, slot)) : [])
                .Distinct().ToArray();
            helperRoutes[guid] = result;
            return result;
        }
        // The vendor client is stateful. Serial requests avoid one pending
        // variable/slot request cancelling another request on the same client.
        for (var index = 0; index < variables.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLiveConnection(connectionRevision);
            var variableGuid = variables[index];
            if (index % 25 == 0)
                progress?.Report(new Fee2ContainerProgress(18 + index * 10 / Math.Max(1, variables.Length),
                    $"Aktuelle Signalzuordnung {index + 1} von {variables.Length} wird gelesen ({variableGuid:D}) …"));
            try
            {
                var assignments = await Services.ApiInstance!.Interface
                    .GetAssignedSceneObjectsAsync(variableGuid) ?? [];
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (objectGuid, slots) in assignments)
                {
                    foreach (var slot in slots ?? Array.Empty<string>())
                    {
                        if (scopedObjects.Contains(objectGuid))
                        {
                            matches.Add(new FeeContainerLiveAssignment(
                                variableGuid,
                                objectGuid,
                                slot));
                        }

                        // Container2FEE creates MoveBit outside the container root for
                        // a second PLC_IN consumer. Follow that project-wide assignment
                        // and only scope the linked target back to the selected root.
                        if (!string.Equals(slot, "Output 01", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (failedHelpers.Contains(objectGuid))
                            continue;
                        try
                        {
                            var links = await ReadHelperRouteAsync(objectGuid);
                            foreach (var (linkedGuid, linkedSlot) in links)
                            {
                                if (scopedObjects.Contains(linkedGuid))
                                    matches.Add(new FeeContainerLiveAssignment(
                                        variableGuid,
                                        linkedGuid,
                                        linkedSlot));
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            failedHelpers.Add(objectGuid);
                            issues.Add(new FeeContainerReconstructionIssue(
                                objectGuid,
                                $"MoveBit-Slotroute konnte nicht gelesen werden ({objectGuid:D}, Input 01): {Fee2ContainerReadPhase.Describe(exception)}"));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                issues.Add(new FeeContainerReconstructionIssue(
                    variableGuid,
                    $"Zuweisungen der Variable konnten nicht gelesen werden ({variableGuid:D}): {Fee2ContainerReadPhase.Describe(exception)}"));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLiveConnection(connectionRevision);
        return new VariableAssignmentRead(matches.Distinct().ToArray(), issues);
    }

    private static void EnsureLiveConnection(long revision)
    {
        if (Services.Connection?.IsConnected != true || Services.Connection.ConnectionRevision != revision)
            throw new InvalidOperationException("Die FEE-Verbindung hat sich während des Einlesens geändert. " +
                "Bitte erneut verbinden und ModelValidation → Update Objects ausführen.");
    }

    private static async Task<IReadOnlyList<SlotResolution>> ResolveSlotsAsync(
        Guid rootGuid,
        IEnumerable<Guid> variableGuids,
        CancellationToken cancellationToken)
    {
        var scopedObjects = (await Services.ApiInstance!.Object
                .GetAllChildrenFromSceneObjectAsync(rootGuid.ToString()) ?? [])
            .Select(value => Guid.TryParse(value, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .Append(rootGuid)
            .ToHashSet();
        using var throttle = new SemaphoreSlim(6);
        var tasks = variableGuids.Distinct().Select(async variableGuid =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var assignments = await Services.ApiInstance.Interface
                    .GetAssignedSceneObjectsAsync(variableGuid) ?? [];
                foreach (var (objectGuid, slots) in assignments)
                {
                    foreach (var slot in slots ?? Array.Empty<string>())
                    {
                        if (scopedObjects.Contains(objectGuid) &&
                            slot.StartsWith("PLC_", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(slot);
                        if (!string.Equals(slot, "Output 01", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var links = await Services.ApiInstance.Interface
                            .GetSlotSlotAssignmentAsync(objectGuid, "Input 01") ?? [];
                        foreach (var (linkedGuidText, linkedSlots) in links)
                        {
                            if (!Guid.TryParse(linkedGuidText, out var linkedGuid) ||
                                !scopedObjects.Contains(linkedGuid))
                                continue;
                            foreach (var linkedSlot in linkedSlots ?? Array.Empty<string>())
                            {
                                if (linkedSlot.StartsWith("PLC_", StringComparison.OrdinalIgnoreCase))
                                    candidates.Add(linkedSlot);
                            }
                        }
                    }
                }

                return candidates.Count switch
                {
                    1 => new SlotResolution(variableGuid, candidates.Single(), null),
                    0 => new SlotResolution(
                        variableGuid,
                        null,
                        $"Für Variable {variableGuid:D} wurde innerhalb des Roots keine PLC-Slotroute gefunden."),
                    _ => new SlotResolution(
                        variableGuid,
                        null,
                        $"Variable {variableGuid:D} besitzt mehrere PLC-Slotrouten: {string.Join(", ", candidates.OrderBy(value => value))}.")
                };
            }
            catch (Exception exception)
            {
                return new SlotResolution(
                    variableGuid,
                    null,
                    $"Slotroute für Variable {variableGuid:D} konnte nicht gelesen werden: {exception.Message}");
            }
            finally
            {
                throttle.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private sealed record SlotResolution(Guid VariableGuid, string? Slot, string? Issue);

    private sealed record VariableAssignmentRead(
        IReadOnlyList<FeeContainerLiveAssignment> Assignments,
        IReadOnlyList<FeeContainerReconstructionIssue> Issues);
}

using FS.SDK.Components;
using FS.SDK.Scene.Objects;
using System.Xml.Linq;
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
    IReadOnlyList<FeeContainerUnmappedObject>? NonContainerObjects = null)
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
/// Lists only top-level BasicFrames as selectable scopes. Roots carrying versioned
/// Container2FEE metadata use the exact round-trip; other roots can be
/// reconstructed from supported descendants and their live assignments.
/// </summary>
public sealed class Fee2ContainerService
{
    public async Task<Fee2ContainerDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default,
        bool reconstructLegacyRoots = true,
        IProgress<Fee2ContainerProgress>? progress = null)
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);

        if (reconstructLegacyRoots)
            return await DiscoverFromModelValidationSnapshotAsync(progress, cancellationToken);

        var roots = new List<Fee2ContainerRoot>();
        var issues = new List<Fee2ContainerDiscoveryIssue>();
        var ignored = 0;
        var guidValues = await Services.ApiInstance.Object
            .GetSceneObjectGuidsOfTypeAsync(nameof(BasicFrame));
        var topLevel = await FeeTopLevelBasicFrameDiscovery.DiscoverAsync(guidValues, cancellationToken);
        issues.AddRange(topLevel.Issues.Select(message =>
            new Fee2ContainerDiscoveryIssue(null, string.Empty, message)));
        var currentVariables = (await Services.ApiInstance.Interface.GetAllVariablesAsync())
            .Select(variable => new FeeContainerVariableState(
                variable.VariableGuid,
                variable.Tag ?? string.Empty,
                variable.Address ?? string.Empty,
                variable.Path ?? string.Empty,
                variable.Type.ToString(),
                variable.Comment ?? string.Empty))
            .ToArray();

        foreach (var guid in topLevel.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = guid.ToString("D");
            try
            {
                var nameXml = await Services.ApiInstance.Object.GetPropertyAsync(
                    guid,
                    nameof(FS.SDK.SceneObject.Name));
                name = Services.ApiInstance.XmlHelper.ConvertToString(nameXml);

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
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var export = await CreateExportAsync(root, cancellationToken);
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
                new XElement("ContainerList", containerElements)));
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
                .GetAllChildrenFromSceneObjectAsync(rootGuid.ToString()))
            .Where(value => Guid.TryParse(value, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var xmlTexts = guidTexts.Length == 0
            ? []
            : (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(guidTexts)).ToArray();
        var objectTags = await ReadObjectTagsAsync(guidTexts, cancellationToken);
        var logicDefinitions = await Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync();
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

        var apiVariables = (await Services.ApiInstance.Interface.GetAllVariablesAsync()).ToArray();
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

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> ReadObjectTagsAsync(
        IEnumerable<string> guidTexts,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(6);
        var reads = guidTexts.Distinct(StringComparer.OrdinalIgnoreCase).Select(async guidText =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(guidText, out var guid))
                return (Guid.Empty, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return (guid, await ReadOptionalTagsAsync(guid));
            }
            finally
            {
                throttle.Release();
            }
        });
        return (await Task.WhenAll(reads))
            .Where(item => item.Item1 != Guid.Empty)
            .ToDictionary(item => item.Item1, item => item.Item2);
    }

    private static async Task<Fee2ContainerDiscoveryResult> DiscoverFromModelValidationSnapshotAsync(
        IProgress<Fee2ContainerProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new Fee2ContainerProgress(5, "FEE-Projekt wird einmalig wie in ModelValidation eingelesen …"));
        if (Services.FeeObjects is null)
            throw new InvalidOperationException("Der ModelValidation-FEE-Dienst ist nicht initialisiert.");
        await Services.FeeObjects.UpdateFeeDataAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var allObjects = Services.FeeObjects.AllFeeObjects?.ToArray() ?? [];
        var roots = allObjects
            .OfType<FeeBasicFrame>()
            .Where(frame => frame.Parent is not FeeBasicFrame)
            .OrderBy(frame => frame.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(frame => frame.Guid)
            .ToArray();
        var variables = allObjects
            .OfType<FeeInterface>()
            .SelectMany(item => item.Signals ?? [])
            .GroupBy(signal => signal.Guid)
            .Select(group => group.First())
            .ToArray();
        var variableStates = variables.Select(signal => new FeeContainerVariableState(
            signal.Guid,
            signal.Tag ?? string.Empty,
            signal.Address ?? string.Empty,
            signal.Path ?? string.Empty,
            signal.IOTypeString ?? string.Empty,
            signal.Comment ?? string.Empty)).ToArray();
        var liveVariables = variableStates.Select(variable => new FeeContainerLiveVariable(
            variable.VariableGuid,
            variable.Signal,
            variable.Address,
            variable.Path,
            variable.DataType,
            variable.Comment)).ToArray();
        var variableGuids = variables.Select(item => item.Guid).ToHashSet();
        var resultRoots = new List<Fee2ContainerRoot>();
        var resultIssues = new List<Fee2ContainerDiscoveryIssue>();
        var withoutProvenance = 0;

        for (var index = 0; index < roots.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            progress?.Report(new Fee2ContainerProgress(
                roots.Length == 0 ? 90 : 15 + index * 75 / roots.Length,
                $"Root {index + 1} von {roots.Length} wird rekonstruiert: {root.Name}"));
            var scoped = allObjects
                .Where(item => item is not FeeInterface && item.Guid != root.Guid && IsWithinRoot(item, root))
                .ToArray();
            var assignments = scoped
                .Where(item => item.Slots is not null)
                .SelectMany(item => item.Slots
                    .Where(slot => variableGuids.Contains(slot.Value))
                    .Select(slot => new FeeContainerLiveAssignment(slot.Value, item.Guid, slot.Key)))
                .Distinct()
                .ToArray();
            try
            {
                var tags = await ReadOptionalTagsAsync(root.Guid);
                if (FeeContainerProvenanceCodec.TryRead(tags, out var provenance, out var provenanceError))
                {
                    var exactObjectProperties = await ReadContainerObjectPropertiesAsync(scoped, cancellationToken);
                    var exactLiveObjects = scoped
                        .Select(item => ToLiveObject(item, exactObjectProperties.GetValueOrDefault(item.Guid)))
                        .ToArray();
                    var classification = FeeContainerLiveReconstructor.Reconstruct(
                        root.Guid,
                        root.Name,
                        exactLiveObjects,
                        liveVariables,
                        assignments);
                    var slots = ResolveSlotsFromSnapshot(provenance!, assignments);
                    var projection = FeeContainerVariableProjector.Apply(provenance!, variableStates, slots);
                    resultRoots.Add(new Fee2ContainerRoot(
                        root.Guid,
                        root.Name,
                        projection.Snapshot,
                        projection.UpdatedEntries,
                        projection.MissingVariableGuids.Count,
                        projection.UpdatedSlots,
                        projection.UnresolvedSlotVariableGuids.Count,
                        UsesExactProvenance: true,
                        scoped.Length,
                        0,
                        [],
                        classification.UnmappedObjects));
                    continue;
                }

                withoutProvenance++;
                if (tags.ContainsKey(FeeContainerProvenanceCodec.SchemaKey) && !string.IsNullOrWhiteSpace(provenanceError))
                {
                    resultIssues.Add(new Fee2ContainerDiscoveryIssue(
                        root.Guid,
                        root.Name,
                        $"Provenienz ist ungültig; die Struktur wird stattdessen live rekonstruiert: {provenanceError}"));
                }
                var objectProperties = await ReadContainerObjectPropertiesAsync(
                    scoped,
                    cancellationToken);
                var liveObjects = scoped
                    .Select(item => ToLiveObject(
                        item,
                        objectProperties.GetValueOrDefault(item.Guid)))
                    .ToArray();
                var reconstructed = FeeContainerLiveReconstructor.Reconstruct(
                    root.Guid,
                    root.Name,
                    liveObjects,
                    liveVariables,
                    assignments);
                resultRoots.Add(new Fee2ContainerRoot(
                    root.Guid,
                    root.Name,
                    reconstructed.Snapshot,
                    0,
                    0,
                    0,
                    0,
                    UsesExactProvenance: false,
                    reconstructed.InspectedObjectCount,
                    reconstructed.IgnoredObjectCount,
                    reconstructed.Issues,
                    reconstructed.UnmappedObjects));
                resultIssues.AddRange(reconstructed.Issues.Select(issue =>
                    new Fee2ContainerDiscoveryIssue(issue.ObjectGuid, root.Name, issue.Message)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                resultIssues.Add(new Fee2ContainerDiscoveryIssue(
                    root.Guid,
                    root.Name,
                    $"Der Snapshot dieses Roots konnte nicht rekonstruiert werden: {exception.Message}"));
            }
        }

        progress?.Report(new Fee2ContainerProgress(100, "FEE-Roots und Container wurden vollständig ausgewertet."));
        return new Fee2ContainerDiscoveryResult(resultRoots, withoutProvenance, resultIssues);
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
        using var throttle = new SemaphoreSlim(8, 8);
        var reads = candidates.Select(async item =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return (item.Guid, Properties: await ReadOptionalTagsAsync(item.Guid));
            }
            finally
            {
                throttle.Release();
            }
        });
        return (await Task.WhenAll(reads))
            .Where(item => item.Guid != Guid.Empty && item.Properties.Count > 0)
            .ToDictionary(item => item.Guid, item => item.Properties);
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
        FeeSimpleMove;

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
            provenance.ContainerType);
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
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(6);
        var tasks = variableGuids.Distinct().Select(async variableGuid =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var matches = new List<FeeContainerLiveAssignment>();
                var localIssues = new List<FeeContainerReconstructionIssue>();
                var assignments = await Services.ApiInstance!.Interface
                    .GetAssignedSceneObjectsAsync(variableGuid);
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
                        try
                        {
                            var links = await Services.ApiInstance.Interface
                                .GetSlotSlotAssignmentAsync(objectGuid, "Input 01");
                            foreach (var (linkedGuidText, linkedSlots) in links)
                            {
                                if (!Guid.TryParse(linkedGuidText, out var linkedGuid) ||
                                    !scopedObjects.Contains(linkedGuid))
                                    continue;
                                foreach (var linkedSlot in linkedSlots ?? Array.Empty<string>())
                                {
                                    matches.Add(new FeeContainerLiveAssignment(
                                        variableGuid,
                                        linkedGuid,
                                        linkedSlot));
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            localIssues.Add(new FeeContainerReconstructionIssue(
                                objectGuid,
                                $"MoveBit-Slotroute konnte nicht gelesen werden: {exception.Message}"));
                        }
                    }
                }

                return new VariableAssignmentRead(matches, localIssues);
            }
            catch (Exception exception)
            {
                return new VariableAssignmentRead(
                    [],
                    [new FeeContainerReconstructionIssue(
                        variableGuid,
                        $"Zuweisungen der Variable konnten nicht gelesen werden: {exception.Message}")]);
            }
            finally
            {
                throttle.Release();
            }
        });
        var reads = await Task.WhenAll(tasks);
        return new VariableAssignmentRead(
            reads.SelectMany(read => read.Assignments).Distinct().ToArray(),
            reads.SelectMany(read => read.Issues).ToArray());
    }

    private static async Task<IReadOnlyList<SlotResolution>> ResolveSlotsAsync(
        Guid rootGuid,
        IEnumerable<Guid> variableGuids,
        CancellationToken cancellationToken)
    {
        var scopedObjects = (await Services.ApiInstance!.Object
                .GetAllChildrenFromSceneObjectAsync(rootGuid.ToString()))
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
                    .GetAssignedSceneObjectsAsync(variableGuid);
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
                            .GetSlotSlotAssignmentAsync(objectGuid, "Input 01");
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

using FS.SDK.Components;
using FS.SDK.Scene.Objects;
using System.Xml.Linq;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
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
    IReadOnlyList<FeeContainerReconstructionIssue>? ReconstructionIssues = null)
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

/// <summary>
/// Lists only top-level BasicFrames as selectable scopes. Roots carrying versioned
/// Container2FEE metadata use the exact round-trip; other roots can be
/// reconstructed from supported descendants and their live assignments.
/// </summary>
public sealed class Fee2ContainerService
{
    public async Task<Fee2ContainerDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default,
        bool reconstructLegacyRoots = true)
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);

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
            var tagsXml = await Services.ApiInstance!.Object.GetPropertyAsync(
                guid,
                nameof(TagComponent.TagEntries),
                nameof(TagComponent));
            return Services.ApiInstance.XmlHelper.ConvertToDictionaryStringString(tagsXml);
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
                var logicGuidText = xml.Element("Logic")?.Element("PersistedLogicGuid")?.Value;
                var logicName = Guid.TryParse(logicGuidText, out var logicGuid) &&
                                logicNames.TryGetValue(logicGuid, out var resolvedLogicName)
                    ? resolvedLogicName
                    : null;
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
                    xml.Element("Definition")?.Value,
                    xml.Element("Label")?.Value,
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

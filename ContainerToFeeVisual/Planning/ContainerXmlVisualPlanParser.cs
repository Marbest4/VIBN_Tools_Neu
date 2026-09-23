using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed class ContainerXmlVisualPlanParser(IVisualPlanLogger logger)
{
    private const long MaximumXmlBytes = 50L * 1024 * 1024;
    private const long MaximumXmlCharacters = 50L * 1024 * 1024;

    public async Task<VisualPlanLoadResult> ParseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("XML_PATH_EMPTY", "Es wurde keine Container-XML-Datei angegeben.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.Error("Der Container-XML-Pfad ist ungültig.", exception);
            return Failure("XML_PATH_INVALID", "Der Container-XML-Pfad ist ungültig.");
        }

        if (!File.Exists(fullPath))
            return Failure("XML_NOT_FOUND", $"Container-XML wurde nicht gefunden: {fullPath}");

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaximumXmlBytes)
            return Failure("XML_TOO_LARGE", "Die Container-XML überschreitet die zulässige Größe von 50 MB.");

        try
        {
            var (document, fingerprint) = await ReadSecurelyAsync(fullPath, cancellationToken);
            var plan = BuildPlan(fullPath, fingerprint, document);
            logger.Information(
                $"Plan aus '{Path.GetFileName(fullPath)}' geladen: " +
                $"{plan.Nodes.Count(node => node.Kind == VisualNodeKind.Container)} Container, " +
                $"{plan.Nodes.Count(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal)} Signale.");

            var isValid = plan.Issues.All(issue => issue.Severity != VisualIssueSeverity.Error);
            return new VisualPlanLoadResult(
                isValid,
                plan,
                plan.Issues,
                isValid
                    ? "Container-XML wurde als visueller Plan geladen."
                    : "Container-XML wurde geladen, enthält aber Fehler.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            logger.Error("Container-XML konnte nicht geladen werden.", exception);
            return Failure("XML_READ_FAILED", $"Container-XML konnte nicht geladen werden: {exception.Message}");
        }
    }

    private static async Task<(XDocument Document, string Fingerprint)> ReadSecurelyAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                if (read == 0)
                    break;
                offset += read;
            }

            if (offset != bytes.Length)
                throw new EndOfStreamException("Container-XML wurde während des Lesens verändert.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var memory = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(
            memory,
            new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });

        return (XDocument.Load(reader, LoadOptions.SetLineInfo), fingerprint);
    }

    private static VisualPlan BuildPlan(
        string fullPath,
        string fingerprint,
        XDocument document)
    {
        var nodes = new List<VisualNode>();
        var edges = new List<VisualEdge>();
        var targets = new List<VisualSimObjectTarget>();
        var issues = new List<VisualIssue>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        const string rootId = "root:generation";
        const string basicFrameId = "global:basic-frame";
        const string interfaceId = "global:interface";
        const string unknownInterfaceId = "global:unknown-interface";

        AddNode(nodes, edges, new VisualNode(
            rootId, null, null, VisualNodeKind.Root,
            Path.GetFileName(fullPath), "Container2FEE-Plan", null, false));
        AddNode(nodes, edges, new VisualNode(
            basicFrameId, rootId, null, VisualNodeKind.BasicFrame,
            "Auto Generated", "FeeBasicFrame", null, true));
        AddNode(nodes, edges, new VisualNode(
            interfaceId, rootId, null, VisualNodeKind.Interface,
            "Auto Generated", "FeeInterface", null, true));

        var containerElements = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Container")
            .ToList();

        if (containerElements.Count == 0)
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Error,
                "XML_NO_CONTAINERS",
                "Die XML-Datei enthält keine Container."));
        }

        var hasUnknownSignals = false;
        foreach (var element in containerElements)
        {
            var componentName = ChildValue(element, "Component");
            var xmlType = ChildValue(element, "Type");
            var sourceId = element.Attribute("id")?.Value ?? string.Empty;
            var identity = $"{sourceId}\u001f{componentName}\u001f{xmlType}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = ++occurrence;

            var hasDescriptor = ContainerMetadataCatalog.TryGet(xmlType, out var descriptor);

            var containerId = CreateContainerId(sourceId, componentName, xmlType, occurrence);
            var displayName = string.IsNullOrWhiteSpace(componentName)
                ? $"Unbenannter Container ({xmlType})"
                : componentName;
            var containerNode = new VisualNode(
                containerId,
                rootId,
                containerId,
                VisualNodeKind.Container,
                displayName,
                xmlType,
                null,
                false,
                descriptor?.SupportsCreation == true);
            AddNode(nodes, edges, containerNode);

            if (string.IsNullOrWhiteSpace(componentName))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "CONTAINER_COMPONENT_MISSING",
                    "Ein Container besitzt keinen Komponentennamen.",
                    containerId));
            }

            if (string.IsNullOrWhiteSpace(xmlType))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "CONTAINER_TYPE_MISSING",
                    $"Container '{displayName}' besitzt keinen Typ.",
                    containerId));
            }

            var entries = element
                .Descendants()
                .Where(descendant => descendant.Name.LocalName == "Entry")
                .ToList();

            if (!hasDescriptor)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "CONTAINER_TYPE_UNKNOWN",
                    $"Container-Typ '{xmlType}' ist unbekannt; seine Signale werden wie im bestehenden Ablauf in die Unknown-Schnittstelle übernommen.",
                    containerId));

                foreach (var entry in entries)
                {
                    if (!hasUnknownSignals)
                    {
                        AddNode(nodes, edges, new VisualNode(
                            unknownInterfaceId,
                            rootId,
                            null,
                            VisualNodeKind.Interface,
                            "Unknown Signals",
                            "FeeInterface",
                            null,
                            true));
                        hasUnknownSignals = true;
                    }

                    AddSignalNode(
                        nodes,
                        edges,
                        issues,
                        entry,
                        containerId,
                        unknownInterfaceId,
                        null,
                        VisualNodeKind.UnknownSignal,
                        descriptor: null);
                }

                continue;
            }

            string? logicNodeId = null;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName))
            {
                logicNodeId = $"{containerId}:logic";
                AddNode(nodes, edges, new VisualNode(
                    logicNodeId,
                    containerId,
                    containerId,
                    VisualNodeKind.Logic,
                    descriptor.ExpectedLogicName,
                    "FeeLogic",
                    null,
                    false));
            }

            foreach (var (helperName, helperIndex) in descriptor.TechnicalHelpers.Select((name, index) => (name, index)))
            {
                AddNode(nodes, edges, new VisualNode(
                    $"{containerId}:helper:{helperIndex}",
                    logicNodeId ?? containerId,
                    containerId,
                    VisualNodeKind.TechnicalHelper,
                    helperName,
                    "Technisches Hilfsobjekt",
                    null,
                    true));
            }

            foreach (var targetDescriptor in descriptor.Targets)
            {
                var targetId = CreateTargetId(containerId, targetDescriptor.Index, targetDescriptor.DisplayName);
                targets.Add(new VisualSimObjectTarget(
                    targetId,
                    containerId,
                    targetDescriptor.DisplayName,
                    targetDescriptor.AllowedType.FullName ?? targetDescriptor.AllowedType.Name,
                    targetDescriptor.AllowMultiSelect));
                AddNode(nodes, edges, new VisualNode(
                    targetId,
                    logicNodeId ?? containerId,
                    containerId,
                    VisualNodeKind.SimObjectTarget,
                    targetDescriptor.DisplayName,
                    targetDescriptor.AllowedType.Name,
                    null,
                    false));

                if (logicNodeId is not null)
                {
                    edges.Add(new VisualEdge(
                        $"edge:slots:{StableId.Encode(targetId)}:{StableId.Encode(logicNodeId)}",
                        targetId,
                        logicNodeId,
                        VisualEdgeKind.SlotToSlot,
                        "Bestehende Container-Verknüpfung"));
                }
            }

            foreach (var entry in entries)
            {
                AddSignalNode(
                    nodes,
                    edges,
                    issues,
                    entry,
                    containerId,
                    interfaceId,
                    logicNodeId,
                    VisualNodeKind.Signal,
                    descriptor);
            }

            if (entries.Count == 0)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "CONTAINER_WITHOUT_SIGNALS",
                    $"Container '{displayName}' enthält keine Signale.",
                    containerId));
            }
        }

        WireChildren(nodes);
        return new VisualPlan(
            fullPath,
            GetDefaultSidecarPath(fullPath),
            fingerprint,
            new ReadOnlyCollection<VisualNode>(nodes),
            new ReadOnlyCollection<VisualNode>(nodes.Where(node => node.ParentId is null).ToList()),
            new ReadOnlyCollection<VisualEdge>(edges),
            new ReadOnlyCollection<VisualSimObjectTarget>(targets),
            assignments: null,
            creationRequests: null,
            generationSelections: null,
            signalCreationSelections: null,
            signalAssignments: null,
            slotOverrides: null,
            existingInterfaceSelection: null,
            new ReadOnlyCollection<VisualIssue>(issues));
    }

    private static void AddSignalNode(
        List<VisualNode> nodes,
        List<VisualEdge> edges,
        List<VisualIssue> issues,
        XElement entry,
        string containerId,
        string interfaceId,
        string? logicNodeId,
        VisualNodeKind nodeKind,
        ContainerDescriptor? descriptor)
    {
        var slot = ChildValue(entry, "Slot");
        var signal = ChildValue(entry, "Signal");
        var address = ChildValue(entry, "Address");
        var dataType = ChildValue(entry, "DataType");
        var entryId = ChildValue(entry, "ID");
        var signalId = $"{containerId}:signal:{StableId.Encode($"{entryId}\u001f{address}\u001f{signal}\u001f{slot}\u001f{nodes.Count}")}";
        var signalNode = new VisualNode(
            signalId,
            containerId,
            containerId,
            nodeKind,
            string.IsNullOrWhiteSpace(signal) ? address : signal,
            dataType,
            slot,
            false,
            sourceLocation: address);
        AddNode(nodes, edges, signalNode);

        edges.Add(new VisualEdge(
            $"edge:interface:{StableId.Encode(interfaceId)}:{StableId.Encode(signalId)}",
            interfaceId,
            signalId,
            VisualEdgeKind.ParentChild,
            "Schnittstellensignal"));

        var slotTargetId = logicNodeId ?? containerId;
        edges.Add(new VisualEdge(
            $"edge:signal:{StableId.Encode(signalId)}:{StableId.Encode(slotTargetId)}",
            signalId,
            slotTargetId,
            VisualEdgeKind.SignalToSlot,
            slot));

        if (string.IsNullOrWhiteSpace(signal) && string.IsNullOrWhiteSpace(address))
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Error,
                "SIGNAL_IDENTITY_MISSING",
                "Ein Eintrag enthält weder Signalnamen noch Adresse.",
                signalId));
        }

        if (descriptor is not null && !descriptor.Slots.Contains(slot))
        {
            var expectedSlots = string.Join(", ", descriptor.Slots.OrderBy(value => value, StringComparer.Ordinal));
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Error,
                "SIGNAL_SLOT_UNKNOWN",
                $"Slot '{slot}' ist für Container-Typ '{descriptor.RuntimeType.Name}' nicht definiert. " +
                $"Erwartet wird einer dieser Slots: {expectedSlots}.",
                signalId));
        }
    }

    private static void AddNode(
        ICollection<VisualNode> nodes,
        ICollection<VisualEdge> edges,
        VisualNode node)
    {
        nodes.Add(node);
        if (node.ParentId is null)
            return;

        edges.Add(new VisualEdge(
            $"edge:parent:{StableId.Encode(node.ParentId)}:{StableId.Encode(node.Id)}",
            node.ParentId,
            node.Id,
            VisualEdgeKind.ParentChild,
            "enthält"));
    }

    private static void WireChildren(IReadOnlyCollection<VisualNode> nodes)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => node.ParentId is not null))
        {
            if (byId.TryGetValue(node.ParentId!, out var parent))
                parent.AddChild(node);
        }
    }

    private static string ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value ?? string.Empty;

    internal static string CreateContainerId(
        string sourceId,
        string componentName,
        string xmlType,
        int occurrence) =>
        $"container:{StableId.Encode($"{sourceId}\u001f{componentName}\u001f{xmlType}")}:{occurrence}";

    internal static string CreateTargetId(string containerId, int targetIndex, string displayName) =>
        $"{containerId}:target:{targetIndex}:{StableId.Encode(displayName)}";

    internal static string GetDefaultSidecarPath(string sourceXmlPath) =>
        sourceXmlPath + ".container2fee.visual.json";

    private static VisualPlanLoadResult Failure(string code, string message)
    {
        var issues = new[] { new VisualIssue(VisualIssueSeverity.Error, code, message) };
        return new VisualPlanLoadResult(false, null, issues, message);
    }
}

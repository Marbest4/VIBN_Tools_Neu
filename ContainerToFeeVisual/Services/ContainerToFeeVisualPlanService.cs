using System.IO;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Coordinates parsing, sidecar persistence, validated drag/drop changes,
/// undo/redo and execution through the unchanged legacy generator.
/// </summary>
public sealed class ContainerToFeeVisualPlanService
{
    private readonly IVisualPlanLogger _logger;
    private readonly ContainerXmlVisualPlanParser _parser;
    private readonly VisualPlanSidecarStore _sidecarStore;
    private readonly FeeSimObjectDiscovery _discovery;
    private readonly FeeInterfaceDiscovery _interfaceDiscovery;
    private readonly FeeSignalLinkDiscovery _signalLinkDiscovery;
    private readonly FeeSimObjectLinkDiscovery _simObjectLinkDiscovery;
    private readonly LegacyContainerToFeeExecutionAdapter _executor;
    private readonly ExistingSimObjectLinkAdapter _linkExecutor;
    private readonly ExistingSignalLinkAdapter _signalLinkExecutor;
    private readonly Stack<PlanState> _undo = new();
    private readonly Stack<PlanState> _redo = new();
    private readonly HashSet<string> _confirmedDuplicateIdentities = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VisualFeeObject> _feeObjects = [];
    private IReadOnlyList<VisualFeeContainerObject> _feeContainerObjects = [];
    private IReadOnlyDictionary<string, FeeAbstractObject> _runtimeObjects =
        new Dictionary<string, FeeAbstractObject>(StringComparer.Ordinal);
    private IReadOnlyList<VisualFeeInterface> _feeInterfaces = [];
    private IReadOnlyList<VisualFeeSignal> _feeSignals = [];
    private IReadOnlyList<VisualFeeSignalLink> _feeSignalLinks = [];
    private IReadOnlyList<VisualFeeObjectLink> _feeSimObjectLinks = [];
    private IReadOnlyDictionary<Guid, string> _topLevelBasicFrames =
        new Dictionary<Guid, string>();
    private IReadOnlyDictionary<string, FeeInterface> _runtimeInterfaces =
        new Dictionary<string, FeeInterface>(StringComparer.OrdinalIgnoreCase);
    private bool _hasDiscoveredFeeObjects;
    private bool _hasDiscoveredFeeInterfaces;
    private bool _hasDiscoveredFeeSignalLinks;
    private bool _hasDiscoveredFeeSimObjectLinks;

    public ContainerToFeeVisualPlanService()
        : this(new VisualPlanLogger())
    {
    }

    internal ContainerToFeeVisualPlanService(IVisualPlanLogger logger)
    {
        _logger = logger;
        _parser = new ContainerXmlVisualPlanParser(logger);
        _sidecarStore = new VisualPlanSidecarStore(logger);
        _discovery = new FeeSimObjectDiscovery(logger);
        _interfaceDiscovery = new FeeInterfaceDiscovery(logger);
        _signalLinkDiscovery = new FeeSignalLinkDiscovery(logger);
        _simObjectLinkDiscovery = new FeeSimObjectLinkDiscovery(logger);
        _executor = new LegacyContainerToFeeExecutionAdapter(logger);
        _linkExecutor = new ExistingSimObjectLinkAdapter(logger);
        _signalLinkExecutor = new ExistingSignalLinkAdapter(logger);
    }

    public event EventHandler<VisualPlanChangedEventArgs>? PlanChanged;

    public VisualPlan? CurrentPlan { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<VisualFeeObject> DiscoveredFeeObjects => _feeObjects;
    public IReadOnlyList<VisualFeeContainerObject> DiscoveredFeeContainerObjects => _feeContainerObjects;

    public IReadOnlyList<VisualFeeInterface> DiscoveredFeeInterfaces => _feeInterfaces;
    public IReadOnlyList<VisualFeeSignal> DiscoveredFeeSignals => _feeSignals;
    public IReadOnlyList<VisualFeeSignalLink> DiscoveredFeeSignalLinks => _feeSignalLinks;
    public IReadOnlyList<VisualFeeObjectLink> DiscoveredFeeSimObjectLinks => _feeSimObjectLinks;

    public IReadOnlyList<string> SupportedContainerTypes => ContainerMetadataCatalog.SupportedXmlTypes;

    public bool CanClassifySignalOnlyContainer(string? containerId)
    {
        var plan = CurrentPlan;
        var container = string.IsNullOrWhiteSpace(containerId) ? null : plan?.FindNode(containerId);
        if (plan is null || container?.Kind != VisualNodeKind.Container)
            return false;
        // The XML can already contain a known type while still carrying only
        // signal entries. Keep the type selector available so that an incorrect
        // or obsolete declaration can be corrected without editing the source.
        return plan.Nodes.Any(node =>
                   string.Equals(node.ContainerId, container.Id, StringComparison.Ordinal) &&
                   node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal);
    }

    public bool SetSignalOnlyContainerType(string containerId, string typeName)
    {
        var plan = CurrentPlan;
        if (plan is null || !CanClassifySignalOnlyContainer(containerId) ||
            !ContainerMetadataCatalog.TryGet(typeName, out _))
            return false;

        var current = plan.ContainerTypeOverrides.FirstOrDefault(item =>
            string.Equals(item.ContainerId, containerId, StringComparison.Ordinal));
        if (string.Equals(current?.TypeName, typeName, StringComparison.OrdinalIgnoreCase))
            return true;

        var before = Capture(plan);
        var overrides = plan.ContainerTypeOverrides
            .Where(item => !string.Equals(item.ContainerId, containerId, StringComparison.Ordinal))
            .Append(new VisualContainerTypeOverride(containerId, typeName))
            .ToArray();
        plan.ReplaceContainerTypeOverrides(overrides);
        plan.ReplaceAssignments(plan.Assignments.Where(assignment => plan.FindTarget(assignment.TargetId) is not null));
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    public VisualFeeObjectConnectionSummary GetFeeObjectConnectionSummary(string feeObjectId)
    {
        var feeObject = FindFeeObject(feeObjectId);
        if (feeObject is null || !Guid.TryParse(feeObject.GuidString, out var objectGuid))
            return new VisualFeeObjectConnectionSummary(false, []);

        var guid = objectGuid.ToString("D");
        var details = new List<string>();
        foreach (var signalLink in _feeSignalLinks.Where(link => string.Equals(
                     link.ObjectGuidString,
                     guid,
                     StringComparison.OrdinalIgnoreCase)))
        {
            var signal = _feeSignals.FirstOrDefault(item => string.Equals(
                item.GuidString,
                signalLink.SignalGuidString,
                StringComparison.OrdinalIgnoreCase));
            var signalDescription =
                $"Signal '{signal?.Tag ?? "Name nicht auflösbar"}' aus Interface " +
                $"'{signal?.InterfaceName ?? "unbekannt"}'" +
                (signalLink.IsIndirect ? " (über MoveBit)" : string.Empty);
            details.Add(string.IsNullOrWhiteSpace(signalLink.SlotName)
                ? $"Verknüpft mit {signalDescription}"
                : $"Eigener Slot '{signalLink.SlotName.Trim()}' ← {signalDescription}");
        }

        foreach (var objectLink in _feeSimObjectLinks.Where(link =>
                     string.Equals(link.ObjectGuidString, guid, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(link.LinkedObjectGuidString, guid, StringComparison.OrdinalIgnoreCase)))
        {
            var isSource = string.Equals(
                objectLink.ObjectGuidString,
                guid,
                StringComparison.OrdinalIgnoreCase);
            var otherGuid = isSource
                ? objectLink.LinkedObjectGuidString
                : objectLink.ObjectGuidString;
            var ownSlot = isSource ? objectLink.SlotName : objectLink.LinkedSlotName;
            var otherSlot = isSource ? objectLink.LinkedSlotName : objectLink.SlotName;
            var linkedObject = DescribeLinkedFeeObject(otherGuid);
            details.Add((string.IsNullOrWhiteSpace(ownSlot), string.IsNullOrWhiteSpace(otherSlot)) switch
            {
                (false, false) =>
                    $"Eigener Slot '{ownSlot.Trim()}' ↔ {linkedObject}, dort Slot '{otherSlot.Trim()}'",
                (false, true) => $"Eigener Slot '{ownSlot.Trim()}' ↔ {linkedObject}",
                (true, false) => $"Verknüpft mit {linkedObject}, dort Slot '{otherSlot.Trim()}'",
                _ => $"Verknüpft mit {linkedObject}",
            });
        }

        return new VisualFeeObjectConnectionSummary(
            _hasDiscoveredFeeSimObjectLinks,
            details.Where(detail => !string.IsNullOrWhiteSpace(detail))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(detail => detail, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public bool IsDuplicateFeeObject(string? feeObjectId) =>
        FindFeeObject(feeObjectId)?.HasExactDuplicate == true;

    public bool IsDuplicateFeeObjectConfirmed(string? feeObjectId) =>
        FindFeeObject(feeObjectId) is { HasExactDuplicate: true } feeObject &&
        _confirmedDuplicateIdentities.Contains(CreateDuplicateIdentity(feeObject));

    public VisualAssignmentResult ConfirmDuplicateAssignment(string targetId, string feeObjectId)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return AssignmentFailure("Es ist kein visueller Plan geladen.", "PLAN_NOT_LOADED");
        var target = plan.FindTarget(targetId);
        var feeObject = FindFeeObject(feeObjectId);
        if (target is null || feeObject is null || !feeObject.HasExactDuplicate)
        {
            return AssignmentFailure(
                "Der Mehrfachtreffer ist nicht mehr vorhanden.",
                "DUPLICATE_CONFIRMATION_TARGET_MISSING",
                targetId);
        }
        if (!target.CanAssign(feeObject))
            return AssignmentFailure("Das gewählte Objekt ist nicht kompatibel.", "FEE_OBJECT_INCOMPATIBLE", targetId);

        var before = Capture(plan);
        var assignments = plan.Assignments.ToList();
        if (!assignments.Any(item => item.TargetId == targetId && item.FeeObjectId == feeObjectId))
            assignments.Add(ToAssignment(targetId, feeObject));
        if (!target.AllowMultiSelect)
        {
            assignments = assignments
                .Where(item => item.TargetId != targetId || item.FeeObjectId == feeObjectId)
                .ToList();
        }
        _confirmedDuplicateIdentities.Add(CreateDuplicateIdentity(feeObject));
        RecordMutation(before);
        plan.ReplaceAssignments(assignments);
        _logger.Information(
            $"Mehrfachfund '{feeObject.Name}' unter '{feeObject.ParentName}' wurde ausdrücklich bestätigt; " +
            (target.AllowMultiSelect ? "Multi-Select-Zuordnungen bleiben erhalten." : "der bestätigte Treffer wurde eindeutig ausgewählt."));
        RaisePlanChanged();
        return new VisualAssignmentResult(
            true,
            target.AllowMultiSelect
                ? $"Mehrfachfund '{feeObject.Name}' wurde für das Multi-Select-Ziel bestätigt."
                : $"'{feeObject.Name}' wurde als eindeutiger Treffer bestätigt; andere Treffer wurden von diesem Ziel gelöst.",
            ToAssignment(targetId, feeObject),
            []);
    }

    public async Task<VisualPlanLoadResult> LoadXmlAsync(
        string xmlPath,
        CancellationToken cancellationToken = default)
    {
        var result = await _parser.ParseAsync(xmlPath, cancellationToken);
        if (result.Plan is null)
            return result;

        var plan = result.Plan;
        var additionalIssues = new List<VisualIssue>();
        if (File.Exists(plan.SidecarPath))
        {
            var sidecar = await _sidecarStore.ReadAsync(plan.SidecarPath, cancellationToken);
            if (!sidecar.Success || sidecar.Document is null)
            {
                additionalIssues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_AUTOLOAD_FAILED",
                    $"Gespeicherte Änderungen wurden ignoriert: {sidecar.Message}"));
            }
            else if (!string.Equals(
                         sidecar.Document.SourceFingerprint,
                         plan.SourceFingerprint,
                         StringComparison.OrdinalIgnoreCase))
            {
                additionalIssues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_SOURCE_CHANGED",
                    "Die Container-XML wurde seit dem Speichern des visuellen Plans geändert; " +
                    "alte Zuordnungen wurden nicht automatisch übernommen."));
            }
            else
            {
                var documentIssues = ValidateAndApplyDocument(plan, sidecar.Document);
                additionalIssues.AddRange(documentIssues);
            }
        }

        if (additionalIssues.Count > 0)
            plan = CloneWithIssues(plan, additionalIssues);

        SetPlan(plan);
        var allIssues = plan.Issues;
        var success = allIssues.All(issue => issue.Severity != VisualIssueSeverity.Error);
        return new VisualPlanLoadResult(
            success,
            plan,
            allIssues,
            success
                ? "Container-XML und visueller Plan wurden geladen."
                : "Container-XML enthält Fehler; die Generierung bleibt deaktiviert.");
    }

    public async Task<VisualPlanLoadResult> LoadSidecarAsync(
        string sidecarPath,
        CancellationToken cancellationToken = default)
    {
        var sidecar = await _sidecarStore.ReadAsync(sidecarPath, cancellationToken);
        if (!sidecar.Success || sidecar.Document is null)
            return Failure("SIDECAR_READ_FAILED", sidecar.Message);

        var parsed = await _parser.ParseAsync(sidecar.SourceXmlPath, cancellationToken);
        if (parsed.Plan is null)
            return parsed;
        if (parsed.Issues.Any(issue => issue.Severity == VisualIssueSeverity.Error))
            return parsed;

        var plan = parsed.Plan;
        if (!string.Equals(
                sidecar.Document.SourceFingerprint,
                plan.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "SIDECAR_SOURCE_CHANGED",
                "Die referenzierte Container-XML wurde seit dem Speichern geändert. " +
                "Der Plan wurde nicht angewendet.");
        }

        var issues = ValidateAndApplyDocument(plan, sidecar.Document);
        if (issues.Any(issue => issue.Severity == VisualIssueSeverity.Error))
        {
            return new VisualPlanLoadResult(
                false,
                null,
                issues,
                "Der gespeicherte Plan enthält ungültige Zuordnungen.");
        }

        plan.SidecarPath = Path.GetFullPath(sidecarPath);
        if (issues.Count > 0)
            plan = CloneWithIssues(plan, issues);
        SetPlan(plan);
        return new VisualPlanLoadResult(true, plan, plan.Issues, "Gespeicherter visueller Plan wurde geladen.");
    }

    public async Task SaveSidecarAsync(
        string? sidecarPath = null,
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan ?? throw new InvalidOperationException("Es ist kein visueller Plan geladen.");
        var targetPath = string.IsNullOrWhiteSpace(sidecarPath)
            ? plan.SidecarPath
            : Path.GetFullPath(sidecarPath);
        await _sidecarStore.SaveAsync(plan, targetPath, cancellationToken);
        plan.SidecarPath = targetPath;
    }

    public async Task<IReadOnlyList<VisualFeeObject>> DiscoverFeeObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _discovery.DiscoverAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _confirmedDuplicateIdentities.Clear();
        _feeObjects = result.Objects;
        _runtimeObjects = result.RuntimeObjects;
        _feeContainerObjects = result.ContainerObjects;
        _topLevelBasicFrames = result.TopLevelBasicFrames;
        _hasDiscoveredFeeObjects = true;
        _feeSimObjectLinks = [];
        _hasDiscoveredFeeSimObjectLinks = false;
        RemoveStaleObjectAssignments();
        return _feeObjects;
    }

    public async Task<IReadOnlyList<VisualFeeObjectLink>> DiscoverFeeSimObjectLinksAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null || !_hasDiscoveredFeeObjects)
        {
            _feeSimObjectLinks = [];
            _hasDiscoveredFeeSimObjectLinks = false;
            return _feeSimObjectLinks;
        }

        var relevantIds = plan.Assignments.Select(item => item.FeeObjectId)
            .Concat(_feeObjects.Where(item => item.HasExactDuplicate).Select(item => item.Id))
            .ToHashSet(StringComparer.Ordinal);
        var relevantObjects = _runtimeObjects.Where(item => relevantIds.Contains(item.Key))
            .Select(item => item.Value)
            .ToArray();
        var result = await _simObjectLinkDiscovery.DiscoverAsync(relevantObjects, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _feeSimObjectLinks = result.Links;
        _hasDiscoveredFeeSimObjectLinks = true;
        return _feeSimObjectLinks;
    }

    /// <summary>
    /// Removes an object which the SDK has already deleted from the local live
    /// snapshot. This deliberately performs no further vendor call.
    /// </summary>
    public bool ForgetDeletedFeeObject(string feeObjectId)
    {
        var deleted = FindFeeObject(feeObjectId);
        if (deleted is null)
            return false;

        var remaining = _feeObjects.Where(item => !string.Equals(
                item.Id,
                feeObjectId,
                StringComparison.Ordinal))
            .ToArray();
        var duplicateIdentities = remaining.GroupBy(CreateDuplicateIdentity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _feeObjects = remaining.Select(item => item.WithExactDuplicate(
                duplicateIdentities.Contains(CreateDuplicateIdentity(item))))
            .ToArray();
        _runtimeObjects = _runtimeObjects
            .Where(item => !string.Equals(item.Key, feeObjectId, StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        _feeSimObjectLinks = _feeSimObjectLinks.Where(link =>
                !string.Equals(link.ObjectGuidString, deleted.GuidString, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(link.LinkedObjectGuidString, deleted.GuidString, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _feeSignalLinks = _feeSignalLinks.Where(link => !string.Equals(
                link.ObjectGuidString,
                deleted.GuidString,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _confirmedDuplicateIdentities.Clear();

        if (CurrentPlan is { } plan)
        {
            var assignments = plan.Assignments.Where(item => !string.Equals(
                    item.FeeObjectId,
                    feeObjectId,
                    StringComparison.Ordinal))
                .ToArray();
            if (assignments.Length != plan.Assignments.Count)
            {
                plan.ReplaceAssignments(assignments);
                RaisePlanChanged();
            }
        }

        return true;
    }

    public async Task<IReadOnlyList<VisualFeeInterface>> DiscoverFeeInterfacesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _interfaceDiscovery.DiscoverAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _feeInterfaces = result.Interfaces;
        _runtimeInterfaces = result.RuntimeInterfaces;
        _feeSignals = result.Signals;
        _feeSignalLinks = [];
        _hasDiscoveredFeeInterfaces = true;
        _hasDiscoveredFeeSignalLinks = false;
        RemoveStaleSignalAssignments();
        return _feeInterfaces;
    }

    public async Task<IReadOnlyList<VisualFeeSignalLink>> DiscoverFeeSignalLinksAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null || !_hasDiscoveredFeeInterfaces)
        {
            _feeSignalLinks = [];
            _hasDiscoveredFeeSignalLinks = false;
            return _feeSignalLinks;
        }

        var activeSignalNodes = plan.Nodes
            .Where(node => node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                           !plan.IsSignalRemoved(node.Id))
            .ToArray();
        var relevantTags = activeSignalNodes
            .Select(node => node.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantLocations = activeSignalNodes
            .Select(node => node.SourceLocation)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explicitGuids = plan.SignalAssignments
            .Select(assignment => assignment.FeeSignalGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantSignals = _feeSignals.Where(signal =>
                explicitGuids.Contains(signal.GuidString) ||
                relevantTags.Contains(signal.Tag) ||
                relevantLocations.Contains(signal.Location))
            .ToArray();
        var result = await _signalLinkDiscovery.DiscoverAsync(relevantSignals, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _feeSignalLinks = result.Links;
        _hasDiscoveredFeeSignalLinks = true;
        return _feeSignalLinks;
    }

    public VisualSignalConnectionState GetSignalConnectionState(
        string signalNodeId,
        string feeSignalGuid)
    {
        var plan = CurrentPlan;
        var node = plan?.FindNode(signalNodeId);
        if (plan is null || node?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
            return new(VisualSignalConnectionKind.NotRead, "Signalziel ist nicht mehr vorhanden.");
        if (!_hasDiscoveredFeeSignalLinks)
            return new(VisualSignalConnectionKind.NotRead, "FEE-Signalverknüpfungen wurden noch nicht aktualisiert.");

        var links = _feeSignalLinks.Where(link => string.Equals(
                link.SignalGuidString,
                feeSignalGuid,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var container = node.ContainerId is null ? null : plan.FindNode(node.ContainerId);
        if (container is null || !ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor))
            return new(VisualSignalConnectionKind.LinkMissing, "Der Container besitzt kein auflösbares FEE-Ziel.");

        var requiresLink = !string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) ||
                           !string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType) ||
                           descriptor.Targets.Count > 0 ||
                           descriptor.TechnicalHelpers.Count > 0;
        if (!requiresLink)
        {
            return new(
                VisualSignalConnectionKind.LinkMissing,
                "Das Signal wurde gefunden, besitzt aber kein definiertes FEE-Objektziel. Der signal-only Container bleibt deshalb als offene Verknüpfung markiert.");
        }

        var expectedObjectGuids = ResolveExpectedSignalTargetGuids(plan, container, descriptor);
        var matchingLinks = links.Where(link => expectedObjectGuids.Contains(link.ObjectGuidString)).ToArray();
        if (matchingLinks.Length > 0)
        {
            var endpoints = string.Join(", ", matchingLinks.Select(link =>
                    $"{link.ObjectGuidString}/{link.SlotName}{(link.IsIndirect ? " (über MoveBit)" : string.Empty)}")
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return new(
                VisualSignalConnectionKind.Linked,
                $"Vorhandene FEE-Verknüpfung bestätigt: {endpoints}");
        }

        var actual = links.Length == 0
            ? "Das Signal besitzt keine Objektzuordnung."
            : $"Vorhandene Zuordnung gehört zu einem anderen Objekt: {string.Join(", ", links.Select(link => $"{link.ObjectGuidString}/{link.SlotName}"))}";
        return new(
            VisualSignalConnectionKind.LinkMissing,
            $"Signal gefunden, erforderliche Verknüpfung zu '{container.Name}' fehlt. {actual}");
    }

    public VisualSimObjectConnectionState GetSimObjectConnectionState(string targetId) =>
        GetSimObjectConnectionState(targetId, null);

    /// <summary>
    /// Returns the live link state for one assignment. Supplying no object ID
    /// aggregates all assignments of the target and is intentionally stricter:
    /// one missing link keeps the target open while already-linked siblings stay
    /// individually verified in the UI.
    /// </summary>
    public VisualSimObjectConnectionState GetSimObjectConnectionState(
        string targetId,
        string? feeObjectId)
    {
        var plan = CurrentPlan;
        var target = plan?.FindTarget(targetId);
        var container = target is null ? null : plan?.FindNode(target.ContainerId);
        if (plan is null || target is null || container is null)
            return new(VisualSimObjectConnectionKind.NotRead, "SimObject-Ziel ist nicht mehr vorhanden.");

        var assignments = plan.Assignments.Where(item =>
                string.Equals(item.TargetId, targetId, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(feeObjectId) ||
                 string.Equals(item.FeeObjectId, feeObjectId, StringComparison.Ordinal)))
            .ToArray();
        if (assignments.Length == 0)
            return new(
                VisualSimObjectConnectionKind.NotRead,
                string.IsNullOrWhiteSpace(feeObjectId)
                    ? "Noch kein vorhandenes FEE-SimObject zugeordnet."
                    : "Die ausgewählte FEE-SimObject-Zuordnung ist nicht mehr im Plan vorhanden.");
        if (!_hasDiscoveredFeeSimObjectLinks)
            return new(VisualSimObjectConnectionKind.NotRead, "FEE-SimObject-Verknüpfungen wurden noch nicht aktualisiert.");
        if (!ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor))
            return new(VisualSimObjectConnectionKind.LinkMissing, "Containerdefinition ist nicht auflösbar.");
        if (string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName))
        {
            return new(
                VisualSimObjectConnectionKind.NotRequired,
                assignments.Length == 1
                    ? $"FEE-SimObject '{assignments[0].FeeObjectName}' bestätigt; dieser Container erwartet keine SimObject-zu-Logik-Verknüpfung."
                    : "Vorhandene FEE-SimObjects bestätigt; dieser Container erwartet keine SimObject-zu-Logik-Verknüpfung.");
        }

        var expectedLogics = _feeContainerObjects.Where(item =>
                item.Kind == VisualFeeContainerObjectKind.Logic &&
                string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, item.Definition) &&
                Guid.TryParse(item.GuidString, out _))
            .ToArray();
        if (expectedLogics.Length == 0)
        {
            return new(
                VisualSimObjectConnectionKind.LinkMissing,
                $"FEE-SimObject gefunden; passende Logik '{descriptor.ExpectedLogicName}' fehlt.");
        }

        var unlinked = new List<string>();
        foreach (var assignment in assignments)
        {
            var guidText = FindFeeObject(assignment.FeeObjectId)?.GuidString;
            if (!Guid.TryParse(guidText, out var objectGuid) ||
                expectedLogics.Count(logic => IsObjectLinked(objectGuid, Guid.Parse(logic.GuidString))) != 1)
            {
                unlinked.Add(assignment.FeeObjectName);
            }
        }
        var details = string.Join("; ", assignments.SelectMany(assignment =>
            GetFeeObjectConnectionSummary(assignment.FeeObjectId).Details).Distinct(StringComparer.OrdinalIgnoreCase));
        var detailSuffix = string.IsNullOrWhiteSpace(details) ? string.Empty : $" Live-Verknüpfungen: {details}";
        return unlinked.Count == 0
            ? new(
                VisualSimObjectConnectionKind.Linked,
                $"Vorhandene FEE-SimObject-Verknüpfung zu '{container.Name}' bestätigt.{detailSuffix}")
            : new(
                VisualSimObjectConnectionKind.LinkMissing,
                $"FEE-SimObject gefunden; Verknüpfung zur Logik fehlt, ist mehrdeutig oder nicht rücklesbar: {string.Join(", ", unlinked)}.{detailSuffix}");
    }

    private bool IsObjectLinked(Guid runtimeObjectGuid, Guid expectedLogicGuid)
    {
        var objectGuid = runtimeObjectGuid.ToString("D");
        var logicGuid = expectedLogicGuid.ToString("D");
        return _feeSimObjectLinks.Any(link =>
            (string.Equals(link.ObjectGuidString, objectGuid, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(link.LinkedObjectGuidString, logicGuid, StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(link.ObjectGuidString, logicGuid, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(link.LinkedObjectGuidString, objectGuid, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<string> FindVerifiedSignalNodeIds(string feeSignalGuid)
    {
        var bySignal = FindVerifiedSignalNodeIds();
        return bySignal.TryGetValue(feeSignalGuid, out var nodeIds) ? nodeIds : [];
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FindVerifiedSignalNodeIds()
    {
        var plan = CurrentPlan;
        if (plan is null || plan.ExistingInterfaceSelections.Count == 0)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var selectedGuids = plan.ExistingInterfaceSelections
            .Select(item => item.InterfaceGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in plan.Nodes.Where(node =>
                     node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                     !plan.IsSignalRemoved(node.Id)))
        {
            var signal = ResolveSignalForNode(plan, node, selectedGuids);
            if (signal is null || !GetSignalConnectionState(node.Id, signal.GuidString).IsVerified)
                continue;
            if (!result.TryGetValue(signal.GuidString, out var nodeIds))
            {
                nodeIds = [];
                result.Add(signal.GuidString, nodeIds);
            }
            nodeIds.Add(node.Id);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares the current plan with top-level FEE frames carrying exact
    /// Container2FEE provenance. Legacy structural reconstruction remains an
    /// explicit FEE2Container operation because traversing every old root and
    /// all signal assignments would make the normal visual refresh scale badly.
    /// </summary>
    public async Task<IReadOnlySet<string>> DiscoverVerifiedContainerIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var reverseService = new Fee2ContainerService();
        var discovery = await reverseService.DiscoverAsync(
            cancellationToken,
            reconstructLegacyRoots: false,
            knownTopLevelRoots: _topLevelBasicFrames);
        var documents = new List<XDocument>();
        foreach (var root in discovery.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var export = await reverseService.CreateExportAsync(root, cancellationToken);
                documents.Add(export.Snapshot.ContainerDocument);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning($"FEE-Container '{root.Name}' konnte für den Bestandsvergleich nicht rekonstruiert werden: {exception.Message}");
            }
        }

        var source = XDocument.Load(plan.SourceXmlPath, LoadOptions.None);
        return VisualExistingContainerComparer.FindVerifiedContainerIds(plan, source, documents);
    }

    /// <summary>
    /// Reproduces the legacy name-and-type matching for targets which have not
    /// been edited manually. One FEE object remains assignable to only one target.
    /// </summary>
    public int AutoAssignMatches()
    {
        var plan = CurrentPlan;
        if (plan is null || _feeObjects.Count == 0)
            return 0;

        var assignments = plan.Assignments.ToList();
        var assignedObjectIds = assignments
            .Select(assignment => assignment.FeeObjectId)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var before = Capture(plan);

        foreach (var target in plan.Targets)
        {
            if (assignments.Any(assignment => assignment.TargetId == target.Id))
                continue;

            var containerName = plan.FindNode(target.ContainerId)?.Name ?? string.Empty;
            var matches = _feeObjects
                .Where(target.CanAssign)
                .Where(item => !assignedObjectIds.Contains(item.Id))
                .Where(item => string.Equals(item.Name, containerName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            if (!target.AllowMultiSelect)
            {
                // Exact duplicates must remain visible in the plan instead of
                // silently hiding all but the first GUID. Validation marks the
                // target as erroneous and forced execution remains an explicit
                // user decision.
                var exactDuplicates = matches.Where(item => item.HasExactDuplicate).ToArray();
                matches = exactDuplicates.Length > 1 ? exactDuplicates : matches.Take(1).ToArray();
            }

            foreach (var match in matches)
            {
                assignments.Add(ToAssignment(target.Id, match));
                assignedObjectIds.Add(match.Id);
                added++;
            }
        }

        if (added == 0)
            return 0;

        RecordMutation(before);
        plan.ReplaceAssignments(assignments);
        RaisePlanChanged();
        _logger.Information($"{added} FEE-SimObject-Zuordnung(en) automatisch erkannt.");
        return added;
    }

    /// <summary>
    /// Removes sidecar assignments whose scene object was deleted.  Keeping a
    /// stale GUID made validation and runtime binding fail even though the
    /// selected container is able to recreate the missing object.
    /// </summary>
    private void RemoveStaleObjectAssignments()
    {
        var plan = CurrentPlan;
        if (plan is null || !_hasDiscoveredFeeObjects)
            return;

        var availableIds = _feeObjects.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var retained = plan.Assignments
            .Where(assignment => availableIds.Contains(assignment.FeeObjectId))
            .ToArray();
        var removed = plan.Assignments.Count - retained.Length;
        if (removed == 0)
            return;

        plan.ReplaceAssignments(retained);
        _logger.Warning(
            $"{removed} gespeicherte FEE-SimObject-Zuordnung(en) verweisen auf gelöschte Objekte und wurden verworfen. Fehlende Objekte können neu erzeugt werden.");
        RaisePlanChanged();
    }

    private void RemoveStaleSignalAssignments()
    {
        var plan = CurrentPlan;
        if (plan is null || !_hasDiscoveredFeeInterfaces)
            return;

        var availableGuids = _feeSignals
            .Select(item => item.GuidString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = plan.SignalAssignments
            .Where(assignment => availableGuids.Contains(assignment.FeeSignalGuid))
            .ToArray();
        var removed = plan.SignalAssignments.Count - retained.Length;
        if (removed == 0)
            return;

        plan.ReplaceSignalAssignments(retained);
        _logger.Warning(
            $"{removed} gespeicherte FEE-Signalzuordnung(en) verweisen auf gelöschte Variablen und wurden verworfen. Die Signale können neu aufgelöst oder erzeugt werden.");
        RaisePlanChanged();
    }

    public VisualAssignmentResult TryAssign(string targetId, string feeObjectId)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return AssignmentFailure("Es ist kein visueller Plan geladen.", "PLAN_NOT_LOADED");

        var target = plan.FindTarget(targetId);
        if (target is null)
            return AssignmentFailure("Das Zuordnungsziel existiert nicht mehr.", "TARGET_NOT_FOUND", targetId);
        var feeObject = _feeObjects.FirstOrDefault(item => item.Id == feeObjectId);
        if (feeObject is null)
            return AssignmentFailure("Das FEE-SimObject ist nicht mehr verfügbar.", "FEE_OBJECT_NOT_FOUND", targetId);
        if (!target.CanAssign(feeObject))
        {
            return AssignmentFailure(
                $"'{feeObject.Name}' ist nicht kompatibel mit '{target.DisplayName}'.",
                "FEE_OBJECT_INCOMPATIBLE",
                targetId);
        }

        var existing = plan.Assignments.FirstOrDefault(assignment =>
            assignment.TargetId == targetId && assignment.FeeObjectId == feeObjectId);
        if (existing is not null)
            return new VisualAssignmentResult(true, "Die Zuordnung besteht bereits.", existing, []);

        var before = Capture(plan);
        var assignments = plan.Assignments
            .Where(assignment => assignment.FeeObjectId != feeObjectId)
            .Where(assignment => target.AllowMultiSelect || assignment.TargetId != targetId)
            .ToList();
        var added = ToAssignment(targetId, feeObject);
        assignments.Add(added);

        RecordMutation(before);
        plan.ReplaceAssignments(assignments);
        RaisePlanChanged();
        return new VisualAssignmentResult(
            true,
            $"'{feeObject.Name}' wurde '{target.DisplayName}' zugeordnet.",
            added,
            []);
    }

    public VisualAssignmentResult RemoveAssignment(string targetId, string feeObjectId)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return AssignmentFailure("Es ist kein visueller Plan geladen.", "PLAN_NOT_LOADED");

        var removed = plan.Assignments.FirstOrDefault(assignment =>
            assignment.TargetId == targetId && assignment.FeeObjectId == feeObjectId);
        if (removed is null)
            return AssignmentFailure("Die Zuordnung existiert nicht mehr.", "ASSIGNMENT_NOT_FOUND", targetId);

        var before = Capture(plan);
        plan.ReplaceAssignments(plan.Assignments.Where(assignment => assignment != removed));
        RecordMutation(before);
        RaisePlanChanged();
        return new VisualAssignmentResult(true, "Zuordnung wurde entfernt.", removed, []);
    }

    public VisualSignalAssignmentResult TryAssignSignal(string signalNodeId, string feeSignalGuid)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return SignalAssignmentFailure("Es ist kein visueller Plan geladen.", "PLAN_NOT_LOADED");
        var node = plan.FindNode(signalNodeId);
        if (node?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
            return SignalAssignmentFailure("Das Ziel ist kein Container-Signal.", "SIGNAL_TARGET_NOT_FOUND", signalNodeId);
        var signal = _feeSignals.FirstOrDefault(item => string.Equals(
            item.GuidString,
            feeSignalGuid,
            StringComparison.OrdinalIgnoreCase));
        if (signal is null)
            return SignalAssignmentFailure("Das FEE-Signal ist nicht mehr verfügbar.", "FEE_SIGNAL_NOT_FOUND", signalNodeId);
        if (plan.ExistingInterfaceSelections.Count == 0)
        {
            return SignalAssignmentFailure(
                "Vor der Signalzuordnung muss in 'Gefundene FEE-Signale' mindestens ein Interface ausgewählt werden.",
                "FEE_INTERFACE_NOT_SELECTED",
                signalNodeId);
        }
        if (!IsSelectedInterface(plan, signal.InterfaceGuidString))
        {
            return SignalAssignmentFailure(
                $"Signal '{signal.Tag}' gehört zu keinem der ausgewählten Interfaces.",
                "FEE_SIGNAL_WRONG_INTERFACE",
                signalNodeId);
        }

        var assignment = new VisualSignalAssignment(
            signalNodeId,
            signal.GuidString,
            signal.Tag,
            signal.InterfaceName);
        if (plan.SignalAssignments.Any(item => item == assignment))
            return new VisualSignalAssignmentResult(true, "Die Signalzuordnung besteht bereits.", assignment, []);

        var before = Capture(plan);
        plan.ReplaceSignalAssignments(plan.SignalAssignments
            .Where(item => !string.Equals(item.SignalNodeId, signalNodeId, StringComparison.Ordinal))
            .Append(assignment));
        RecordMutation(before);
        RaisePlanChanged();
        return new VisualSignalAssignmentResult(
            true,
            $"FEE-Signal '{signal.Tag}' aus Interface '{signal.InterfaceName}' wurde ausdrücklich zugeordnet.",
            assignment,
            []);
    }

    /// <summary>
    /// Extends one container with existing interface signals. The new entries
    /// deliberately start without a slot so the user must choose an allowed
    /// slot before generation.
    /// </summary>
    public VisualSignalAssignmentResult AddSignals(
        string containerId,
        IEnumerable<string> feeSignalGuids)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return SignalAssignmentFailure("Es ist kein visueller Plan geladen.", "PLAN_NOT_LOADED");
        var container = plan.FindNode(containerId);
        if (container?.Kind != VisualNodeKind.Container ||
            !ContainerMetadataCatalog.TryGet(container.TypeName, out _))
        {
            return SignalAssignmentFailure(
                "Signale können nur einem unterstützten Container hinzugefügt werden.",
                "SIGNAL_CONTAINER_NOT_SUPPORTED",
                containerId);
        }
        if (plan.ExistingInterfaceSelections.Count == 0)
        {
            return SignalAssignmentFailure(
                "Vor dem Hinzufügen muss mindestens ein Interface ausgewählt werden.",
                "FEE_INTERFACE_NOT_SELECTED",
                containerId);
        }

        var requested = feeSignalGuids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var signals = requested
            .Select(guid => _feeSignals.FirstOrDefault(signal => string.Equals(
                signal.GuidString,
                guid,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (signals.Any(signal => signal is null))
            return SignalAssignmentFailure("Mindestens ein FEE-Signal ist nicht mehr verfügbar.", "FEE_SIGNAL_NOT_FOUND", containerId);
        if (signals.Any(signal => !IsSelectedInterface(plan, signal!.InterfaceGuidString)))
        {
            return SignalAssignmentFailure(
                "Es dürfen nur Signale der ausgewählten Interfaces hinzugefügt werden.",
                "FEE_SIGNAL_WRONG_INTERFACE",
                containerId);
        }

        var newSignals = signals
            .Cast<VisualFeeSignal>()
            .Where(signal => !plan.SignalAssignments.Any(item => string.Equals(
                item.FeeSignalGuid,
                signal.GuidString,
                StringComparison.OrdinalIgnoreCase)))
            .Where(signal => !plan.AddedSignals.Any(item =>
                string.Equals(item.ContainerId, containerId, StringComparison.Ordinal) &&
                string.Equals(item.FeeSignalGuid, signal.GuidString, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (newSignals.Length == 0)
            return new VisualSignalAssignmentResult(true, "Die ausgewählten Signale sind bereits im Containerplan enthalten.", null, []);

        var before = Capture(plan);
        var added = plan.AddedSignals.ToList();
        var assignments = plan.SignalAssignments.ToList();
        VisualSignalAssignment? lastAssignment = null;
        foreach (var signal in newSignals)
        {
            var nodeId = $"{containerId}:added-signal:{StableId.Encode(signal.GuidString)}";
            added.Add(new VisualAddedSignal(
                nodeId,
                containerId,
                $"{containerId}:signals",
                signal.GuidString,
                signal.Tag,
                signal.InterfaceGuidString,
                signal.InterfaceName,
                signal.Address,
                signal.Path,
                signal.DataType,
                signal.Usage));
            lastAssignment = new VisualSignalAssignment(
                nodeId,
                signal.GuidString,
                signal.Tag,
                signal.InterfaceName);
            assignments.Add(lastAssignment);
        }
        plan.ReplaceAddedSignals(added);
        plan.ReplaceSignalAssignments(assignments);
        RecordMutation(before);
        RaisePlanChanged();
        return new VisualSignalAssignmentResult(
            true,
            $"{newSignals.Length} Signal(e) wurden ergänzt. Vor der Generierung muss für jeden neuen Eintrag ein Slot ausgewählt werden.",
            lastAssignment,
            []);
    }

    /// <summary>
    /// Assigns existing FEE variables directly to one declared container slot.
    /// Existing source entries are reused first; additional PLC_IN fan-in
    /// entries are created in the sidecar-backed effective document.
    /// </summary>
    public VisualSignalAssignmentResult AssignSignalsToSlot(
        string containerId,
        string slot,
        IEnumerable<string> feeSignalGuids)
    {
        var plan = CurrentPlan;
        var container = plan?.FindNode(containerId);
        if (plan is null || container?.Kind != VisualNodeKind.Container ||
            !ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor))
        {
            return SignalAssignmentFailure(
                "Das Signalziel gehört zu keinem unterstützten Container.",
                "SIGNAL_SLOT_CONTAINER_NOT_SUPPORTED",
                containerId);
        }
        var canonicalSlot = descriptor.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate, slot, StringComparison.OrdinalIgnoreCase));
        if (canonicalSlot is null)
            return SignalAssignmentFailure($"Slot '{slot}' ist für diesen Container nicht zulässig.", "SIGNAL_SLOT_UNKNOWN", containerId);
        if (plan.ExistingInterfaceSelections.Count == 0)
            return SignalAssignmentFailure("Zuerst mindestens ein Interface auswählen.", "FEE_INTERFACE_NOT_SELECTED", containerId);

        var requestedGuids = feeSignalGuids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requestedGuids.Length == 0)
            return SignalAssignmentFailure("Es wurde kein FEE-Signal ausgewählt.", "FEE_SIGNAL_NOT_SELECTED", containerId);
        var signals = requestedGuids.Select(guid => _feeSignals.FirstOrDefault(signal => string.Equals(
                signal.GuidString,
                guid,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (signals.Any(signal => signal is null))
            return SignalAssignmentFailure("Mindestens ein FEE-Signal ist nicht mehr verfügbar.", "FEE_SIGNAL_NOT_FOUND", containerId);
        if (signals.Any(signal => !IsSelectedInterface(plan, signal!.InterfaceGuidString)))
        {
            return SignalAssignmentFailure(
                "Es dürfen nur Signale der ausgewählten Interfaces zugeordnet werden.",
                "FEE_SIGNAL_WRONG_INTERFACE",
                containerId);
        }

        var allowsMultiple = ContainerSlotMultiplicityPolicy.IsPlcInput(canonicalSlot);
        if (!allowsMultiple && requestedGuids.Length > 1)
        {
            return SignalAssignmentFailure(
                $"Slot '{canonicalSlot}' ist exklusiv und erlaubt nur ein Signal.",
                "SIGNAL_SLOT_EXCLUSIVE",
                containerId);
        }

        var existingNodes = plan.Nodes.Where(node =>
                string.Equals(node.ContainerId, containerId, StringComparison.Ordinal) &&
                node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                !plan.IsSignalRemoved(node.Id) &&
                string.Equals(plan.GetEffectiveSlot(node), canonicalSlot, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!allowsMultiple && existingNodes.Count > 1)
        {
            return SignalAssignmentFailure(
                $"Slot '{canonicalSlot}' ist im ContainerFile bereits mehrfach vorhanden und muss zuerst bereinigt werden.",
                "SIGNAL_SLOT_ALREADY_DUPLICATED",
                containerId);
        }

        var previousNodes = requestedGuids.ToDictionary(
            guid => guid,
            guid => plan.SignalAssignments
                .Where(assignment => string.Equals(
                    assignment.FeeSignalGuid,
                    guid,
                    StringComparison.OrdinalIgnoreCase))
                .Select(assignment => plan.FindNode(assignment.SignalNodeId))
                .FirstOrDefault(node => node is not null),
            StringComparer.OrdinalIgnoreCase);
        var foreignNode = plan.SignalAssignments
            .Where(assignment => requestedGuids.Contains(
                assignment.FeeSignalGuid,
                StringComparer.OrdinalIgnoreCase))
            .Select(assignment => plan.FindNode(assignment.SignalNodeId))
            .FirstOrDefault(node => node is not null &&
                                    !string.Equals(node.ContainerId, containerId, StringComparison.Ordinal));
        if (foreignNode is not null)
        {
            var foreignContainer = plan.FindNode(foreignNode.ContainerId ?? string.Empty)?.Name ??
                                   foreignNode.ContainerId;
            return SignalAssignmentFailure(
                $"Das FEE-Signal ist bereits Container '{foreignContainer}' zugeordnet. " +
                "Die bestehende Zuordnung zuerst entfernen, damit keine unbemerkte Doppelverwendung entsteht.",
                "FEE_SIGNAL_ASSIGNED_TO_OTHER_CONTAINER",
                foreignNode.Id);
        }

        var before = Capture(plan);
        var assignments = plan.SignalAssignments
            .Where(assignment => !requestedGuids.Contains(assignment.FeeSignalGuid, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var addedSignals = plan.AddedSignals.ToList();
        var slotOverrides = plan.SlotOverrides.ToList();
        var availableNodes = allowsMultiple
            ? existingNodes.Where(node => assignments.All(assignment =>
                !string.Equals(assignment.SignalNodeId, node.Id, StringComparison.Ordinal))).ToList()
            : existingNodes.Take(1).ToList();
        VisualSignalAssignment? lastAssignment = null;

        foreach (var signal in signals.Cast<VisualFeeSignal>())
        {
            VisualNode? targetNode = previousNodes.GetValueOrDefault(signal.GuidString);
            if (targetNode is not null)
                availableNodes.RemoveAll(node => string.Equals(node.Id, targetNode.Id, StringComparison.Ordinal));
            else
            {
                targetNode = availableNodes.FirstOrDefault();
                if (targetNode is not null)
                    availableNodes.RemoveAt(0);
            }
            if (targetNode is null)
            {
                var nodeId = $"{containerId}:added-signal:{StableId.Encode(signal.GuidString)}";
                var existingAdded = addedSignals.FirstOrDefault(item => string.Equals(
                    item.NodeId,
                    nodeId,
                    StringComparison.Ordinal));
                if (existingAdded is null)
                {
                    addedSignals.Add(new VisualAddedSignal(
                        nodeId,
                        containerId,
                        $"{containerId}:signals",
                        signal.GuidString,
                        signal.Tag,
                        signal.InterfaceGuidString,
                        signal.InterfaceName,
                        signal.Address,
                        signal.Path,
                        signal.DataType,
                        signal.Usage));
                }
                plan.ReplaceAddedSignals(addedSignals);
                targetNode = plan.FindNode(nodeId);
                if (targetNode is null)
                    return SignalAssignmentFailure("Der zusätzliche Signaleintrag konnte nicht angelegt werden.", "ADDED_SIGNAL_CREATE_FAILED", containerId);
            }

            slotOverrides.RemoveAll(item => string.Equals(item.SignalNodeId, targetNode.Id, StringComparison.Ordinal));
            if (!string.Equals(targetNode.Slot, canonicalSlot, StringComparison.Ordinal))
                slotOverrides.Add(new VisualSlotOverride(targetNode.Id, canonicalSlot));

            assignments.RemoveAll(assignment => string.Equals(
                assignment.SignalNodeId,
                targetNode.Id,
                StringComparison.Ordinal));
            lastAssignment = new VisualSignalAssignment(
                targetNode.Id,
                signal.GuidString,
                signal.Tag,
                signal.InterfaceName);
            assignments.Add(lastAssignment);
        }

        plan.ReplaceAddedSignals(addedSignals);
        plan.ReplaceSlotOverrides(slotOverrides);
        plan.ReplaceSignalAssignments(assignments);
        RecordMutation(before);
        RaisePlanChanged();
        return new VisualSignalAssignmentResult(
            true,
            $"{requestedGuids.Length} FEE-Signal(e) wurden Slot '{canonicalSlot}' zugeordnet.",
            lastAssignment,
            []);
    }

    public VisualSignalAssignmentResult RemoveSignal(string signalNodeId)
    {
        var plan = CurrentPlan;
        var node = plan?.FindNode(signalNodeId);
        if (plan is null || node?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
            return SignalAssignmentFailure("Das ausgewählte Element ist kein Containersignal.", "SIGNAL_NODE_NOT_FOUND", signalNodeId);

        var wasAdded = plan.IsAddedSignal(signalNodeId);
        var before = Capture(plan);
        plan.ReplaceSignalAssignments(plan.SignalAssignments.Where(item =>
            !string.Equals(item.SignalNodeId, signalNodeId, StringComparison.Ordinal)));
        plan.ReplaceSlotOverrides(plan.SlotOverrides.Where(item =>
            !string.Equals(item.SignalNodeId, signalNodeId, StringComparison.Ordinal)));
        if (wasAdded)
        {
            plan.ReplaceAddedSignals(plan.AddedSignals.Where(item =>
                !string.Equals(item.NodeId, signalNodeId, StringComparison.Ordinal)));
        }
        else
            plan.ReplaceRemovedSignalNodeIds(plan.RemovedSignalNodeIds.Append(signalNodeId));
        RecordMutation(before);
        RaisePlanChanged();
        return new VisualSignalAssignmentResult(
            true,
            wasAdded
                ? "Zusätzliches Signal wurde aus dem Containerplan entfernt."
                : "Signal wurde aus dem wirksamen Containerplan entfernt. Die unveränderte Quelldatei kann über Rückgängig wiederhergestellt werden.",
            null,
            []);
    }

    public async Task SaveEffectiveContainerXmlAsync(
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan ?? throw new InvalidOperationException("Es ist kein visueller Plan geladen.");
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveDocument = RuntimeVisualPlanBinder.CreateEffectiveDocument(plan);
        var includedIds = CreateEffectiveIncludedContainerIds(plan, effectiveDocument);
        var snapshot = FeeContainerProvenanceCodec.Create(
            effectiveDocument,
            includedIds,
            plan.SourceFingerprint);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () => FeeContainerProvenanceCodec.SaveAtomically(snapshot, targetPath),
            cancellationToken);
    }

    private static IReadOnlySet<string> CreateEffectiveIncludedContainerIds(
        VisualPlan plan,
        XDocument effectiveDocument)
    {
        var effectiveContainers = effectiveDocument.Descendants()
            .Where(element => element.Name.LocalName == "Container")
            .ToArray();
        var planContainers = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container)
            .ToArray();
        if (effectiveContainers.Length != planContainers.Length)
        {
            return planContainers
                .Where(node => plan.IsGenerationSelected(node.Id) ||
                               !ContainerMetadataCatalog.TryGet(node.TypeName, out _))
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
        }

        // A type override changes the stable container identity because the type
        // is part of that identity. Recreate the IDs from the effective XML while
        // retaining the user's selection from the corresponding plan node.
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var includedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < effectiveContainers.Length; index++)
        {
            var planContainer = planContainers[index];
            if (!plan.IsGenerationSelected(planContainer.Id) &&
                ContainerMetadataCatalog.TryGet(planContainer.TypeName, out _))
                continue;

            var element = effectiveContainers[index];
            var sourceId = element.Attribute("id")?.Value ?? string.Empty;
            var component = element.Elements().FirstOrDefault(child =>
                child.Name.LocalName == "Component")?.Value ?? string.Empty;
            var type = element.Elements().FirstOrDefault(child =>
                child.Name.LocalName == "Type")?.Value ?? string.Empty;
            var identity = $"{sourceId}\u001f{component}\u001f{type}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = ++occurrence;
            includedIds.Add(ContainerXmlVisualPlanParser.CreateContainerId(
                sourceId,
                component,
                type,
                occurrence));
        }

        return includedIds;
    }

    public bool SetCreationRequested(string containerId, bool requested)
    {
        var plan = CurrentPlan;
        var container = plan?.FindNode(containerId);
        if (plan is null || container is null || !container.SupportsCreation)
            return false;
        if (plan.IsCreationRequested(containerId) == requested)
            return true;

        var before = Capture(plan);
        var requests = plan.CreationRequests
            .Where(item => item.ContainerId != containerId)
            .ToList();
        if (!requested)
            requests.Add(new VisualCreationRequest(containerId, false));
        plan.ReplaceCreationRequests(requests);
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    /// <summary>Includes or excludes one complete legacy container generation unit.</summary>
    public bool SetGenerationSelected(string containerId, bool selected)
    {
        var plan = CurrentPlan;
        var container = plan?.FindNode(containerId);
        if (plan is null || container?.Kind != VisualNodeKind.Container ||
            !ContainerMetadataCatalog.TryGet(container.TypeName, out _))
            return false;
        if (plan.IsGenerationSelected(containerId) == selected)
            return true;

        var before = Capture(plan);
        var selections = plan.GenerationSelections
            .Where(item => item.ContainerId != containerId)
            .ToList();
        if (!selected)
            selections.Add(new VisualGenerationSelection(containerId, false));
        plan.ReplaceGenerationSelections(selections);
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    /// <summary>Selects or deselects all supported legacy container units in one undo step.</summary>
    public int SetAllGenerationSelected(bool selected)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return 0;

        var containers = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container &&
                           ContainerMetadataCatalog.TryGet(node.TypeName, out _))
            .ToArray();
        var changed = containers.Count(node => plan.IsGenerationSelected(node.Id) != selected);
        if (changed == 0)
            return 0;

        var before = Capture(plan);
        plan.ReplaceGenerationSelections(selected
            ? []
            : containers.Select(node => new VisualGenerationSelection(node.Id, false)));
        RecordMutation(before);
        RaisePlanChanged();
        return changed;
    }

    /// <summary>Deselects fully verified containers in one consistent plan update.</summary>
    public int DeselectVerifiedContainers(IReadOnlySet<string> containerIds)
    {
        var plan = CurrentPlan;
        if (plan is null || containerIds.Count == 0)
            return 0;

        var ids = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container &&
                           containerIds.Contains(node.Id) &&
                           plan.IsGenerationSelected(node.Id))
            .Select(node => node.Id)
            .ToArray();
        if (ids.Length == 0)
            return 0;

        var before = Capture(plan);
        var excluded = plan.GenerationSelections
            .Where(item => !ids.Contains(item.ContainerId, StringComparer.Ordinal))
            .Concat(ids.Select(id => new VisualGenerationSelection(id, false)))
            .ToArray();
        plan.ReplaceGenerationSelections(excluded);
        RecordMutation(before);
        RaisePlanChanged();
        return ids.Length;
    }

    /// <summary>Enables or disables creation for every supported container in one undo step.</summary>
    public int SetAllCreationRequested(bool requested)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return 0;

        var containers = plan.Nodes
            .Where(node => node.Kind == VisualNodeKind.Container && node.SupportsCreation)
            .ToArray();
        var changed = containers.Count(node => plan.IsCreationRequested(node.Id) != requested);
        if (changed == 0)
            return 0;

        var before = Capture(plan);
        plan.ReplaceCreationRequests(requested
            ? []
            : containers.Select(node => new VisualCreationRequest(node.Id, false)));
        RecordMutation(before);
        RaisePlanChanged();
        return changed;
    }

    public bool SetExistingInterface(VisualFeeInterface? feeInterface)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return false;

        var next = feeInterface is null
            ? null
            : new VisualExistingInterfaceSelection(feeInterface.GuidString, feeInterface.Name);
        if (Equals(plan.ExistingInterfaceSelection, next))
            return true;

        var before = Capture(plan);
        plan.SetExistingInterfaceSelection(next);
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    public bool SetExistingInterfaces(IEnumerable<VisualFeeInterface> feeInterfaces)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return false;

        var next = feeInterfaces
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.GuidString))
            .Select(item => new VisualExistingInterfaceSelection(item.GuidString, item.Name))
            .DistinctBy(item => item.InterfaceGuid, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (plan.ExistingInterfaceSelections.SequenceEqual(next))
            return true;

        var before = Capture(plan);
        plan.SetExistingInterfaceSelections(next);
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    public bool SetSlotOverride(string signalNodeId, string slot)
    {
        var plan = CurrentPlan;
        var signalNode = plan?.FindNode(signalNodeId);
        if (plan is null || signalNode?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal) ||
            string.IsNullOrWhiteSpace(signalNode.ContainerId) ||
            plan.FindNode(signalNode.ContainerId) is not { } containerNode ||
            !ContainerMetadataCatalog.TryGet(containerNode.TypeName, out var descriptor))
            return false;

        var canonicalSlot = descriptor.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate, slot?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (canonicalSlot is null)
            return false;
        var occupiedByOtherSignals = plan.Nodes.Count(node =>
            node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
            !string.Equals(node.Id, signalNodeId, StringComparison.Ordinal) &&
            string.Equals(node.ContainerId, signalNode.ContainerId, StringComparison.Ordinal) &&
            string.Equals(plan.GetEffectiveSlot(node), canonicalSlot, StringComparison.OrdinalIgnoreCase));
        if (ContainerSlotMultiplicityPolicy.GetDuplicateError(canonicalSlot, occupiedByOtherSignals + 1) is not null)
            return false;
        if (string.Equals(plan.GetEffectiveSlot(signalNode), canonicalSlot, StringComparison.Ordinal))
            return true;

        var before = Capture(plan);
        var overrides = plan.SlotOverrides
            .Where(item => !string.Equals(item.SignalNodeId, signalNodeId, StringComparison.Ordinal))
            .ToList();
        if (!string.Equals(signalNode.Slot, canonicalSlot, StringComparison.Ordinal))
            overrides.Add(new VisualSlotOverride(signalNodeId, canonicalSlot));
        plan.ReplaceSlotOverrides(overrides);
        RecordMutation(before);
        RaisePlanChanged();
        return true;
    }

    public VisualValidationResult Validate()
    {
        var plan = CurrentPlan;
        if (plan is null)
        {
            var issue = new VisualIssue(
                VisualIssueSeverity.Error,
                "PLAN_NOT_LOADED",
                "Es ist kein visueller Plan geladen.");
            return new VisualValidationResult(false, [issue]);
        }

        var issues = plan.Issues
            .Where(issue => issue.Code != "SIGNAL_SLOT_UNKNOWN" ||
                            issue.NodeId is null ||
                            plan.SlotOverrides.All(item => !string.Equals(
                                item.SignalNodeId,
                                issue.NodeId,
                                StringComparison.Ordinal)))
            .ToList();
        foreach (var added in plan.AddedSignals)
        {
            var node = plan.FindNode(added.NodeId);
            if (node is null || string.IsNullOrWhiteSpace(plan.GetEffectiveSlot(node)))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "ADDED_SIGNAL_SLOT_REQUIRED",
                    $"Für das zusätzlich eingefügte Signal '{added.FeeSignalTag}' muss ein erwarteter Slot ausgewählt werden.",
                    added.NodeId));
            }
            if (plan.ExistingInterfaceSelections.Count > 0 &&
                !IsSelectedInterface(plan, added.FeeInterfaceGuid))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "ADDED_SIGNAL_WRONG_INTERFACE",
                    $"Das zusätzliche Signal '{added.FeeSignalTag}' gehört zu keinem aktuell ausgewählten Interface.",
                    added.NodeId));
            }
        }
        foreach (var container in plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container))
        {
            var signalNodes = plan.Nodes.Where(node =>
                    string.Equals(node.ContainerId, container.Id, StringComparison.Ordinal) &&
                    node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                    !plan.IsSignalRemoved(node.Id))
                .ToArray();
            foreach (var slotGroup in signalNodes
                         .Select(node => (Node: node, Slot: plan.GetEffectiveSlot(node)))
                         .Where(item => !string.IsNullOrWhiteSpace(item.Slot))
                         .GroupBy(item => item.Slot, StringComparer.OrdinalIgnoreCase))
            {
                var message = ContainerSlotMultiplicityPolicy.GetDuplicateError(slotGroup.Key, slotGroup.Count());
                if (message is null)
                    continue;
                foreach (var item in slotGroup)
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        "DUPLICATE_EXCLUSIVE_SLOT",
                        message,
                        item.Node.Id));
                }
            }

            var hasRuntimeObject = plan.Nodes.Any(node =>
                string.Equals(node.ContainerId, container.Id, StringComparison.Ordinal) &&
                node.Kind is VisualNodeKind.Logic or VisualNodeKind.SimObjectTarget or VisualNodeKind.TechnicalHelper);
            if (!hasRuntimeObject && signalNodes.Length > 0 && plan.IsGenerationSelected(container.Id))
            {
                var isUnknownContainer =
                    string.Equals(container.TypeName, "unknown", StringComparison.OrdinalIgnoreCase) ||
                    signalNodes.All(node => node.Kind == VisualNodeKind.UnknownSignal);
                if (isUnknownContainer)
                {
                    // Unknown is the deliberate interface-only fallback. The
                    // parser's CONTAINER_TYPE_UNKNOWN warning remains visible;
                    // an additional signal-only error would be contradictory.
                    continue;
                }
                var isDeclaredSignalOnly = ContainerMetadataCatalog.TryGet(container.TypeName, out var descriptor) &&
                                           string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) &&
                                           string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType) &&
                                           descriptor.Targets.Count == 0 &&
                                           descriptor.TechnicalHelpers.Count == 0;
                issues.Add(new VisualIssue(
                    isDeclaredSignalOnly ? VisualIssueSeverity.Warning : VisualIssueSeverity.Error,
                    isDeclaredSignalOnly ? "SIGNAL_ONLY_CONTAINER" : "SIGNAL_ONLY_CONTAINER_UNDEFINED",
                    isDeclaredSignalOnly
                        ? $"Container '{container.Name}' ist als signal-only Typ definiert. Die Signale werden im Interface berücksichtigt, bleiben ohne FEE-Objektziel jedoch als offene Verknüpfung markiert."
                        : $"Container '{container.Name}' besteht ausschließlich aus Signalen, obwohl der Typ kein signal-only Container ist. Wahrscheinlich fehlt eine Containerdefinition oder ein erwartetes FEE-Objekt.",
                    container.Id));
            }
        }
        foreach (var group in plan.Assignments.GroupBy(assignment => assignment.FeeObjectId))
        {
            if (group.Count() > 1)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "FEE_OBJECT_ASSIGNED_MULTIPLE_TIMES",
                    $"FEE-Objekt '{group.First().FeeObjectName}' wurde mehrfach zugeordnet."));
            }
        }

        if (_hasDiscoveredFeeObjects)
        {
            foreach (var duplicateGroup in _feeObjects
                         .Where(item => item.HasExactDuplicate)
                         .GroupBy(item => string.Join("\u001f",
                             item.Name.Trim(),
                             item.TypeName,
                             item.FeeType.Trim(),
                             item.ParentGuidString.Trim()),
                             StringComparer.OrdinalIgnoreCase))
            {
                var sample = duplicateGroup.First();
                var isConfirmed = _confirmedDuplicateIdentities.Contains(CreateDuplicateIdentity(sample));
                var isMotionJoint = string.Equals(sample.FeeType, "MotionJoint", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(sample.TypeName, "MotionJoint", StringComparison.OrdinalIgnoreCase) ||
                                    sample.TypeName.EndsWith("FeeJoint", StringComparison.OrdinalIgnoreCase);
                var matchingTargets = plan.Targets.Where(target =>
                        target.CanAssign(sample) &&
                        string.Equals(
                            plan.FindNode(target.ContainerId)?.Name,
                            sample.Name,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var nodeIds = matchingTargets.Length == 0
                    ? new string?[] { null }
                    : matchingTargets.Select(target => (string?)target.Id).ToArray();
                foreach (var nodeId in nodeIds)
                {
                    issues.Add(new VisualIssue(
                        isConfirmed || isMotionJoint
                            ? VisualIssueSeverity.Warning
                            : VisualIssueSeverity.Error,
                        "DUPLICATE_FEE_SIMOBJECT_IDENTITY",
                        $"{duplicateGroup.Count()} identische FEE-SimObjects '{sample.Name}' vom Typ " +
                        $"'{sample.FeeType}' wurden unter demselben Parent '{sample.ParentName}' gefunden. " +
                        (isConfirmed
                            ? "Der Mehrfachfund wurde für diese Sitzung ausdrücklich bestätigt."
                            : isMotionJoint
                                ? "Mehrere MotionJoints können durch die CAD-Struktur beabsichtigt sein; der Mehrfachfund bleibt deshalb als Warnung zur Sichtprüfung erhalten."
                            : "Die GUIDs sind unterschiedlich; im Strukturbaum einen konkreten Treffer bestätigen oder die Duplikate im FEE-Projekt bereinigen."),
                        nodeId));
                }
            }
        }

        foreach (var target in plan.Targets)
        {
            var targetAssignments = plan.Assignments
                .Where(assignment => assignment.TargetId == target.Id)
                .ToArray();
            if (targetAssignments.Length == 0 &&
                plan.IsGenerationSelected(target.ContainerId) &&
                !plan.IsCreationRequested(target.ContainerId))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "SIM_OBJECT_TARGET_UNASSIGNED",
                    $"Für '{target.DisplayName}' fehlt ein verfügbares FEE-SimObject. " +
                    "Ein Objekt zuordnen, die Erzeugung aktivieren oder den Container abwählen.",
                    target.Id));
            }
            if (!target.AllowMultiSelect && targetAssignments.Length > 1)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "SINGLE_TARGET_HAS_MULTIPLE_OBJECTS",
                    $"Ziel '{target.DisplayName}' erlaubt nur ein Objekt.",
                    target.Id));
            }

            foreach (var assignment in targetAssignments)
            {
                var discovered = _feeObjects.FirstOrDefault(item => item.Id == assignment.FeeObjectId);
                if (_hasDiscoveredFeeObjects && discovered is null)
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        "ASSIGNED_FEE_OBJECT_MISSING",
                        $"FEE-Objekt '{assignment.FeeObjectName}' ist nicht mehr vorhanden.",
                        target.Id));
                }
                else if (discovered is not null && !target.CanAssign(discovered))
                {
                    issues.Add(new VisualIssue(
                        VisualIssueSeverity.Error,
                        "ASSIGNED_FEE_OBJECT_INCOMPATIBLE",
                        $"FEE-Objekt '{assignment.FeeObjectName}' ist nicht kompatibel.",
                        target.Id));
                }
            }
        }

        var selectedContainers = plan.Nodes.Where(node =>
                node.Kind == VisualNodeKind.Container &&
                ContainerMetadataCatalog.TryGet(node.TypeName, out _) &&
                plan.IsGenerationSelected(node.Id))
            .ToArray();
        if (selectedContainers.Length == 0)
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Warning,
                "NO_CONTAINERS_SELECTED",
                "Es ist kein unterstützter Container zur Generierung ausgewählt."));
        }

        foreach (var assignment in plan.Assignments.Where(assignment => plan.FindTarget(assignment.TargetId) is null))
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Error,
                "ASSIGNMENT_TARGET_MISSING",
                $"Das Ziel für '{assignment.FeeObjectName}' existiert nicht mehr.",
                assignment.TargetId));
        }

        var distinct = issues
            .DistinctBy(issue => (issue.Severity, issue.Code, issue.Message, issue.NodeId))
            .ToArray();
        return new VisualValidationResult(
            distinct.All(issue => issue.Severity != VisualIssueSeverity.Error),
            distinct);
    }

    public async Task<VisualExecutionResult> ExecuteAsync(
        IReadOnlyList<VisualIssue>? acceptedValidationErrors = null,
        IProgress<VisualGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return new VisualExecutionResult(false, "Es ist kein visueller Plan geladen.", Validate().Issues);

        // Always refresh before a write. A previous generation changes both
        // scene objects and variables; reusing the old snapshot caused the
        // second click to recreate objects/interfaces and eventually stall.
        await DiscoverFeeObjectsAsync(cancellationToken);
        await DiscoverFeeInterfacesAsync(cancellationToken);
        AutoAssignMatches();

        var validation = Validate();
        if (!validation.IsValid && acceptedValidationErrors is null)
        {
            return new VisualExecutionResult(
                false,
                "Der Plan enthält Fehler und wurde nicht ausgeführt.",
                validation.Issues);
        }

        var currentErrors = validation.Issues
            .Where(issue => issue.Severity == VisualIssueSeverity.Error)
            .ToArray();
        // The confirmation belongs to this complete start operation. Refreshing
        // FEE immediately before the write may refine the same validation
        // findings; forcing a second click would neither add information nor
        // improve safety. A runtime-only preflight conflict is returned with
        // RequiresOverrideConfirmation; the UI can confirm it and retry this
        // still read-only stage in the same start operation.
        var effectiveAcceptedErrors = currentErrors.Length > 0
            ? currentErrors
            : acceptedValidationErrors?
                .Where(issue => issue.Severity == VisualIssueSeverity.Error)
                .ToArray() ?? [];
        return await _executor.ExecuteAsync(
            plan,
            _runtimeObjects,
            _runtimeInterfaces,
            effectiveAcceptedErrors,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Reuses existing FEE logic objects and writes only the configured
    /// SimObject-to-logic slot assignments. No container, signal or interface
    /// is created in this mode.
    /// </summary>
    public async Task<VisualExecutionResult> LinkExistingAssignmentsOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return new VisualExecutionResult(false, "Es ist kein visueller Plan geladen.", Validate().Issues);

        if (_runtimeObjects.Count == 0)
        {
            await DiscoverFeeObjectsAsync(cancellationToken);
            AutoAssignMatches();
        }

        var validation = Validate();
        if (!validation.IsValid)
            return new VisualExecutionResult(false, "Der Plan enthält Fehler und wurde nicht verknüpft.", validation.Issues);

        return await _linkExecutor.ExecuteAsync(plan, _runtimeObjects, cancellationToken);
    }

    /// <summary>
    /// Links variables from the selected existing interface to existing FEE
    /// objects only. No object, interface or variable is created.
    /// </summary>
    public async Task<VisualExecutionResult> LinkExistingSignalsOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        var plan = CurrentPlan;
        if (plan is null)
            return new VisualExecutionResult(false, "Es ist kein visueller Plan geladen.", Validate().Issues);
        if (_runtimeObjects.Count == 0)
        {
            await DiscoverFeeObjectsAsync(cancellationToken);
            AutoAssignMatches();
        }
        if (!_hasDiscoveredFeeInterfaces)
            await DiscoverFeeInterfacesAsync(cancellationToken);

        return await _signalLinkExecutor.ExecuteAsync(
            plan,
            _runtimeObjects,
            _runtimeInterfaces,
            cancellationToken);
    }

    public bool Undo()
    {
        var plan = CurrentPlan;
        if (plan is null || _undo.Count == 0)
            return false;

        _redo.Push(Capture(plan));
        Restore(plan, _undo.Pop());
        RaisePlanChanged();
        return true;
    }

    public bool Redo()
    {
        var plan = CurrentPlan;
        if (plan is null || _redo.Count == 0)
            return false;

        _undo.Push(Capture(plan));
        Restore(plan, _redo.Pop());
        RaisePlanChanged();
        return true;
    }

    private void SetPlan(VisualPlan plan)
    {
        CurrentPlan = plan;
        _undo.Clear();
        _redo.Clear();
        _confirmedDuplicateIdentities.Clear();
        ClearFeeDiscovery();
        RaisePlanChanged();
    }

    /// <summary>Discards live session data while retaining the plan and its undo history.</summary>
    public void ClearFeeDiscovery()
    {
        _topLevelBasicFrames = new Dictionary<Guid, string>();
        _feeObjects = [];
        _feeContainerObjects = [];
        _runtimeObjects = new Dictionary<string, FeeAbstractObject>(StringComparer.Ordinal);
        _feeInterfaces = [];
        _feeSignals = [];
        _feeSignalLinks = [];
        _feeSimObjectLinks = [];
        _runtimeInterfaces = new Dictionary<string, FeeInterface>(StringComparer.OrdinalIgnoreCase);
        _hasDiscoveredFeeObjects = false;
        _hasDiscoveredFeeInterfaces = false;
        _hasDiscoveredFeeSignalLinks = false;
        _hasDiscoveredFeeSimObjectLinks = false;
    }

    private IReadOnlyList<VisualIssue> ValidateAndApplyDocument(
        VisualPlan plan,
        VisualPlanSidecarDocument document)
    {
        var issues = new List<VisualIssue>();
        var assignments = document.Assignments ?? [];
        var requests = document.CreationRequests ?? [];
        var generationSelections = document.GenerationSelections ?? [];
        var signalCreationSelections = document.SignalCreationSelections ?? [];
        var signalAssignments = document.SignalAssignments ?? [];
        var addedSignals = document.AddedSignals ?? [];
        var slotOverrides = document.SlotOverrides ?? [];
        var containerTypeOverrides = document.ContainerTypeOverrides ?? [];
        var removedSignalNodeIds = document.RemovedSignalNodeIds ?? [];

        var validTypeOverrides = containerTypeOverrides.Where(typeOverride =>
        {
            var valid = plan.FindNode(typeOverride.ContainerId)?.Kind == VisualNodeKind.Container &&
                        ContainerMetadataCatalog.TryGet(typeOverride.TypeName, out _);
            if (!valid)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_CONTAINER_TYPE_INVALID",
                    $"Die gespeicherte Containertyp-Auswahl '{typeOverride.TypeName}' ist nicht mehr gültig und wurde ignoriert.",
                    typeOverride.ContainerId));
            }
            return valid;
        }).ToArray();
        plan.ReplaceContainerTypeOverrides(validTypeOverrides);

        var validAddedSignals = addedSignals.Where(added =>
        {
            var container = plan.FindNode(added.ContainerId);
            var valid = container?.Kind == VisualNodeKind.Container &&
                        ContainerMetadataCatalog.TryGet(container.TypeName, out _) &&
                        string.Equals(added.SignalGroupId, $"{added.ContainerId}:signals", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(added.NodeId) &&
                        !string.IsNullOrWhiteSpace(added.FeeSignalGuid);
            if (!valid)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_ADDED_SIGNAL_INVALID",
                    $"Ein zusätzliches Signal '{added.FeeSignalTag}' verweist auf keinen gültigen Container und wurde ignoriert.",
                    added.NodeId));
            }
            return valid;
        }).DistinctBy(item => item.NodeId, StringComparer.Ordinal).ToArray();
        plan.ReplaceAddedSignals(validAddedSignals);

        foreach (var assignment in assignments)
        {
            var target = plan.FindTarget(assignment.TargetId);
            if (target is null)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "SIDECAR_TARGET_MISSING",
                    $"Gespeichertes Ziel für '{assignment.FeeObjectName}' existiert nicht mehr.",
                    assignment.TargetId));
            }
        }
        foreach (var duplicate in assignments.GroupBy(item => item.FeeObjectId).Where(group => group.Count() > 1))
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Error,
                "SIDECAR_DUPLICATE_FEE_OBJECT",
                $"FEE-Objekt '{duplicate.First().FeeObjectName}' ist im gespeicherten Plan mehrfach zugeordnet."));
        }
        foreach (var targetGroup in assignments.GroupBy(item => item.TargetId))
        {
            var target = plan.FindTarget(targetGroup.Key);
            if (target is not null && !target.AllowMultiSelect && targetGroup.Count() > 1)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Error,
                    "SIDECAR_SINGLE_TARGET_MULTIPLE",
                    $"Ziel '{target.DisplayName}' enthält mehrere gespeicherte Objekte.",
                    target.Id));
            }
        }
        foreach (var request in requests.Where(item => !item.IsRequested))
        {
            var node = plan.FindNode(request.ContainerId);
            if (node is null || !node.SupportsCreation)
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_CREATION_UNSUPPORTED",
                    "Eine nicht mehr unterstützte Erzeugungsausnahme wurde ignoriert.",
                    request.ContainerId));
            }
        }
        foreach (var selection in generationSelections)
        {
            var node = plan.FindNode(selection.ContainerId);
            if (node?.Kind != VisualNodeKind.Container ||
                !ContainerMetadataCatalog.TryGet(node.TypeName, out _))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_GENERATION_TARGET_MISSING",
                    "Eine nicht mehr vorhandene Containerauswahl wurde ignoriert.",
                    selection.ContainerId));
            }
        }
        foreach (var selection in signalCreationSelections)
        {
            var node = plan.FindNode(selection.ContainerId);
            if (node?.Kind != VisualNodeKind.Container ||
                !ContainerMetadataCatalog.TryGet(node.TypeName, out _))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_SIGNAL_TARGET_MISSING",
                    "Eine nicht mehr vorhandene Signalerzeugungs-Auswahl wurde ignoriert.",
                    selection.ContainerId));
            }
        }

        if (issues.Any(issue => issue.Severity == VisualIssueSeverity.Error))
            return issues;

        if (document.SchemaVersion < 4 && requests.Count > 0)
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Info,
                "SIDECAR_CREATION_DEFAULT_MIGRATED",
                "Fehlende SimObjects werden jetzt standardmäßig erzeugt; die frühere Positivliste wurde migriert."));
            requests = [];
        }
        foreach (var slotOverride in slotOverrides)
        {
            var signalNode = plan.FindNode(slotOverride.SignalNodeId);
            var containerNode = signalNode?.ContainerId is null
                ? null
                : plan.FindNode(signalNode.ContainerId);
            if (signalNode?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal) ||
                containerNode is null ||
                !ContainerMetadataCatalog.TryGet(containerNode.TypeName, out var descriptor) ||
                !descriptor.Slots.Contains(slotOverride.Slot))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_SLOT_OVERRIDE_INVALID",
                    $"Die gespeicherte Slot-Auswahl '{slotOverride.Slot}' ist nicht mehr gültig und wurde ignoriert.",
                    slotOverride.SignalNodeId));
            }
        }
        foreach (var assignment in signalAssignments)
        {
            var node = plan.FindNode(assignment.SignalNodeId);
            if (node?.Kind is not (VisualNodeKind.Signal or VisualNodeKind.UnknownSignal))
            {
                issues.Add(new VisualIssue(
                    VisualIssueSeverity.Warning,
                    "SIDECAR_SIGNAL_ASSIGNMENT_TARGET_MISSING",
                    $"Die gespeicherte Zuordnung für FEE-Signal '{assignment.FeeSignalTag}' wurde ignoriert, weil das Ziel nicht mehr existiert.",
                    assignment.SignalNodeId));
            }
        }
        if (signalCreationSelections.Count > 0)
        {
            issues.Add(new VisualIssue(
                VisualIssueSeverity.Info,
                "SIDECAR_SIGNAL_SELECTION_IGNORED",
                "Die frühere Auswahl 'Signale erzeugen' ist entfallen. Signale werden automatisch gesucht, wiederverwendet oder in einem neuen AutoGenerated-Interface erzeugt."));
        }

        plan.ReplaceSlotOverrides(slotOverrides.Where(slotOverride =>
        {
            var signalNode = plan.FindNode(slotOverride.SignalNodeId);
            var containerNode = signalNode?.ContainerId is null ? null : plan.FindNode(signalNode.ContainerId);
            return signalNode?.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                   containerNode is not null &&
                   ContainerMetadataCatalog.TryGet(containerNode.TypeName, out var descriptor) &&
                   descriptor.Slots.Contains(slotOverride.Slot);
        }));
        plan.ReplaceRemovedSignalNodeIds(removedSignalNodeIds);
        // Rebuild type-derived logic and SimObject targets after added signals
        // and effective slots are restored. Otherwise an older sidecar order
        // can remove those generated nodes or validate against stale slots.
        plan.ReplaceContainerTypeOverrides(validTypeOverrides);
        plan.ReplaceAssignments(assignments);
        plan.ReplaceCreationRequests(requests.Where(request =>
            !request.IsRequested && plan.FindNode(request.ContainerId)?.SupportsCreation == true));
        plan.ReplaceGenerationSelections(generationSelections.Where(selection =>
            !selection.IsSelected &&
            plan.FindNode(selection.ContainerId)?.Kind == VisualNodeKind.Container));
        plan.ReplaceSignalCreationSelections([]);
        plan.ReplaceSignalAssignments(signalAssignments.Where(assignment =>
            plan.FindNode(assignment.SignalNodeId)?.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
            !removedSignalNodeIds.Contains(assignment.SignalNodeId, StringComparer.Ordinal)));
        plan.SetExistingInterfaceSelections(document.ExistingInterfaceSelections.Count > 0
            ? document.ExistingInterfaceSelections
            : document.ExistingInterfaceSelection is null
                ? []
                : [document.ExistingInterfaceSelection]);
        return issues;
    }

    private static VisualPlan CloneWithIssues(VisualPlan plan, IEnumerable<VisualIssue> additionalIssues)
    {
        plan.AddIssues(additionalIssues);
        return plan;
    }

    private VisualFeeObject? FindFeeObject(string? feeObjectId) =>
        string.IsNullOrWhiteSpace(feeObjectId)
            ? null
            : _feeObjects.FirstOrDefault(item => string.Equals(item.Id, feeObjectId, StringComparison.Ordinal));

    private string DescribeLinkedFeeObject(string guidString)
    {
        var containerObject = _feeContainerObjects.FirstOrDefault(item => string.Equals(
            item.GuidString,
            guidString,
            StringComparison.OrdinalIgnoreCase));
        if (containerObject is not null)
        {
            var kind = containerObject.Kind switch
            {
                VisualFeeContainerObjectKind.Logic => "Logik",
                VisualFeeContainerObjectKind.Cabinet => "Cabinet",
                VisualFeeContainerObjectKind.CabinetElement => "CabinetElement",
                _ => "FEE-Objekt",
            };
            return $"{kind} '{containerObject.Name}'";
        }

        var signal = _feeSignals.FirstOrDefault(item => string.Equals(
            item.GuidString,
            guidString,
            StringComparison.OrdinalIgnoreCase));
        if (signal is not null)
            return $"Signal '{signal.Tag}' aus Interface '{signal.InterfaceName}'";

        var simObject = _feeObjects.FirstOrDefault(item => string.Equals(
            item.GuidString,
            guidString,
            StringComparison.OrdinalIgnoreCase));
        return simObject is null
            ? "FEE-Objekt 'Name nicht auflösbar'"
            : $"SimObject '{simObject.Name}'";
    }

    private static string CreateDuplicateIdentity(VisualFeeObject item) => string.Join(
        "\u001f",
        item.Name.Trim(),
        item.TypeName,
        item.FeeType.Trim(),
        item.ParentGuidString.Trim());

    private VisualFeeSignal? ResolveSignalForNode(
        VisualPlan plan,
        VisualNode node,
        IReadOnlySet<string> interfaceGuids)
    {
        var explicitAssignment = plan.SignalAssignments.LastOrDefault(assignment =>
            string.Equals(assignment.SignalNodeId, node.Id, StringComparison.Ordinal));
        if (explicitAssignment is not null)
        {
            return _feeSignals.FirstOrDefault(signal =>
                string.Equals(signal.GuidString, explicitAssignment.FeeSignalGuid, StringComparison.OrdinalIgnoreCase) &&
                interfaceGuids.Contains(signal.InterfaceGuidString));
        }

        var scoped = _feeSignals.Where(signal => interfaceGuids.Contains(signal.InterfaceGuidString))
            .ToArray();
        var byTag = scoped.Where(signal => string.Equals(
                signal.Tag,
                node.Name,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (string.IsNullOrWhiteSpace(node.SourceLocation))
            return byTag.Length == 1 ? byTag[0] : null;
        var exact = byTag.Where(signal => string.Equals(
                signal.Location,
                node.SourceLocation,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length == 1 ? exact[0] : null;
    }

    private HashSet<string> ResolveExpectedSignalTargetGuids(
        VisualPlan plan,
        VisualNode container,
        ContainerDescriptor descriptor)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName))
        {
            result.UnionWith(_feeContainerObjects.Where(item =>
                    item.Kind == VisualFeeContainerObjectKind.Logic &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    ContainerMetadataCatalog.IsSameLogicDefinition(descriptor.ExpectedLogicName, item.Definition))
                .Select(item => item.GuidString));
        }
        if (!string.IsNullOrWhiteSpace(descriptor.ExpectedCabinetElementType))
        {
            result.UnionWith(_feeContainerObjects.Where(item =>
                    item.Kind == VisualFeeContainerObjectKind.CabinetElement &&
                    string.Equals(item.Name, container.Name, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeToken(item.Definition) == NormalizeToken(descriptor.ExpectedCabinetElementType))
                .Select(item => item.GuidString));
        }

        var targetIds = plan.Targets.Where(target => string.Equals(
                target.ContainerId,
                container.Id,
                StringComparison.Ordinal))
            .Select(target => target.Id)
            .ToHashSet(StringComparer.Ordinal);
        var assignedObjectIds = plan.Assignments.Where(assignment => targetIds.Contains(assignment.TargetId))
            .Select(assignment => assignment.FeeObjectId)
            .ToHashSet(StringComparer.Ordinal);
        result.UnionWith(_feeObjects.Where(item => assignedObjectIds.Contains(item.Id))
            .Select(item => item.GuidString));
        result.UnionWith(descriptor.TechnicalHelpers.SelectMany(helperName =>
            FindTechnicalHelpers(container.Id, helperName)).Select(item => item.GuidString));
        return result;
    }

    public IReadOnlyList<VisualFeeContainerObject> FindTechnicalHelpers(string containerId, string helperName)
    {
        var plan = CurrentPlan;
        var container = plan?.FindNode(containerId);
        if (plan is null || container is null)
            return [];
        var signalNodes = plan.Nodes.Where(node => node.ContainerId == containerId &&
            node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal && !plan.IsSignalRemoved(node.Id)).ToArray();
        var signalGuids = _feeSignals.Where(signal => signalNodes.Any(node =>
            plan.SignalAssignments.Any(assignment => assignment.SignalNodeId == node.Id &&
                string.Equals(assignment.FeeSignalGuid, signal.GuidString, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(node.Name, signal.Tag, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(node.SourceLocation) ||
                 string.Equals(node.SourceLocation, signal.Location, StringComparison.OrdinalIgnoreCase))))
            .Select(signal => signal.GuidString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _feeContainerObjects.Where(item => item.Kind == VisualFeeContainerObjectKind.TechnicalHelper &&
            VisualFeeTechnicalHelperResolver.IsExpectedType(helperName, item.Definition) &&
            (VisualFeeTechnicalHelperResolver.MatchesIdentity(item, container) ||
             string.IsNullOrWhiteSpace(item.ProvenanceContainerId) && string.IsNullOrWhiteSpace(item.Name) &&
             _feeSignalLinks.Any(link => signalGuids.Contains(link.SignalGuidString) &&
                 string.Equals(link.ObjectGuidString, item.GuidString, StringComparison.OrdinalIgnoreCase))))).ToArray();
    }

    private static string NormalizeToken(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private void RecordMutation(PlanState state)
    {
        _undo.Push(state);
        _redo.Clear();
    }

    private static PlanState Capture(VisualPlan plan) =>
        new(
            [.. plan.Assignments],
            [.. plan.CreationRequests],
            [.. plan.GenerationSelections],
            [.. plan.SignalCreationSelections],
            [.. plan.SignalAssignments],
            [.. plan.AddedSignals],
            [.. plan.SlotOverrides],
            [.. plan.ContainerTypeOverrides],
            [.. plan.RemovedSignalNodeIds],
            [.. plan.ExistingInterfaceSelections]);

    private static void Restore(VisualPlan plan, PlanState state)
    {
        plan.ReplaceAddedSignals(state.AddedSignals);
        plan.ReplaceSlotOverrides(state.SlotOverrides);
        plan.ReplaceRemovedSignalNodeIds(state.RemovedSignalNodeIds);
        plan.ReplaceContainerTypeOverrides(state.ContainerTypeOverrides);
        plan.ReplaceAssignments(state.Assignments);
        plan.ReplaceCreationRequests(state.CreationRequests);
        plan.ReplaceGenerationSelections(state.GenerationSelections);
        plan.ReplaceSignalCreationSelections(state.SignalCreationSelections);
        plan.ReplaceSignalAssignments(state.SignalAssignments);
        plan.SetExistingInterfaceSelections(state.ExistingInterfaceSelections);
    }

    private void RaisePlanChanged()
    {
        if (CurrentPlan is not null)
            PlanChanged?.Invoke(this, new VisualPlanChangedEventArgs(CurrentPlan));
    }

    private static VisualAssignment ToAssignment(string targetId, VisualFeeObject feeObject) =>
        new(targetId, feeObject.Id, feeObject.Name, feeObject.TypeName);

    private static VisualAssignmentResult AssignmentFailure(
        string message,
        string code,
        string? nodeId = null) =>
        new(false, message, null, [new VisualIssue(VisualIssueSeverity.Error, code, message, nodeId)]);

    private static VisualSignalAssignmentResult SignalAssignmentFailure(
        string message,
        string code,
        string? nodeId = null) =>
        new(false, message, null, [new VisualIssue(VisualIssueSeverity.Error, code, message, nodeId)]);

    private static VisualPlanLoadResult Failure(string code, string message)
    {
        var issue = new VisualIssue(VisualIssueSeverity.Error, code, message);
        return new VisualPlanLoadResult(false, null, [issue], message);
    }

    private sealed record PlanState(
        IReadOnlyList<VisualAssignment> Assignments,
        IReadOnlyList<VisualCreationRequest> CreationRequests,
        IReadOnlyList<VisualGenerationSelection> GenerationSelections,
        IReadOnlyList<VisualSignalCreationSelection> SignalCreationSelections,
        IReadOnlyList<VisualSignalAssignment> SignalAssignments,
        IReadOnlyList<VisualAddedSignal> AddedSignals,
        IReadOnlyList<VisualSlotOverride> SlotOverrides,
        IReadOnlyList<VisualContainerTypeOverride> ContainerTypeOverrides,
        IReadOnlyList<string> RemovedSignalNodeIds,
        IReadOnlyList<VisualExistingInterfaceSelection> ExistingInterfaceSelections);

    private static bool IsSelectedInterface(VisualPlan plan, string interfaceGuid) =>
        plan.ExistingInterfaceSelections.Any(item => string.Equals(
            item.InterfaceGuid,
            interfaceGuid,
            StringComparison.OrdinalIgnoreCase));
}

internal static class VisualExistingContainerComparer
{
    public static IReadOnlySet<string> FindVerifiedContainerIds(
        VisualPlan plan,
        XDocument source,
        IEnumerable<XDocument> feeDocuments)
    {
        var available = feeDocuments
            .SelectMany(document => document.Descendants("Container"))
            .Select(ToSignature)
            .Where(signature => signature is not null)
            .Cast<ContainerSignature>()
            .ToList();
        var used = new HashSet<int>();
        var sourceElements = source.Descendants("Container").ToArray();
        var planContainers = plan.Nodes.Where(node => node.Kind == VisualNodeKind.Container).ToArray();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(sourceElements.Length, planContainers.Length); index++)
        {
            var expected = ToSignature(sourceElements[index]);
            if (expected is null || expected.Entries.Count == 0)
                continue;
            var matchIndex = -1;
            for (var candidateIndex = 0; candidateIndex < available.Count; candidateIndex++)
            {
                if (!used.Contains(candidateIndex) && Same(available[candidateIndex], expected))
                {
                    matchIndex = candidateIndex;
                    break;
                }
            }
            if (matchIndex < 0)
                continue;
            used.Add(matchIndex);
            verified.Add(planContainers[index].Id);
        }
        return verified;
    }

    private static ContainerSignature? ToSignature(XElement container)
    {
        var component = container.Element("Component")?.Value?.Trim() ?? string.Empty;
        var type = container.Element("Type")?.Value?.Trim() ?? string.Empty;
        if (component.Length == 0 || type.Length == 0)
            return null;
        var entries = container.Descendants("Entry")
            .Where(entry => !(entry.Element("ID")?.Value ?? string.Empty)
                .StartsWith("FEE-UNASSIGNED-", StringComparison.Ordinal))
            .Select(entry => string.Join("\u001f", new[]
            {
                entry.Element("ID")?.Value?.Trim() ?? string.Empty,
                entry.Element("Address")?.Value?.Trim() ?? string.Empty,
                entry.Element("DataType")?.Value?.Trim() ?? string.Empty,
                entry.Element("Signal")?.Value?.Trim() ?? string.Empty,
                entry.Element("Slot")?.Value?.Trim() ?? string.Empty,
            }))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ContainerSignature(component, type, entries);
    }

    private static bool Same(ContainerSignature left, ContainerSignature right) =>
        string.Equals(left.Component, right.Component, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
        left.Entries.SequenceEqual(right.Entries, StringComparer.OrdinalIgnoreCase);

    private sealed record ContainerSignature(string Component, string Type, IReadOnlyList<string> Entries);
}

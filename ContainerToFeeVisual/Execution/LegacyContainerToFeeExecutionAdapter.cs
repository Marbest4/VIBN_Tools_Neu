using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Settings;
using System.Xml.Linq;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>
/// Applies the visual plan to freshly parsed legacy containers and delegates
/// all actual creation to the established Container2FEE executor. This keeps
/// the generated FEE behavior identical to the existing tab.
/// </summary>
internal sealed class LegacyContainerToFeeExecutionAdapter(IVisualPlanLogger logger)
{
    public async Task<VisualExecutionResult> ExecuteAsync(
        VisualPlan plan,
        IReadOnlyDictionary<string, FeeAbstractObject> runtimeObjects,
        IReadOnlyDictionary<string, FeeInterface> runtimeInterfaces,
        IReadOnlyList<VisualIssue> acceptedValidationErrors,
        IProgress<VisualGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Services.Connection?.CanUseFeeFeatures != true)
            return Failure(FeeConnectionService.MissingConnectionMessage, "FEE_NOT_CONNECTED");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new VisualGenerationProgress(5, "Generierungsplan und ModelValidation werden geprüft …"));
            var executionWarnings = new List<VisualIssue>();
            var forcedRun = acceptedValidationErrors.Any(issue => issue.Severity == VisualIssueSeverity.Error);
            var excludedContainerIds = new HashSet<string>(StringComparer.Ordinal);

            var binding = RuntimeVisualPlanBinder.Bind(plan, runtimeObjects, excludedContainerIds);
            if (!binding.Success)
                return new VisualExecutionResult(false, binding.Issue!.Message, [binding.Issue]);

            var selectedBindings = binding.Containers
                .Where(item => plan.IsGenerationSelected(item.PlanNode.Id) &&
                               !excludedContainerIds.Contains(item.PlanNode.Id))
                .ToArray();
            var existingLogics = await ExistingSignalLinkAdapter.ReadExistingLogicsAsync(cancellationToken);
            foreach (var item in selectedBindings.Where(item =>
                         item.RuntimeContainer is Interfaces.ILogicOwner or Interfaces.ILogicSimObjectOwner))
            {
                var expectedLogic = plan.Nodes.FirstOrDefault(node =>
                    node.ContainerId == item.PlanNode.Id && node.Kind == VisualNodeKind.Logic)?.Name;
                var matches = existingLogics.Where(logic =>
                        string.Equals(logic.Name, item.RuntimeContainer.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(expectedLogic) ||
                         string.Equals(logic.LogicDefinitionName, expectedLogic, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (matches.Length > 1)
                {
                    return Failure(
                        $"Logik '{item.RuntimeContainer.ComponentName}' ({expectedLogic}) ist mehrfach vorhanden. " +
                        "Die Generierung erzeugt kein weiteres Duplikat; bitte den Bestand eindeutig bereinigen.",
                        "EXISTING_LOGIC_AMBIGUOUS",
                        item.PlanNode.Id);
                }
                if (matches.Length == 1 &&
                    !ContainerExistingObjectReuse.TryAssignLogic(item.RuntimeContainer, matches[0]))
                {
                    return Failure(
                        $"Vorhandene Logik '{item.RuntimeContainer.ComponentName}' konnte nicht wiederverwendet werden.",
                        "EXISTING_LOGIC_BIND_FAILED",
                        item.PlanNode.Id);
                }
            }
            var existingCabinetElements = await ExistingSignalLinkAdapter.ReadExistingCabinetElementsAsync(cancellationToken);
            foreach (var item in selectedBindings.Where(item => item.RuntimeContainer is Interfaces.ICabinetElementOwner))
            {
                var expectedType = ContainerExistingObjectReuse.GetExpectedCabinetElementType(item.RuntimeContainer);
                var matches = existingCabinetElements.Where(element =>
                        string.Equals(element.Name, item.RuntimeContainer.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(element.ElementType, expectedType, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length > 1)
                {
                    return Failure(
                        $"CabinetElement '{item.RuntimeContainer.ComponentName}' ({expectedType}) ist mehrfach vorhanden. " +
                        "Die Generierung erzeugt kein weiteres Duplikat; bitte den Bestand eindeutig bereinigen.",
                        "EXISTING_CABINET_ELEMENT_AMBIGUOUS",
                        item.PlanNode.Id);
                }
                if (matches.Length == 1 &&
                    !ContainerExistingObjectReuse.TryAssignCabinetElement(item.RuntimeContainer, matches[0]))
                {
                    return Failure(
                        $"Vorhandenes CabinetElement '{item.RuntimeContainer.ComponentName}' konnte nicht wiederverwendet werden.",
                        "EXISTING_CABINET_ELEMENT_BIND_FAILED",
                        item.PlanNode.Id);
                }
            }
            var modelPreflightIssues = selectedBindings
                .SelectMany(item => ContainerModelValidationPreflight.Validate(item.RuntimeContainer)
                    .Select(issue => new VisualIssue(
                        issue.Severity == ContainerPreflightSeverity.Error
                            ? VisualIssueSeverity.Error
                            : VisualIssueSeverity.Warning,
                        issue.Code,
                        $"{item.PlanNode.Name}: {issue.Message}",
                        item.PlanNode.Id)))
                .ToArray();
            if (modelPreflightIssues.Any(issue => issue.Severity == VisualIssueSeverity.Error) && !forcedRun)
            {
                return new VisualExecutionResult(
                    false,
                    "Die Generierung wurde vor dem Schreiben abgebrochen, weil Voraussetzungen der ModelValidation fehlen.",
                    modelPreflightIssues);
            }
            // In an explicitly confirmed forced run the user requested a
            // best-effort creation of these containers. Model preflight errors
            // remain persisted below the root instead of silently excluding the
            // affected container. Deterministic runtime conflicts still stop.
            var usedSignalNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var signalRequests = selectedBindings
                .SelectMany(binding => binding.RuntimeContainer.EnumerateAssignedSignals().Select(signal =>
                    new SignalResolutionRequest(
                        binding.PlanNode.Id,
                        binding.PlanNode.Name,
                        signal,
                        FindSignalNodeId(plan, binding.PlanNode.Id, signal, usedSignalNodeIds))))
                .Concat(binding.UnknownSignals.Select(signal =>
                    new SignalResolutionRequest(
                        "unknown-signals",
                        "Unbekannte Signale",
                        signal)))
                .ToArray();
            var reusableInterfaces = plan.ExistingInterfaceSelection is null
                ? Array.Empty<FeeInterface>()
                : runtimeInterfaces.Values.Where(item => string.Equals(
                    item.Guid.ToString("D"),
                    plan.ExistingInterfaceSelection.InterfaceGuid,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            var reusableSignalGuids = reusableInterfaces
                .SelectMany(item => item.Signals ?? [])
                .Select(signal => signal.Guid.ToString("D"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedSignalAssignments = plan.SignalAssignments
                .Where(assignment => reusableSignalGuids.Contains(assignment.FeeSignalGuid))
                .ToArray();
            var signalPlan = SignalResolutionPlanner.Build(
                signalRequests,
                reusableInterfaces,
                selectedSignalAssignments);
            if (!signalPlan.IsValid)
            {
                return new VisualExecutionResult(
                    false,
                    "Vorhandene Signale konnten nicht eindeutig aufgelöst werden.",
                    signalPlan.Issues);
            }
            signalPlan.ApplyExistingBindings();
            progress?.Report(new VisualGenerationProgress(18, "Vorhandene Signale wurden eindeutig aufgelöst."));

            // Missing variables require the installed generation provider, not
            // an existing interface instance with a fixed display name. The
            // legacy generator creates a fresh timestamped instance as well.
            var timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            var existingGenerationInterfaces = runtimeInterfaces.Values
                .Where(item => item.ProviderGuid == Defines.GrobGenerationInterfaceProviderGuid)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Guid)
                .ToArray();
            var preferredGenerationInterface = plan.ExistingInterfaceSelection is null
                ? null
                : existingGenerationInterfaces.FirstOrDefault(item => string.Equals(
                    item.Guid.ToString("D"),
                    plan.ExistingInterfaceSelection.InterfaceGuid,
                    StringComparison.OrdinalIgnoreCase));
            var generationInterface = preferredGenerationInterface ??
                                      existingGenerationInterfaces.FirstOrDefault() ??
                                      new FeeInterface
                                      {
                                          Name = $"Auto Generated (at {timestamp})",
                                          ProviderGuid = Defines.GrobGenerationInterfaceProviderGuid,
                                      };
            if (signalPlan.MissingSignals.Count > 0)
            {
                // Existing interface instances are not evidence for an
                // installed provider. Let FEE resolve the stable provider GUID
                // while creating the interface; this also works in a project
                // in which no interface instance exists yet.
                if (existingGenerationInterfaces.Length == 0 &&
                    !await generationInterface.CreateInterfaceAsync())
                {
                    return Failure(
                        "Der Provider des Grob Generation Interface ist nicht verfügbar oder die neue " +
                        "FEE-Interfaceinstanz konnte nicht angelegt werden. Es wird keine vorhandene " +
                        "Interfaceinstanz vorausgesetzt; bitte nur die Plugininstallation prüfen.",
                        "GROB_GENERATION_INTERFACE_CREATE_FAILED");
                }
                progress?.Report(new VisualGenerationProgress(
                    28,
                    existingGenerationInterfaces.Length == 0
                        ? "Grob Generation Interface wurde neu bereitgestellt."
                        : $"Vorhandenes Grob Generation Interface '{generationInterface.Name}' wird wiederverwendet."));
            }

            var selectedContainers = selectedBindings
                .Select(item => item.RuntimeContainer)
                .ToArray();
            ContainerToFeeService.LinkAddonContainers(selectedContainers);
            var sortedContainers = selectedBindings
                .Select(item => item.RuntimeContainer)
                .OrderBy(container => container.GetType().Name, StringComparer.Ordinal)
                .ThenBy(container => container.ComponentName, StringComparer.Ordinal)
                .ToArray();

            // Create only the unique missing variables before any BasicFrame,
            // logic or SimObject is written. All later legacy calls reuse the
            // resolved GUIDs and therefore cannot duplicate the variables.
            foreach (var missing in signalPlan.MissingSignals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await missing.Signal.CreateSignalAsync(generationInterface))
                {
                    return Failure(
                        $"Signal '{missing.Signal.Tag}' konnte im Grob Generation Interface nicht erzeugt werden. " +
                        "Bereits zuvor angelegte Signale können bestehen geblieben sein; Containerobjekte wurden noch nicht erzeugt.",
                        "GENERATED_SIGNAL_NOT_AVAILABLE",
                        missing.ContainerId);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            signalPlan.ApplyCreatedBindings(generationInterface);
            progress?.Report(new VisualGenerationProgress(40, "Signale wurden wiederverwendet oder erzeugt."));

            if (selectedContainers.Length > 0 || forcedRun)
            {
                var includedContainerIds = plan.Nodes
                    .Where(node => node.Kind == VisualNodeKind.Container)
                    .Where(node =>
                        (plan.IsGenerationSelected(node.Id) && !excludedContainerIds.Contains(node.Id)) ||
                        !ContainerMetadataCatalog.TryGet(node.TypeName, out _))
                    .Select(node => node.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var sourceDocument = RuntimeVisualPlanBinder.CreateEffectiveDocument(plan);
                var signalSources = selectedBindings.ToDictionary(
                    item => item.PlanNode.Id,
                    item => (IReadOnlyList<FeeContainerSignalSource>)item.RuntimeContainer
                        .EnumerateAssignedSignals()
                        .Select(signal => new FeeContainerSignalSource(
                            signal.Comment ?? string.Empty,
                            signal.Tag ?? string.Empty,
                            string.IsNullOrWhiteSpace(signal.Path) ? signal.Address ?? string.Empty : signal.Path,
                            signal.IOTypeString ?? string.Empty,
                            signal.Guid))
                        .ToArray(),
                    StringComparer.Ordinal);
                var provenance = FeeContainerProvenanceCodec.Create(
                    sourceDocument,
                    includedContainerIds,
                    plan.SourceFingerprint,
                    signalSources);
                var expectedBindings = signalSources.Values.Sum(signals => signals.Count);
                if (provenance.SignalBindings.Count != expectedBindings)
                {
                    return Failure(
                        "Die Container-Einträge konnten nicht eindeutig den aufgelösten FEE-Signalen zugeordnet werden. " +
                        "Die Generierung wurde vor dem BasicFrame abgebrochen.",
                        "PROVENANCE_SIGNAL_BINDING_INCOMPLETE");
                }
                var persistentTags = new Dictionary<string, string>(provenance.Tags, StringComparer.Ordinal);
                if (forcedRun)
                {
                    VisualGenerationOverrideMarker.Add(
                        persistentTags,
                        acceptedValidationErrors.Concat(modelPreflightIssues));
                }
                var basicFrame = new FeeBasicFrame
                {
                    Name = forcedRun
                        ? $"Auto Generated (at {timestamp}) - Trotz Validierungsfehlern erstellt"
                        : $"Auto Generated (at {timestamp})",
                    PersistentTags = persistentTags,
                };
                await basicFrame.CreateAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await basicFrame.SendAndWaitAsync();
                if (!string.IsNullOrWhiteSpace(basicFrame.PersistentTagWarning))
                {
                    var warning = new VisualIssue(
                        VisualIssueSeverity.Warning,
                        "PROVENANCE_TAG_UNCONFIRMED",
                        basicFrame.PersistentTagWarning,
                        selectedBindings.FirstOrDefault()?.PlanNode.Id);
                    executionWarnings.Add(warning);
                    logger.Warning(
                        "FEE hat die Root-Provenienz nicht bestätigt; die bestätigte Generierung wird fortgesetzt.",
                        basicFrame.PersistentTagWarning);
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (forcedRun)
                {
                    var persistedErrors = acceptedValidationErrors
                        .Concat(modelPreflightIssues)
                        .Where(issue => issue.Severity == VisualIssueSeverity.Error)
                        .DistinctBy(issue => (issue.Code, issue.Message, issue.NodeId))
                        .ToArray();
                    for (var index = 0; index < persistedErrors.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var error = persistedErrors[index];
                        var errorText = $"[{error.Code}] {error.Message}";
                        var errorFrame = new FeeBasicFrame
                        {
                            Parent = basicFrame,
                            Name = $"Fehler {index + 1:000} - {Truncate(errorText, 160)}",
                            PersistentTags = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["vibn.validation.error.code"] = error.Code,
                                ["vibn.validation.error.message"] = error.Message,
                                ["vibn.validation.error.node"] = error.NodeId ?? string.Empty,
                            },
                        };
                        await errorFrame.CreateAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        await errorFrame.SendAndWaitAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(errorFrame.PersistentTagWarning))
                        {
                            logger.Warning(
                                $"Der FEE-Fehlerhinweis '{errorFrame.Name}' wurde erzeugt, seine Tag-Properties " +
                                "wurden aber nicht bestätigt.",
                                errorFrame.PersistentTagWarning);
                        }
                    }
                }
                progress?.Report(new VisualGenerationProgress(50, "Generierungs-BasicFrame und Fehlerhinweise wurden erstellt."));

                await ContainerToFeeService.CreateAllContainersAsync(
                    sortedContainers,
                    generationInterface,
                    basicFrame,
                    (completed, total, name) =>
                    {
                        var isStarting = name.StartsWith("Wird erstellt: ", StringComparison.Ordinal);
                        progress?.Report(new VisualGenerationProgress(
                            total == 0 ? 90 : 50 + (isStarting ? completed - 1 : completed) * 40 / total,
                            isStarting
                                ? $"Container {completed} von {total} wird erstellt: {name["Wird erstellt: ".Length..]}"
                                : $"Container {completed} von {total} erstellt: {name}"));
                    },
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (binding.UnknownSignals.Count > 0)
            {
                foreach (var signal in binding.UnknownSignals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await signal.CreateSignalAsync(generationInterface);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            logger.Information(
                $"Visuelle Generierung abgeschlossen: {selectedContainers.Length} Container " +
                $"({signalPlan.ExistingBindings.Count} vorhandene, " +
                $"{signalPlan.MissingSignals.Count} neu zu erzeugende Signale), " +
                $"{binding.UnknownSignals.Count} unbekannte Signale. " +
                "Der erzeugte BasicFrame enthält Container2FEE-Provenienz für FEE2Container.");
            progress?.Report(new VisualGenerationProgress(100, "FEE-Generierung vollständig abgeschlossen."));
            return new VisualExecutionResult(
                true,
                (forcedRun
                    ? "ACHTUNG: Bestätigte Generierung trotz Validierungsfehlern abgeschlossen. "
                    : "Generierung abgeschlossen: ") +
                $"{selectedContainers.Length} Container wurden verarbeitet; " +
                $"{signalPlan.ExistingBindings.Count} Signale wurden wiederverwendet und " +
                $"{signalPlan.MissingSignals.Count} im Grob Generation Interface erzeugt." +
                (executionWarnings.Count > 0
                    ? " Hinweis: FEE hat die Provenienz-Tags nicht bestätigt; Details stehen im Protokoll."
                    : string.Empty),
                acceptedValidationErrors
                    .Concat(modelPreflightIssues)
                    .Concat(executionWarnings)
                    .Distinct()
                    .ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Error("Die visuelle Container2FEE-Generierung ist fehlgeschlagen.", exception);
            return new VisualExecutionResult(
                false,
                "Generierung fehlgeschlagen. Details stehen im Protokoll.",
                [new VisualIssue(
                    VisualIssueSeverity.Error,
                    "LEGACY_EXECUTION_FAILED",
                    exception.Message)]);
        }
    }

    private static VisualExecutionResult Failure(string message, string code, string? nodeId = null) =>
        new(false, message, [new VisualIssue(VisualIssueSeverity.Error, code, message, nodeId)]);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static string? FindSignalNodeId(
        VisualPlan plan,
        string containerId,
        FeeInterfaceSignal signal,
        ISet<string> usedNodeIds)
    {
        var identity = !string.IsNullOrWhiteSpace(signal.Tag)
            ? signal.Tag
            : !string.IsNullOrWhiteSpace(signal.Path)
                ? signal.Path
                : signal.Address;
        var candidate = plan.Nodes
            .Where(node => node.ContainerId == containerId &&
                           node.Kind is VisualNodeKind.Signal or VisualNodeKind.UnknownSignal &&
                           !plan.IsSignalRemoved(node.Id) &&
                           !usedNodeIds.Contains(node.Id))
            .OrderByDescending(node => string.Equals(node.Name, identity, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(node => string.Equals(node.TypeName, signal.IOTypeString, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (candidate is not null)
            usedNodeIds.Add(candidate.Id);
        return candidate?.Id;
    }

    private static HashSet<string> ResolveAffectedContainers(
        VisualPlan plan,
        IEnumerable<VisualIssue> issues) => issues
        .Where(issue => issue.Severity == VisualIssueSeverity.Error)
        .Select(issue => ResolveAffectedContainer(plan, issue.NodeId))
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    private static string? ResolveAffectedContainer(VisualPlan plan, string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return null;
        var target = plan.FindTarget(nodeId);
        if (target is not null)
            return target.ContainerId;
        var node = plan.FindNode(nodeId);
        return node?.Kind == VisualNodeKind.Container ? node.Id : node?.ContainerId;
    }
}

internal static class VisualGenerationOverrideMarker
{
    public const string AcceptedKey = "vibn.validation.override";
    public const string ErrorCountKey = "vibn.validation.error-count";
    public const string ErrorPrefix = "vibn.validation.error.";

    public static void Add(IDictionary<string, string> tags, IEnumerable<VisualIssue> issues)
    {
        var errors = issues
            .Where(issue => issue.Severity == VisualIssueSeverity.Error)
            .Select(issue => $"[{issue.Code}] {issue.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        tags[AcceptedKey] = "true";
        tags[ErrorCountKey] = errors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < errors.Length; index++)
            tags[$"{ErrorPrefix}{index + 1:000}"] = errors[index];
    }
}

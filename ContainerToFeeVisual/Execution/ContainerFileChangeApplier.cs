using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Prepares the complete batch before mutating the stateful FEE SDK.</summary>
internal static class ContainerFileChangeApplier
{
    public static async Task<XDocument> ReadCurrentFileAsync(CancellationToken token)
    {
        RequireSession();
        var service = new Fee2ContainerService();
        var discovery = await service.DiscoverAsync(token);
        if (discovery.Issues.Any(issue => issue.Message.Contains("konnte nicht", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Der FEE-Stand konnte nicht vollständig eingelesen werden. Hinweise in FEE2Container prüfen.");
        var containers = new List<XElement>();
        var inventoryObjects = new List<XElement>(); var inventorySignals = new List<XElement>();
        foreach (var root in discovery.Roots)
        {
            var snapshot = (await service.CreateExportAsync(root, token)).Snapshot;
            foreach (var item in snapshot.ContainerDocument.Descendants("Container"))
            {
                var clone = new XElement(item); clone.SetAttributeValue("feeRootGuid", root.Guid.ToString("D"));
                containers.Add(clone);
            }
            inventoryObjects.AddRange(snapshot.ContainerDocument.Root?.Element("FeeInventory")?.Element("SimObjects")?.Elements("SimObject") ?? []);
            inventorySignals.AddRange(snapshot.ContainerDocument.Root?.Element("FeeInventory")?.Element("Signals")?.Elements("Signal") ?? []);
        }
        return ContainerFileXml.Document(containers, new XElement("FeeInventory",
            new XElement("SimObjects", inventoryObjects.DistinctBy(item => item.Element("Guid")?.Value).Select(item => new XElement(item))),
            new XElement("Signals", inventorySignals.DistinctBy(item => item.Element("Guid")?.Value).Select(item => new XElement(item)))));
    }

    public static Task<string> ApplyAsync(IReadOnlyList<ContainerFileChange> changes, XDocument reviewed,
        IProgress<VisualGenerationProgress>? progress, CancellationToken token) => FeeMutationScope.RunAsync(
            () => ApplyCoreAsync(changes, reviewed, progress, token), token);

    private static async Task<string> ApplyCoreAsync(IReadOnlyList<ContainerFileChange> changes, XDocument reviewed,
        IProgress<VisualGenerationProgress>? progress, CancellationToken token)
    {
        RequireSession();
        var revision = Services.Connection.ConnectionRevision;
        void CheckSession()
        {
            token.ThrowIfCancellationRequested(); RequireSession();
            if (revision != Services.Connection.ConnectionRevision)
                throw new InvalidOperationException("FEE-Verbindung hat sich geändert. Bestand erneut einlesen und vergleichen.");
        }
        progress?.Report(new VisualGenerationProgress(5, "Änderungen und aktuelle FEE-GUIDs werden geprüft …"));
        var current = await ReadCurrentFileAsync(token);
        CheckSession();
        var liveContainers = current.Descendants("Container").ToArray();
        var allObjects = Services.FeeObjects.AllFeeObjects?.ToDictionary(item => item.Guid) ?? [];
        var updatedContainers = changes.Where(item => item.NewContainer is not null).Select(item => new XElement(item.NewContainer!)).ToArray();
        var deleteGuids = new HashSet<Guid>();
        var oldLive = new List<XElement>();
        foreach (var change in changes)
        {
            if (change.OldContainer is null) continue;
            var name = change.OldContainer.Element("Component")?.Value;
            var type = change.OldContainer.Element("Type")?.Value;
            var matches = liveContainers.Where(item => string.Equals(item.Element("Component")?.Value, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Element("Type")?.Value, type, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Container '{name}' ({type}) ist im FEE-Bestand nicht eindeutig. Aktuellen FEE-Stand links einlesen.");
            oldLive.Add(matches[0]);
            var objects = ContainerFileXml.Objects(matches[0]).ToArray();
            var signalOnly = ContainerMetadataCatalog.TryGet(type ?? "", out var descriptor) &&
                descriptor.Targets.Count == 0 && string.IsNullOrWhiteSpace(descriptor.ExpectedLogicName) && descriptor.TechnicalHelpers.Count == 0;
            if (!signalOnly && !objects.Any(item => item.Element("Role")?.Value == "Primary"))
                throw new InvalidOperationException($"Container '{name}' besitzt kein eindeutig identifiziertes primäres FEE-Objekt.");
            foreach (var item in objects)
            {
                if (!Guid.TryParse(item.Element("Guid")?.Value, out var guid) || !allObjects.ContainsKey(guid))
                    throw new InvalidOperationException($"FEE-Objekt von '{name}' ist nicht mehr vorhanden.");
                var role = item.Element("Role")?.Value;
                var generated = role == "TechnicalHelper" || item.Element("FeeType")?.Value is "LogicObject" or "CabinetElement" or "BoolNot" or "MoveBit" or "BoolAnd" or "BoolOr";
                if (change.NewContainer is null || generated) deleteGuids.Add(guid);
            }
        }
        // Shared or explicitly retained physical objects are never removed as
        // a side effect of replacing a logic container.
        var retained = reviewed.Descendants("Container").SelectMany(ContainerFileXml.Objects).Where(item => item.Element("Role")?.Value != "TechnicalHelper" &&
            item.Element("FeeType")?.Value is not "LogicObject" and not "CabinetElement" and not "BoolNot" and not "MoveBit" and not "BoolAnd" and not "BoolOr")
            .Select(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
        var unaffected = liveContainers.Except(oldLive).SelectMany(ContainerFileXml.Objects)
            .Select(item => Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
        deleteGuids.ExceptWith(retained); deleteGuids.ExceptWith(unaffected);
        foreach (var guid in deleteGuids)
            if ((allObjects[guid].ChildrenGuids ?? []).Any(child => allObjects.ContainsKey(child) && !deleteGuids.Contains(child)))
                throw new InvalidOperationException($"Objekt {guid:D} enthält weitere FEE-Objekte außerhalb der ausgewählten Löschungen.");
        foreach (var added in changes.Where(item => item.OldContainer is null && item.NewContainer is not null))
            if (liveContainers.Any(item => string.Equals(item.Element("Component")?.Value, added.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Element("Type")?.Value, added.Type, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Der neue Container '{added.Name}' ({added.Type}) existiert bereits im aktuellen FEE-Stand.");
        foreach (var container in updatedContainers)
        {
            var name = container.Element("Component")?.Value ?? "";
            if (!ContainerMetadataCatalog.TryGet(container.Element("Type")?.Value ?? "", out _))
                throw new InvalidOperationException($"Container-Typ von '{name}' wird vom FEE-Generator nicht unterstützt.");
            var retainedObjects = ContainerFileXml.Objects(container).Where(item =>
                item.Element("Role")?.Value != "TechnicalHelper" && item.Element("FeeType")?.Value is not "LogicObject" and not "CabinetElement" and not "BoolNot" and not "MoveBit" and not "BoolAnd" and not "BoolOr").ToArray();
            foreach (var item in retainedObjects)
                if (!Guid.TryParse(item.Element("Guid")?.Value, out var guid) || !allObjects.TryGetValue(guid, out var actual) ||
                    !string.Equals(actual.FeeType, item.Element("FeeType")?.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SimObject '{item.Element("Name")?.Value}' von '{name}' ist nicht vorhanden oder besitzt einen anderen FEE-Typ. Zuordnung im Vergleich bearbeiten.");
        }

        var backupDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GROB", "VIBN_Tools", "ContainerFileChanges", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        current.Save(Path.Combine(backupDirectory, "FEE-vorher.xml"));
        reviewed.Save(Path.Combine(backupDirectory, "Container-geprueft.xml"));
        var selectedPath = Path.Combine(backupDirectory, "Container-auswahl.xml");
        ContainerFileXml.Document(updatedContainers, reviewed.Root?.Element("FeeInventory")).Save(selectedPath);
        var operations = new List<string>();
        void Journal(string message)
        {
            operations.Add(message);
            File.WriteAllText(Path.Combine(backupDirectory, "Ausfuehrung.json"), JsonSerializer.Serialize(operations));
        }
        Journal("Vorprüfung begonnen; noch keine FEE-Änderungen.");
        var planService = new ContainerToFeeVisualPlanService();
        RuntimeVisualPlanBindingResult? binding = null;
        IReadOnlyList<FeeInterface> interfaces = [];
        var signalUpdates = new List<(FeeInterfaceSignal Signal, FeeInterface Interface)>();
        if (updatedContainers.Length > 0)
        {
            var loaded = await planService.LoadXmlAsync(selectedPath, token);
            if (loaded.Plan is null || !loaded.Success) throw new InvalidOperationException(loaded.Message);
            await planService.DiscoverFeeObjectsAsync(token);
            await planService.DiscoverFeeInterfacesAsync(token);
            var plan = planService.CurrentPlan!;
            plan.SetExistingInterfaceSelections(planService.ComparisonInterfaces.Select(item =>
                new VisualExistingInterfaceSelection(item.Guid.ToString("D"), item.Name ?? "")));
            var unresolved = plan.Issues.Where(item => item.Code == "XML_OBJECT_TARGET_UNRESOLVED" &&
                updatedContainers.Any(container => ContainerFileXml.Objects(container).Any(obj =>
                    !string.IsNullOrWhiteSpace(obj.Element("Target")?.Value)))).ToArray();
            if (unresolved.Length > 0) throw new InvalidOperationException(string.Join("; ", unresolved.Select(item => item.Message)));
            var validation = planService.Validate();
            var errors = validation.Issues.Where(item => item.Severity == VisualIssueSeverity.Error).ToArray();
            if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(item => item.Message)));
            binding = RuntimeVisualPlanBinder.Bind(plan, planService.ComparisonRuntimeObjects);
            if (!binding.Success) throw new InvalidOperationException(binding.Issue!.Message);
            var preflight = binding.Containers.SelectMany(item => ContainerModelValidationPreflight.Validate(item.RuntimeContainer))
                .Where(item => item.Severity == ContainerPreflightSeverity.Error).ToArray();
            if (preflight.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Select(item => item.Message)));
            interfaces = planService.ComparisonInterfaces;
            signalUpdates = PrepareSignalUpdates(updatedContainers, reviewed, interfaces);
            var predicted = CloneInterfaces(interfaces, signalUpdates);
            var usedSignalNodes = new HashSet<string>(StringComparer.Ordinal);
            var requests = binding.Containers.SelectMany(item => item.RuntimeContainer.EnumerateAssignedSignals().Select(signal =>
                LegacyContainerToFeeExecutionAdapter.CreateSignalResolutionRequest(plan, item.PlanNode, signal, usedSignalNodes)));
            var signalPlan = SignalResolutionPlanner.Build(requests, predicted, plan.SignalAssignments);
            if (!signalPlan.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, signalPlan.Issues.Select(item => item.Message)));
        }
        var unlinks = new List<FeeVariableUnlinkOperation>();
        var liveVariableGuids = Services.FeeObjects.AllFeeObjects.OfType<FeeInterface>()
            .SelectMany(item => item.Signals ?? []).Select(item => item.Guid).ToHashSet();
        foreach (var change in changes.Where(item => item.OldContainer is not null && item.NewContainer is not null))
        {
            var old = oldLive.Single(item => item.Element("Component")?.Value == change.OldContainer!.Element("Component")?.Value &&
                item.Element("Type")?.Value == change.OldContainer.Element("Type")?.Value);
            var type = old.Element("Type")?.Value ?? "";
            var wanted = change.NewContainer!.Descendants("Entry").ToArray();
            var nextGuids = ContainerFileXml.Objects(change.NewContainer).Select(item =>
                Guid.TryParse(item.Element("Guid")?.Value, out var guid) ? guid : Guid.Empty).ToHashSet();
            foreach (var obj in ContainerFileXml.Objects(old))
            {
                if (!Guid.TryParse(obj.Element("Guid")?.Value, out var guid) || deleteGuids.Contains(guid) || !allObjects.TryGetValue(guid, out var actual)) continue;
                var retainedObject = nextGuids.Contains(guid) || nextGuids.Count == 0;
                foreach (var slot in actual.Slots ?? [])
                {
                    if (!liveVariableGuids.Contains(slot.Value)) continue;
                    var xmlSlot = FeeContainerLiveReconstructor.MapRuntimeSlot(type, slot.Key);
                    if (xmlSlot is null) continue;
                    var desired = retainedObject && wanted.Any(entry => entry.Element("Slot")?.Value == xmlSlot);
                    if (!desired) unlinks.Add(FeeVariableUnlinkOperation.Prepare(guid, slot.Key, slot.Value));
                }
            }
        }
        CheckSession(); Journal("Gesamte Vorprüfung erfolgreich. Ausgewählte Lösch-GUIDs: " + string.Join(",", deleteGuids));
        try
        {
            foreach (var unlink in unlinks.DistinctBy(item => (item.ObjectGuid, item.Slot, item.VariableGuid)))
            {
                CheckSession(); Journal($"Variablenzuordnung entfernen: {unlink.ObjectGuid:D}/{unlink.Slot}");
                await unlink.ExecuteAsync(); Journal("Entfernen der Variablenzuordnung bestätigt.");
            }
            foreach (var update in signalUpdates)
            {
                CheckSession(); Journal($"Signaländerung gestartet: {update.Signal.Guid:D}");
                if (!await update.Signal.CreateSignalAsync(update.Interface)) throw new InvalidOperationException("FEE hat eine Signaländerung abgelehnt.");
                Journal($"Signaländerung bestätigt: {update.Signal.Guid:D}");
            }
            foreach (var guid in deleteGuids)
            {
                CheckSession(); Journal($"Objektlöschung gestartet: {guid:D}");
                await Task.Run(() => Services.ApiInstance.Object.DeleteObject(guid));
                var remaining = await Services.ApiInstance.Object.GetSceneObjectGuidsAsync();
                if (remaining.Any(value => Guid.TryParse(value, out var actual) && actual == guid))
                    throw new InvalidOperationException($"Löschung von {guid:D} wurde von FEE nicht bestätigt.");
                Journal($"Objektlöschung bestätigt: {guid:D}");
            }
            if (binding is not null)
            {
                CheckSession(); Journal("Generierung und Verknüpfung des geprüften Containerstands gestartet.");
                var result = await planService.ExecuteAsync(progress: progress, cancellationToken: token);
                if (!result.Success) throw new InvalidOperationException(result.Message + " " + string.Join("; ", result.Issues.Select(item => item.Message)));
                Journal(result.Message);
            }
            CheckSession();
            foreach (var group in oldLive.GroupBy(item => item.Attribute("feeRootGuid")?.Value))
            {
                CheckSession();
                if (!Guid.TryParse(group.Key, out var rootGuid) || rootGuid == Guid.Empty) continue;
                var tags = await FeeTagPropertyStore.ReadAsync(rootGuid);
                if (!FeeContainerProvenanceCodec.TryRead(tags, out var rootSnapshot, out _)) continue;
                var removedKeys = group.Select(item => (item.Element("Component")?.Value, item.Element("Type")?.Value)).ToHashSet();
                var rootDocument = new XDocument(rootSnapshot!.ContainerDocument);
                foreach (var signal in rootSnapshot.SignalBindings)
                    rootDocument.Descendants("Container").ElementAtOrDefault(signal.ContainerIndex)?.Descendants("Entry")
                        .ElementAtOrDefault(signal.EntryIndex)?.SetAttributeValue("feeGuid", signal.VariableGuid.ToString("D"));
                foreach (var item in rootDocument.Descendants("Container").Where(item =>
                             removedKeys.Contains((item.Element("Component")?.Value, item.Element("Type")?.Value))).ToArray()) item.Remove();
                var includedIds = new HashSet<string>(StringComparer.Ordinal);
                var sources = new Dictionary<string, IReadOnlyList<FeeContainerSignalSource>>();
                var occurrences = new Dictionary<string, int>();
                foreach (var item in rootDocument.Descendants("Container"))
                {
                    var sourceId = item.Attribute("id")?.Value ?? "";
                    var name = item.Element("Component")?.Value ?? ""; var type = item.Element("Type")?.Value ?? "";
                    var identity = sourceId + "\u001f" + name + "\u001f" + type;
                    occurrences.TryGetValue(identity, out var occurrence); occurrences[identity] = ++occurrence;
                    var id = ContainerXmlVisualPlanParser.CreateContainerId(sourceId, name, type, occurrence); includedIds.Add(id);
                    sources[id] = item.Descendants("Entry").Where(entry => Guid.TryParse(entry.Attribute("feeGuid")?.Value, out _))
                        .Select(entry => new FeeContainerSignalSource(entry.Element("ID")?.Value ?? "", entry.Element("Signal")?.Value ?? "",
                            entry.Element("Address")?.Value ?? "", entry.Element("DataType")?.Value ?? "",
                            Guid.Parse(entry.Attribute("feeGuid")!.Value))).ToArray();
                }
                var projected = FeeContainerProvenanceCodec.Create(rootDocument, includedIds, rootSnapshot.SourceFingerprint, sources);
                Journal($"Root-Provenienzänderung gestartet: {rootGuid:D}");
                await FeeTagPropertyStore.WriteAndVerifyAsync(rootGuid, projected.Tags);
                Journal($"Root-Provenienzänderung bestätigt: {rootGuid:D}");
            }
            CheckSession();
            var after = await ReadCurrentFileAsync(token); after.Save(Path.Combine(backupDirectory, "FEE-nachher.xml"));
            Journal("Änderungsanwendung abgeschlossen; FEE-Stand erneut eingelesen.");
            return $"{changes.Count} Containeränderungen angewendet. Vorher-/Nachher-Stand und Ausführung: {backupDirectory}. " +
                "Vorhandene Interfacevariablen bleiben erhalten; entfallene Verknüpfungen werden mit ihren Containerobjekten entfernt.";
        }
        catch (Exception ex)
        {
            Journal("Anwendung angehalten: " + ex.Message);
            throw new InvalidOperationException($"{ex.Message} Bereits bestätigte Schritte und Wiederherstellungsstand: {backupDirectory}. FEE erneut einlesen, bevor weitere Änderungen angewendet werden.", ex);
        }
    }

    private static List<(FeeInterfaceSignal Signal, FeeInterface Interface)> PrepareSignalUpdates(
        IEnumerable<XElement> changed, XDocument reviewed, IReadOnlyList<FeeInterface> interfaces)
    {
        var available = interfaces.SelectMany(parent => (parent.Signals ?? []).Select(signal => (Signal: signal, Interface: parent)))
            .GroupBy(item => item.Signal.Guid).ToDictionary(group => group.Key, group => group.First());
        var result = new List<(FeeInterfaceSignal, FeeInterface)>();
        foreach (var group in changed.SelectMany(item => item.Descendants("Entry"))
                     .Where(item => Guid.TryParse(item.Attribute("feeGuid")?.Value, out _))
                     .GroupBy(item => Guid.Parse(item.Attribute("feeGuid")!.Value)))
        {
            if (!available.TryGetValue(group.Key, out var current))
                throw new InvalidOperationException($"Signal-GUID {group.Key:D} ist im Projekt nicht vorhanden. GUID entfernen, um ein neues Signal zu erzeugen.");
            var entry = group.First();
            string Key(XElement item) => string.Join("\u001f", new[] { "Signal", "Address", "DataType", "ID" }.Select(name => item.Element(name)?.Value ?? ""));
            if (reviewed.Descendants("Entry").Where(item => Guid.TryParse(item.Attribute("feeGuid")?.Value, out var guid) && guid == group.Key)
                .Any(item => Key(item) != Key(entry))) throw new InvalidOperationException($"Die gemeinsame Signal-GUID {group.Key:D} besitzt widersprüchliche Änderungen.");
            var signal = new FeeInterfaceSignal
            {
                Guid = group.Key, Tag = entry.Element("Signal")?.Value ?? "",
                Address = entry.Element("Address")?.Value ?? "", Path = "",
                IOTypeString = entry.Element("DataType")?.Value ?? "", Comment = entry.Element("ID")?.Value ?? ""
            };
            if (signal.Address.Contains("GVL_IO", StringComparison.OrdinalIgnoreCase)) { signal.Path = signal.Address; signal.Address = ""; }
            signal.SetIoMode();
            if (signal.Tag != current.Signal.Tag || signal.Address != current.Signal.Address || signal.Path != current.Signal.Path ||
                signal.IOTypeString != current.Signal.IOTypeString || signal.Comment != current.Signal.Comment)
                result.Add((signal, current.Interface));
        }
        return result;
    }

    private static IReadOnlyList<FeeInterface> CloneInterfaces(IReadOnlyList<FeeInterface> interfaces,
        IReadOnlyList<(FeeInterfaceSignal Signal, FeeInterface Interface)> updates) => interfaces.Select(parent => new FeeInterface
        {
            Guid = parent.Guid, ProviderGuid = parent.ProviderGuid, Name = parent.Name,
            Signals = (parent.Signals ?? []).Select(signal => updates.FirstOrDefault(update => update.Signal.Guid == signal.Guid).Signal ?? signal).ToList()
        }).ToArray();

    private static void RequireSession()
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException("FEE-Verbindung fehlt.");
        if (!Services.Connection.AreModelValidationObjectsCurrent)
            throw new InvalidOperationException(Services.Connection.ModelValidationUnavailableReason);
    }
}

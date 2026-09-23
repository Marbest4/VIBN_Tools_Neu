using FS.SDK.Components;
using FS.SDK.Scene.Objects;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Settings;
using static VIBN_Tools.GlobalClasses.Services;
using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.SpecialDevices;

public sealed record Fee2SpecialDeviceRoot(
    Guid Guid,
    string Name,
    FeeSpecialDeviceSnapshot Snapshot,
    int UpdatedSignalCount,
    int MissingSignalCount,
    bool HasProvenance = true,
    Fee2SpecialDeviceSignalCoverage? SignalCoverage = null)
{
    public string SourceKind => HasProvenance
        ? "SpecialDevices2FEE-Provenienz"
        : "FEE-Struktur (Rekonstruktion)";

    public int InputSignalCount => SignalCoverage?.InputSignalCount ?? 0;
    public int ConnectedInputSignalCount => SignalCoverage?.ConnectedInputSignalCount ?? 0;
    public int OutputSignalCount => SignalCoverage?.OutputSignalCount ?? 0;
    public int ConnectedOutputSignalCount => SignalCoverage?.ConnectedOutputSignalCount ?? 0;
    public string MissingPlcSlots => SignalCoverage is null || SignalCoverage.MissingSlots.Count == 0
        ? string.Empty
        : string.Join(", ", SignalCoverage.MissingSlots);
}

public sealed record Fee2SpecialDeviceSignalCoverage(
    int InputSignalCount,
    int ConnectedInputSignalCount,
    int OutputSignalCount,
    int ConnectedOutputSignalCount,
    IReadOnlyList<string> MissingSlots);

public sealed record Fee2SpecialDevicesProgress(int Current, int Total, string Message);

public sealed record Fee2SpecialDeviceDiscoveryIssue(Guid? Guid, string RootName, string Message);

public sealed record Fee2SpecialDeviceDiscoveryResult(
    IReadOnlyList<Fee2SpecialDeviceRoot> Roots,
    int IgnoredWithoutProvenance,
    IReadOnlyList<Fee2SpecialDeviceDiscoveryIssue> Issues);

/// <summary>
/// Reads every BasicFrame in the scene hierarchy. Tagged roots use the exact
/// reverse snapshot; older roots are reconstructed only when a known device
/// logic definition is identified unambiguously. Scanning nested frames is
/// required because SpecialDevices2FEE roots may be grouped below a project
/// frame in older models.
/// </summary>
public sealed class Fee2SpecialDevicesService
{
    public async Task<Fee2SpecialDeviceDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default,
        IProgress<Fee2SpecialDevicesProgress>? progress = null)
    {
        if (Services.Connection?.CanUseFeeFeatures != true || Services.ApiInstance is null)
            throw new InvalidOperationException(FeeConnectionService.MissingConnectionMessage);

        var variables = (await Services.ApiInstance.Interface.GetAllVariablesAsync())
            .Select(variable => new FeeInterfaceSignal
            {
                Guid = variable.VariableGuid,
                Tag = variable.Tag,
                Address = variable.Address,
                Path = variable.Path,
                IOType = variable.Type,
                Comment = variable.Comment,
                Usage = variable.Usage,
                References = variable.References
            })
            .GroupBy(variable => variable.Guid)
            .ToDictionary(group => group.Key, group => group.First());
        var roots = new List<Fee2SpecialDeviceRoot>();
        var issues = new List<Fee2SpecialDeviceDiscoveryIssue>();
        var ignored = 0;
        var logicDefinitions = await Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync();
        var logicNames = logicDefinitions
            .Where(item => Guid.TryParse(item.Guid, out _))
            .GroupBy(item => Guid.Parse(item.Guid))
            .ToDictionary(group => group.Key, group => group.First().Name ?? string.Empty);
        var knownDevices = BuildKnownDeviceDefinitions();
        progress?.Report(new Fee2SpecialDevicesProgress(0, 1, "Signalverknüpfungen werden einmalig eingelesen ..."));
        var assignmentRead = await ReadVariableAssignmentsAsync(variables.Values, cancellationToken);
        var assignmentsByLogic = assignmentRead.AssignmentsByLogic;
        issues.AddRange(assignmentRead.Warnings.Select(message =>
            new Fee2SpecialDeviceDiscoveryIssue(null, string.Empty, message)));
        var guidValues = await Services.ApiInstance.Object
            .GetSceneObjectGuidsOfTypeAsync(nameof(BasicFrame));
        var basicFrames = guidValues
            .Select(value => Guid.TryParse(value, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .Distinct()
            .ToArray();

        for (var frameIndex = 0; frameIndex < basicFrames.Length; frameIndex++)
        {
            var guid = basicFrames[frameIndex];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new Fee2SpecialDevicesProgress(
                frameIndex + 1,
                basicFrames.Length,
                $"BasicFrame {frameIndex + 1}/{basicFrames.Length} wird geprüft ..."));
            var name = guid.ToString("D");
            try
            {
                var nameXml = await Services.ApiInstance.Object.GetPropertyAsync(
                    guid,
                    nameof(FS.SDK.SceneObject.Name));
                name = Services.ApiInstance.XmlHelper.ConvertToString(nameXml);
                var tags = await ReadOptionalTagsAsync(guid);
                if (!tags.ContainsKey(FeeSpecialDeviceProvenanceCodec.SchemaKey))
                {
                    var reconstructed = await TryReconstructAsync(
                        guid,
                        name,
                        variables.Values.ToArray(),
                        logicNames,
                        knownDevices,
                        assignmentsByLogic,
                        cancellationToken);
                    if (reconstructed.Root is null)
                    {
                        ignored++;
                        if (!string.IsNullOrWhiteSpace(reconstructed.Issue))
                            issues.Add(new Fee2SpecialDeviceDiscoveryIssue(guid, name, reconstructed.Issue));
                    }
                    else
                    {
                        roots.Add(reconstructed.Root);
                        if (!string.IsNullOrWhiteSpace(reconstructed.Issue))
                            issues.Add(new Fee2SpecialDeviceDiscoveryIssue(guid, name, reconstructed.Issue));
                    }
                    continue;
                }
                if (!FeeSpecialDeviceProvenanceCodec.TryRead(tags, out var source, out var error))
                {
                    issues.Add(new Fee2SpecialDeviceDiscoveryIssue(guid, name, error));
                    continue;
                }

                var updated = 0;
                var missing = 0;
                var currentSignals = source!.Signals.Select(signal =>
                {
                    if (!variables.TryGetValue(signal.VariableGuid, out var variable))
                    {
                        missing++;
                        return signal;
                    }
                    updated++;
                    return signal with
                    {
                        Tag = variable.Tag ?? signal.Tag,
                        Address = variable.Address ?? signal.Address,
                        DataType = variable.IOType.ToString(),
                        Comment = variable.Comment ?? signal.Comment
                    };
                }).ToArray();
                var snapshot = source with { Signals = currentSignals };
                var coverage = await ReadCoverageForTaggedRootAsync(
                    guid,
                    snapshot,
                    logicNames,
                    knownDevices,
                    assignmentsByLogic,
                    cancellationToken);
                roots.Add(new Fee2SpecialDeviceRoot(guid, name, snapshot, updated, missing, true, coverage));
                if (missing > 0)
                {
                    issues.Add(new Fee2SpecialDeviceDiscoveryIssue(
                        guid,
                        name,
                        $"{missing} referenzierte FEE-Variable(n) fehlen; der gespeicherte Generierungsstand bleibt erhalten."));
                }
            }
            catch (Exception exception)
            {
                issues.Add(new Fee2SpecialDeviceDiscoveryIssue(
                    guid,
                    name,
                    $"Der FEE-Root konnte nicht gelesen werden: {exception.Message}"));
            }
        }

        // A top-level ancestor can contain exactly the same device logic as its
        // nested device frame. Prefer provenance, then the frame whose own name
        // contains the reconstructed prefix, so one physical device is shown once.
        var uniqueRoots = roots
            .GroupBy(root => (
                Prefix: root.Snapshot.Prefix.Trim().ToUpperInvariant(),
                Manufacturer: root.Snapshot.Manufacturer.Trim().ToUpperInvariant(),
                DeviceType: root.Snapshot.DeviceType.Trim().ToUpperInvariant()))
            .Select(group => group
                .OrderByDescending(root => root.HasProvenance)
                .ThenByDescending(root => root.Name.Contains(
                    root.Snapshot.Prefix,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(root => root.Name.Length)
                .First())
            .OrderBy(root => root.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Fee2SpecialDeviceDiscoveryResult(
            uniqueRoots,
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
            // Legacy/manual BasicFrames may not own a TagComponent at all.
            // Missing provenance must lead to structural reconstruction, not
            // to dropping the node from discovery.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static async Task<(Fee2SpecialDeviceRoot? Root, string? Issue)> TryReconstructAsync(
        Guid rootGuid,
        string rootName,
        IReadOnlyList<FeeInterfaceSignal> variables,
        IReadOnlyDictionary<Guid, string> logicNames,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        IReadOnlyDictionary<Guid, LogicVariableAssignments> assignmentsByLogic,
        CancellationToken cancellationToken)
    {
        var childGuids = (await ApiInstance!.Object
                .GetAllChildrenFromSceneObjectAsync(rootGuid.ToString("D")))
            .Where(value => Guid.TryParse(value, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (childGuids.Length == 0)
            return (null, null);

        var xmlTexts = (await ApiInstance.Object.GetSceneObjectsAsXmlAsync(childGuids)).ToArray();
        var matches = new List<(Guid LogicGuid, string ObjectName, KnownDeviceDefinition Device)>();
        for (var index = 0; index < Math.Min(childGuids.Length, xmlTexts.Length); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = XElement.Parse(xmlTexts[index]);
            var persistedLogic = xml.Element("Logic")?.Element("PersistedLogicGuid")?.Value;
            if (!Guid.TryParse(persistedLogic, out var definitionGuid) ||
                !logicNames.TryGetValue(definitionGuid, out var definitionName))
                continue;
            var deviceMatches = knownDevices
                .Where(device => string.Equals(device.LogicDefinitionName, definitionName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (deviceMatches.Length == 1)
            {
                matches.Add((
                    Guid.Parse(childGuids[index]),
                    xml.Attribute("Name")?.Value ?? string.Empty,
                    deviceMatches[0]));
            }
        }

        if (matches.Count == 0)
            return (null, null);
        if (matches.Count > 1)
            return (null, $"Der Root enthält {matches.Count} bekannte Special-Device-Logiken; eine eindeutige Gerätezuordnung ist nicht möglich.");

        var match = matches[0];
        var assignedVariables = assignmentsByLogic.TryGetValue(match.LogicGuid, out var logicAssignments)
            ? logicAssignments.Variables
            : [];
        var prefix = ExtractPrefix(match.ObjectName, rootName);
        var robotType = InferRobotType(assignedVariables);
        if (match.Device.RequiresRobotType && robotType is null)
        {
            return (null,
                "Die Geräteart wurde erkannt, der Robotertyp (ABB/Fanuc/Kuka) ist aus den aktuellen Signaladressen jedoch nicht eindeutig ableitbar.");
        }

        var signals = assignedVariables.Select(variable => new FeeSpecialDeviceSignalSnapshot(
            variable.Guid,
            variable.Tag ?? string.Empty,
            variable.Address ?? string.Empty,
            variable.Usage.ToString(),
            variable.IOType.ToString(),
            variable.Comment ?? string.Empty)).ToArray();
        var input = InferBaseAddress(assignedVariables, "Write");
        var output = InferBaseAddress(assignedVariables, "Read");
        var snapshot = new FeeSpecialDeviceSnapshot(
            FeeSpecialDeviceProvenanceCodec.CurrentSchema,
            prefix,
            match.Device.Manufacturer.ToString(),
            match.Device.DeviceType.ToString(),
            robotType?.ToString(),
            input,
            output,
            signals);
        var issue = signals.Length == 0
            ? "Die Geräteart wurde erkannt, aber es wurden keine Variablenzuweisungen an der Gerätelogik gefunden."
            : "Das Gerät wurde ohne Provenienz aus Logikdefinition und Variablenzuweisungen rekonstruiert; bitte vor Wiederverwendung fachlich vergleichen.";
        var coverage = await ReadCoverageAsync(match.LogicGuid, assignmentsByLogic, cancellationToken);
        return (new Fee2SpecialDeviceRoot(rootGuid, rootName, snapshot, signals.Length, 0, false, coverage), issue);
    }

    private static IReadOnlyList<KnownDeviceDefinition> BuildKnownDeviceDefinitions()
    {
        var definitions = new List<KnownDeviceDefinition>();
        foreach (var item in DeviceFactory.DeviceFactoryMap)
        {
            var requiresRobot = DeviceMetadata.MetadataMap.TryGetValue(item.Key, out var metadata) &&
                                metadata.RequiresRobotType;
            var probe = item.Value("__probe__", new SpecialDeviceAddresses(0, 0), RobotType.Kuka);
            definitions.Add(new KnownDeviceDefinition(
                item.Key.Item1,
                item.Key.Item2,
                probe.DeviceLogicObject.LogicDefinitionName,
                requiresRobot));
        }
        return definitions;
    }

    private static async Task<VariableAssignmentReadResult> ReadVariableAssignmentsAsync(
        IEnumerable<FeeInterfaceSignal> variables,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(6);
        var tasks = variables.Where(variable => variable.References > 0).Select(async variable =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var assignments = await ApiInstance!.Interface.GetAssignedSceneObjectsAsync(variable.Guid);
                return (Variable: variable, Assignments: assignments.ToArray(), Warning: (string?)null);
            }
            catch (Exception exception)
            {
                return (Variable: variable, Assignments: [], Warning:
                    $"Signalzuordnungen für '{variable.Tag}' konnten nicht gelesen werden: {exception.Message}");
            }
            finally
            {
                throttle.Release();
            }
        });
        var result = new Dictionary<Guid, LogicVariableAssignments>();
        var reads = await Task.WhenAll(tasks);
        foreach (var item in reads)
        {
            foreach (var assignment in item.Assignments)
            {
                if (!result.TryGetValue(assignment.Item1, out var existing))
                {
                    existing = new LogicVariableAssignments([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    result[assignment.Item1] = existing;
                }
                existing.Variables.Add(item.Variable);
                foreach (var slot in assignment.Item2.Where(slot => !string.IsNullOrWhiteSpace(slot)))
                    existing.ConnectedSlots.Add(slot);
            }
        }
        return new VariableAssignmentReadResult(
            result,
            reads.Select(item => item.Warning).OfType<string>().ToArray());
    }

    private static async Task<Fee2SpecialDeviceSignalCoverage?> ReadCoverageForTaggedRootAsync(
        Guid rootGuid,
        FeeSpecialDeviceSnapshot snapshot,
        IReadOnlyDictionary<Guid, string> logicNames,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        IReadOnlyDictionary<Guid, LogicVariableAssignments> assignmentsByLogic,
        CancellationToken cancellationToken)
    {
        var definition = knownDevices.FirstOrDefault(device =>
            string.Equals(device.Manufacturer.ToString(), snapshot.Manufacturer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(device.DeviceType.ToString(), snapshot.DeviceType, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return null;

        var matches = await FindLogicMatchesAsync(rootGuid, logicNames, [definition], cancellationToken);
        return matches.Count == 1
            ? await ReadCoverageAsync(matches[0].LogicGuid, assignmentsByLogic, cancellationToken)
            : null;
    }

    private static async Task<Fee2SpecialDeviceSignalCoverage> ReadCoverageAsync(
        Guid logicGuid,
        IReadOnlyDictionary<Guid, LogicVariableAssignments> assignmentsByLogic,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slots = (await ApiInstance!.Object.GetSlotNamesAsync(logicGuid))
            .Where(slot => slot.StartsWith("PLC_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var inputs = slots.Where(slot => slot.StartsWith("PLC_IN", StringComparison.OrdinalIgnoreCase)).ToArray();
        var outputs = slots.Where(slot => slot.StartsWith("PLC_OUT", StringComparison.OrdinalIgnoreCase)).ToArray();
        var connected = assignmentsByLogic.TryGetValue(logicGuid, out var assignments)
            ? assignments.ConnectedSlots
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = slots.Where(slot => !connected.Contains(slot))
            .OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Fee2SpecialDeviceSignalCoverage(
            inputs.Length,
            inputs.Count(connected.Contains),
            outputs.Length,
            outputs.Count(connected.Contains),
            missing);
    }

    private static async Task<IReadOnlyList<(Guid LogicGuid, string ObjectName, KnownDeviceDefinition Device)>> FindLogicMatchesAsync(
        Guid rootGuid,
        IReadOnlyDictionary<Guid, string> logicNames,
        IReadOnlyList<KnownDeviceDefinition> knownDevices,
        CancellationToken cancellationToken)
    {
        var childGuids = (await ApiInstance!.Object.GetAllChildrenFromSceneObjectAsync(rootGuid.ToString("D")))
            .Where(value => Guid.TryParse(value, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (childGuids.Length == 0)
            return [];

        var xmlTexts = (await ApiInstance.Object.GetSceneObjectsAsXmlAsync(childGuids)).ToArray();
        var matches = new List<(Guid LogicGuid, string ObjectName, KnownDeviceDefinition Device)>();
        for (var index = 0; index < Math.Min(childGuids.Length, xmlTexts.Length); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = XElement.Parse(xmlTexts[index]);
            var persistedLogic = xml.Element("Logic")?.Element("PersistedLogicGuid")?.Value;
            if (!Guid.TryParse(persistedLogic, out var definitionGuid) ||
                !logicNames.TryGetValue(definitionGuid, out var definitionName))
                continue;
            var deviceMatches = knownDevices
                .Where(device => string.Equals(device.LogicDefinitionName, definitionName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (deviceMatches.Length == 1)
                matches.Add((Guid.Parse(childGuids[index]), xml.Attribute("Name")?.Value ?? string.Empty, deviceMatches[0]));
        }
        return matches;
    }

    private static string ExtractPrefix(string objectName, string rootName)
    {
        var source = string.IsNullOrWhiteSpace(objectName) ? rootName : objectName;
        var marker = source.LastIndexOf(" (", StringComparison.Ordinal);
        var prefix = marker > 0 ? source[..marker] : source;
        return string.IsNullOrWhiteSpace(prefix) ? "FEE_SpecialDevice" : prefix.Trim();
    }

    private static RobotType? InferRobotType(IEnumerable<FeeInterfaceSignal> variables)
    {
        var addresses = variables.Select(variable => (string?)variable.Address ?? string.Empty).ToArray();
        if (addresses.Any(address => address.StartsWith("$IN[", StringComparison.OrdinalIgnoreCase) ||
                                     address.StartsWith("$OUT[", StringComparison.OrdinalIgnoreCase)))
            return RobotType.Kuka;
        if (addresses.Any(address => address.StartsWith("DIN[", StringComparison.OrdinalIgnoreCase) ||
                                     address.StartsWith("DOUT[", StringComparison.OrdinalIgnoreCase)))
            return RobotType.Fanuc;
        if (addresses.Length > 0 && addresses.All(address =>
                double.TryParse(address, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _)))
            return RobotType.ABB;
        return null;
    }

    private static int InferBaseAddress(IEnumerable<FeeInterfaceSignal> variables, string usage)
    {
        var values = variables
            .Where(variable => string.Equals(variable.Usage.ToString(), usage, StringComparison.OrdinalIgnoreCase))
            .Select(variable => ParseAddress((string?)variable.Address))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? 0 : (int)Math.Floor(values.Min());
    }

    private static double? ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        var match = Regex.Match(address, @"(?:\$?(?:IN|OUT)|D(?:IN|OUT)|[AE](?:[BWD])?)?\[?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private sealed record KnownDeviceDefinition(
        DeviceManufacturer Manufacturer,
        Enum DeviceType,
        string LogicDefinitionName,
        bool RequiresRobotType);

    private sealed record LogicVariableAssignments(
        List<FeeInterfaceSignal> Variables,
        HashSet<string> ConnectedSlots);

    private sealed record VariableAssignmentReadResult(
        IReadOnlyDictionary<Guid, LogicVariableAssignments> AssignmentsByLogic,
        IReadOnlyList<string> Warnings);
}

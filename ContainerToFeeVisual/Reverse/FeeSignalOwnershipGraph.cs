using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Only an operator's input can transfer ownership to an upstream scene object.</summary>
public sealed class FeeSignalOwnershipGraph
{
    private readonly IReadOnlyDictionary<Guid, FeeSignalEndpoint> _objects;
    private readonly ILookup<Guid, Peer> _peers;
    private readonly Dictionary<Guid, IReadOnlyList<Owner>> _owners = new();
    private ILookup<Guid, Guid>? _members;
    private static readonly HashSet<string> CounterInputs = new(StringComparer.OrdinalIgnoreCase)
        { "Reset", "Enable", "Trigger", "Clock", "CountUp", "CountDown" };
    public sealed record Owner(Guid Guid, string Slot = "");
    private sealed record Peer(Guid Guid, string LocalSlot, string RemoteSlot);

    public FeeSignalOwnershipGraph(IReadOnlyDictionary<Guid, FeeSignalEndpoint> objects, IEnumerable<VisualFeeObjectLink> links)
    {
        _objects = objects;
        _peers = links.Where(link => Guid.TryParse(link.ObjectGuidString, out _) && Guid.TryParse(link.LinkedObjectGuidString, out _))
            .SelectMany(link => new[]
            {
                (Guid: Guid.Parse(link.ObjectGuidString), Peer: new Peer(Guid.Parse(link.LinkedObjectGuidString), link.SlotName, link.LinkedSlotName)),
                (Guid: Guid.Parse(link.LinkedObjectGuidString), Peer: new Peer(Guid.Parse(link.ObjectGuidString), link.LinkedSlotName, link.SlotName))
            }).Distinct().ToLookup(item => item.Guid, item => item.Peer);
    }

    public IReadOnlyList<Owner> Owners(Guid guid)
    {
        if (_owners.TryGetValue(guid, out var cached)) return cached;
        if (!ContainerFileXml.IsSimpleLogicType(_objects.GetValueOrDefault(guid)?.FeeType))
            return _owners[guid] = [new(guid)];
        var pending = new Queue<Guid>(); pending.Enqueue(guid);
        var visited = new HashSet<Guid>();
        var terminals = new HashSet<Guid>();
        var owners = new HashSet<Owner>();
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            var upstream = _peers[current].Where(item => IsInput(item.LocalSlot) && _objects.ContainsKey(item.Guid)).ToArray();
            if (upstream.Length == 0) terminals.Add(current);
            foreach (var peer in upstream)
            {
                if (!_objects.TryGetValue(peer.Guid, out var endpoint)) continue;
                if (FeeContainerReadPolicy.IsIgnored(endpoint.FeeType, endpoint.Name)) continue;
                if (ContainerFileXml.IsSimpleLogicType(endpoint.FeeType))
                {
                    if (IsOutput(peer.RemoteSlot)) pending.Enqueue(peer.Guid);
                }
                else owners.Add(new(peer.Guid, peer.RemoteSlot));
            }
        }
        // A helper cycle without a primary object is one standalone container.
        if (owners.Count == 0)
        {
            var canonical = (terminals.Count > 0 ? terminals : visited).OrderBy(item => item).FirstOrDefault();
            owners.Add(new(canonical == Guid.Empty ? guid : canonical));
        }
        return _owners[guid] = owners.OrderBy(item => item.Guid).ThenBy(item => item.Slot, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<Guid> Members(Guid owner) => (_members ??= _objects.Keys.SelectMany(guid => Owners(guid)
        .Select(item => (Owner: item.Guid, Member: guid))).Distinct().ToLookup(item => item.Owner, item => item.Member))[owner].ToArray();

    public IReadOnlyList<FeeContainerObjectDetails> CreateDetails(IEnumerable<FeeContainerLiveAssignment> assignments)
    {
        var signals = assignments.ToLookup(item => item.TargetObjectGuid);
        return _objects.Values.Select(item => new FeeContainerObjectDetails(item.Guid, item.Name, item.FeeType,
            item.ParentName, item.LogicDefinitionName.Length > 0 ? item.LogicDefinitionName :
                ContainerFileXml.IsSimpleLogicType(item.FeeType) ? item.FeeType : "",
            string.Join("; ", signals[item.Guid].Select(route => $"{route.TargetSlot} ↔ Signal {route.VariableGuid:D}" + (route.IsIndirect ? " (über Hilfslogik)" : ""))
                .Concat(_peers[item.Guid].Select(peer => $"{peer.LocalSlot} ↔ {_objects.GetValueOrDefault(peer.Guid)?.Name ?? peer.Guid.ToString("D")}/{peer.RemoteSlot}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)),
            VIBN_Tools.SpecialDevices.FeeSpecialDeviceLogicCatalog.Contains(item.LogicDefinitionName), item.ParentGuid,
            item.SlotNames, item.CabinetDefinition)).ToArray();
    }

    public static IEnumerable<string> HelperSlotNames(string type, IEnumerable<string>? known = null)
    {
        type = type.Split('.').Last();
        var inputs = type.StartsWith("BoolAnd", StringComparison.OrdinalIgnoreCase) || type.StartsWith("BoolOr", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var inputMatch = System.Text.RegularExpressions.Regex.Match(type, @"(\d+)Inputs", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (inputMatch.Success && int.TryParse(inputMatch.Groups[1].Value, out var count)) inputs = Math.Clamp(count, 1, 128);
        var outputs = 1;
        var outputMatch = System.Text.RegularExpressions.Regex.Match(type, @"Copy(\d+)Outputs", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (outputMatch.Success && int.TryParse(outputMatch.Groups[1].Value, out count)) outputs = Math.Clamp(count, 1, 128);
        return (known ?? []).Concat(Enumerable.Range(1, inputs).Select(index => $"Input {index:00}"))
            .Concat(Enumerable.Range(1, outputs).Select(index => $"Output {index:00}")).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    public static bool IsInput(string slot)
    {
        slot = (slot ?? "").Trim();
        return slot.StartsWith("Input", StringComparison.OrdinalIgnoreCase) || slot.StartsWith("In ", StringComparison.OrdinalIgnoreCase) || CounterInputs.Contains(slot);
    }
    public static bool IsOutput(string slot) => (slot ?? "").Trim().StartsWith("Output", StringComparison.OrdinalIgnoreCase) ||
        (slot ?? "").Trim().StartsWith("Out ", StringComparison.OrdinalIgnoreCase);
}

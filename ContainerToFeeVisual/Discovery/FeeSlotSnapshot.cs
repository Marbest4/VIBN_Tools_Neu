using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Resolves live XML slot groups to scene instances, never to provenance or definition GUIDs.</summary>
internal sealed class FeeSlotSnapshot
{
    private readonly IReadOnlyDictionary<Guid, FeeAbstractObject> _objects;
    private readonly ILookup<Guid, (Guid ObjectGuid, string Slot)> _groups;
    private readonly HashSet<Guid> _variableGuids;

    internal FeeSlotSnapshot(IEnumerable<FeeAbstractObject> objects, IEnumerable<Guid>? variableGuids = null)
    {
        _variableGuids = (variableGuids ?? []).ToHashSet();
        _objects = objects.Where(item => item.Guid != Guid.Empty).DistinctBy(item => item.Guid).ToDictionary(item => item.Guid);
        _groups = _objects.Values.SelectMany(item => (item.Slots ?? []).Where(slot => slot.Value != Guid.Empty)
            .Select(slot => (Group: slot.Value, Endpoint: (item.Guid, slot.Key.Trim()))))
            .ToLookup(item => item.Group, item => item.Endpoint);
    }

    internal IReadOnlyList<VisualFeeObjectLink> ObjectLinks(IEnumerable<FeeAbstractObject> objects) => objects
        .SelectMany(item => (item.Slots ?? []).Where(slot => slot.Value != Guid.Empty && !_variableGuids.Contains(slot.Value)).SelectMany(slot =>
        {
            if (_objects.ContainsKey(slot.Value))
                return slot.Value != item.Guid
                    ? new[] { new VisualFeeObjectLink(item.GuidString, slot.Key.Trim(), slot.Value.ToString("D"), "") }
                    : Array.Empty<VisualFeeObjectLink>();
            var peers = _groups[slot.Value].Where(peer => peer.ObjectGuid != item.Guid).ToArray();
            if (peers.Length > 0)
                return peers.Select(peer => new VisualFeeObjectLink(item.GuidString, slot.Key.Trim(),
                    peer.ObjectGuid.ToString("D"), peer.Slot)).ToArray();
            return Array.Empty<VisualFeeObjectLink>();
        })).Distinct().ToArray();

    internal IReadOnlyList<VisualFeeSignalLink> SignalLinks(IEnumerable<VisualFeeSignal> signals)
    {
        var guids = signals.Where(signal => Guid.TryParse(signal.GuidString, out _))
            .Select(signal => Guid.Parse(signal.GuidString)).ToHashSet();
        return _objects.Values.SelectMany(item => (item.Slots ?? []).Where(slot => guids.Contains(slot.Value))
            .Select(slot => new VisualFeeSignalLink(slot.Value.ToString("D"), item.GuidString, item.FeeType ?? "", slot.Key.Trim(), false)))
            .Distinct().ToArray();
    }

    internal static bool IsRoutingHelper(FeeAbstractObject item) => item is FeeSimpleMove or FeeSimpleNot or FeeSimpleAnd or FeeSimpleOr;

    internal static IEnumerable<string> RoutingSlots(FeeAbstractObject item, string connectedSlot)
    {
        if (!IsRoutingHelper(item)) return [];
        if (connectedSlot.Trim().StartsWith("Input", StringComparison.OrdinalIgnoreCase)) return ["Output 01"];
        if (!connectedSlot.Trim().StartsWith("Output", StringComparison.OrdinalIgnoreCase)) return [];
        var defaults = item is FeeSimpleAnd or FeeSimpleOr ? new[] { "Input 01", "Input 02" } : ["Input 01"];
        return (item.Slots?.Keys ?? Enumerable.Empty<string>()).Where(slot => slot.StartsWith("Input", StringComparison.OrdinalIgnoreCase))
            .Concat(defaults).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<VisualFeeSignalLink> ExpandSignalRoutes(IEnumerable<VisualFeeSignalLink> signals,
        IReadOnlyList<FeeAbstractObject> objects, IEnumerable<VisualFeeObjectLink> objectLinks)
    {
        var byGuid = objects.Where(item => item.Guid != Guid.Empty).DistinctBy(item => item.Guid).ToDictionary(item => item.GuidString, StringComparer.OrdinalIgnoreCase);
        static string Key(string guid, string slot) => guid.ToUpperInvariant() + ":" + slot.Trim().ToUpperInvariant();
        var peers = objectLinks.Where(link => !string.IsNullOrWhiteSpace(link.LinkedSlotName))
            .SelectMany(link => new[] {
                (Key: Key(link.ObjectGuidString, link.SlotName), Guid: link.LinkedObjectGuidString, Slot: link.LinkedSlotName),
                (Key: Key(link.LinkedObjectGuidString, link.LinkedSlotName), Guid: link.ObjectGuidString, Slot: link.SlotName) })
            .ToLookup(peer => peer.Key, StringComparer.Ordinal);
        var result = signals.Where(link => !link.IsIndirect).ToHashSet();
        foreach (var signal in result.ToArray())
        {
            var pending = new Queue<(string Guid, string Slot)>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue((signal.ObjectGuidString, signal.SlotName));
            while (pending.TryDequeue(out var current))
            {
                if (!visited.Add(Key(current.Guid, current.Slot)) || !byGuid.TryGetValue(current.Guid, out var helper)) continue;
                foreach (var slot in RoutingSlots(helper, current.Slot))
                    foreach (var peer in peers[Key(current.Guid, slot)])
                    {
                        var type = byGuid.GetValueOrDefault(peer.Guid)?.FeeType ?? "";
                        result.Add(new VisualFeeSignalLink(signal.SignalGuidString, peer.Guid, type, peer.Slot, true));
                        pending.Enqueue((peer.Guid, peer.Slot));
                    }
            }
        }
        return result.ToArray();
    }
}

using System.Xml.Linq;

namespace VIBN_Tools.Core.Containers;

/// <summary>An immutable optional Requirements.xml snapshot. Live FEE facts remain the source of object ownership.</summary>
public sealed class ContainerRequirementsCatalog
{
    private readonly Dictionary<string, Definition> _definitions;
    private readonly Func<string, IReadOnlyCollection<string>?, IReadOnlyList<string>>? _signalTypeResolver;

    public ContainerRequirementsCatalog(XDocument document, Func<string, IReadOnlyCollection<string>?, IReadOnlyList<string>>? signalTypeResolver = null)
    {
        _signalTypeResolver = signalTypeResolver;
        ArgumentNullException.ThrowIfNull(document);
        if (document.Root?.Name != "AutoCreate") throw new ArgumentException("Req-XML mit AutoCreate als Wurzel erwartet.", nameof(document));
        _definitions = document.Root.Element("Components")?.Elements("Component")
            .Where(component => !string.IsNullOrWhiteSpace(component.Attribute("type")?.Value))
            .GroupBy(component => component.Attribute("type")!.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new Definition(group.Key,
                group.SelectMany(component => component.Descendants("Slot"))
                    .Select(slot => slot.Attribute("name")?.Value.Trim()).Where(slot => !string.IsNullOrWhiteSpace(slot))
                    .Select(slot => slot!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                group.Select(component => (int?)component.Attribute("minSignals")).Where(value => value.HasValue).Min(),
                group.Select(component => (int?)component.Attribute("maxSignals")).Where(value => value.HasValue).Max()),
                StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        if (_definitions.Count == 0) throw new ArgumentException("Req-XML enthält keine Containertypen.", nameof(document));
        Types = _definitions.Keys.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> Types { get; }
    public IReadOnlyList<string> TypesMatchingSignals(IEnumerable<string> signals, IReadOnlyCollection<string>? candidates = null) => _signalTypeResolver is null ? [] :
        signals.Where(signal => !string.IsNullOrWhiteSpace(signal)).SelectMany(signal => _signalTypeResolver(signal, candidates))
            .Where(Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool Contains(string type) => _definitions.ContainsKey(type);
    public IReadOnlyList<string> Slots(string type) => _definitions.GetValueOrDefault(type)?.Slots ?? [];
    public (int? Min, int? Max) SignalLimits(string type) => _definitions.TryGetValue(type, out var definition)
        ? (definition.Min, definition.Max) : (null, null);

    public string? ConfiguredName(string runtimeType)
    {
        if (_definitions.TryGetValue(runtimeType, out var exact)) return exact.Type;
        var alias = runtimeType switch { "CabinetSwitch" => "Switch", "Switch" => "CabinetSwitch", "CabinetFuse" => "Fuse", "Fuse" => "CabinetFuse", _ => "" };
        return _definitions.GetValueOrDefault(alias)?.Type;
    }

    /// <summary>Custom types need positive slot evidence; an empty scene never matches every definition.</summary>
    public IReadOnlyList<string> TypesMatchingSlots(IEnumerable<string> liveSlots)
    {
        var slots = liveSlots.Where(slot => !string.IsNullOrWhiteSpace(slot)).Select(slot => slot.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return slots.Length == 0 ? [] : Types.Where(type => slots.All(slot => Slots(type).Contains(slot, StringComparer.OrdinalIgnoreCase))).ToArray();
    }

    private sealed record Definition(string Type, IReadOnlyList<string> Slots, int? Min, int? Max);
}

using System.Text.RegularExpressions;
using System.Xml.Linq;
using VIBN_Tools.SpecialDevices;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Groups by interface and robot number; cross-interface merges need a unique shared live MoveByte endpoint.</summary>
public static class FeeRobotExchangeGrouping
{
    public const string RobotPlcType = "SignalaustauschRoboterPLC";
    public const string PlcRobotType = "SignalaustauschPLCRoboter";
    public static bool IsExchange(string type) => type is RobotPlcType or PlcRobotType;
    private static readonly Regex Robot = new(@"^(?:SendToRobot|ReceiveFromRobot)(?<number>\d+)_Byte", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Plc = new(@"^(?:SendToPLC|ReceiveFromPLC)_Byte", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<XElement> Create(IReadOnlyList<FeeContainerLiveVariable> variables, IReadOnlyList<FeeContainerLiveAssignment> routes,
        IReadOnlyDictionary<Guid, FeeSignalEndpoint> endpoints)
    {
        var routesByVariable = routes.ToLookup(item => item.VariableGuid);
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var byVariable = new Dictionary<Guid, string>();
        foreach (var variable in variables)
        {
            var match = Robot.Match(variable.Signal);
            if (!match.Success) continue;
            var number = match.Groups["number"].Value.TrimStart('0'); if (number.Length == 0) number = "0";
            var key = variable.InterfaceGuid + ":robot:" + number;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new(RobotPlcType, $"{variable.InterfaceName}_Robot{number}");
            group.Variables.Add(variable); byVariable[variable.VariableGuid] = key;
        }
        var bridgeOwners = routes.Where(route => byVariable.ContainsKey(route.VariableGuid) &&
            string.Equals(endpoints.GetValueOrDefault(route.TargetObjectGuid)?.FeeType, "MoveByte", StringComparison.OrdinalIgnoreCase))
            .GroupBy(route => route.TargetObjectGuid).ToDictionary(group => group.Key, group => group.Select(route => byVariable[route.VariableGuid]).Distinct().ToArray());
        foreach (var variable in variables.Where(item => Plc.IsMatch(item.Signal)))
        {
            var connected = routesByVariable[variable.VariableGuid].SelectMany(route => bridgeOwners.GetValueOrDefault(route.TargetObjectGuid) ?? [])
                .Distinct().Where(key => groups[key].Variables.Any(peer => !peer.InterfaceGuid.Equals(variable.InterfaceGuid, StringComparison.OrdinalIgnoreCase))).ToArray();
            var key = connected.Length == 1 ? connected[0] : variable.InterfaceGuid + ":plc";
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new(PlcRobotType, $"{variable.InterfaceName}_PLC_Roboter");
            group.Variables.Add(variable);
            if (connected.Length > 1) group.AmbiguousBridge = true;
        }
        var result = new List<XElement>();
        var duplicateNames = groups.Values.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, group) in groups)
        {
            var signals = group.Variables.DistinctBy(item => item.VariableGuid).ToArray();
            var snapshots = signals.Select(variable => new FeeSpecialDeviceSignalSnapshot(variable.VariableGuid, variable.Signal,
                variable.Address, variable.Usage, variable.DataType, variable.Comment, variable.Path, [])).ToArray();
            var minimum = signals.Select(variable => new { Variable = variable, Byte = FeeSpecialDeviceLiveProjector.MinimumByte(
                snapshots.Where(item => item.VariableGuid == variable.VariableGuid), true) ?? FeeSpecialDeviceLiveProjector.MinimumByte(snapshots.Where(item => item.VariableGuid == variable.VariableGuid), false) })
                .Where(item => item.Byte is not null).OrderBy(item => item.Byte).FirstOrDefault()?.Variable;
            result.Add(new XElement("Container", new XAttribute("id", "exchange:" + key), new XAttribute("recognitionSource", "Logik"),
                new XAttribute("origin", group.AmbiguousBridge ? "Signalmuster; MoveByte-Brücke mehrdeutig, Interfaces getrennt belassen" : "Signalmuster / eindeutige aktuelle MoveByte-Verbindung"),
                new XAttribute("specialDevice", "true"), new XAttribute("address", minimum is null ? "" : string.IsNullOrWhiteSpace(minimum.Address) ? minimum.Path : minimum.Address),
                new XElement("Component", duplicateNames.Contains(group.Name) ? group.Name + "_" + key.Replace(':', '_') : group.Name),
                new XElement("Type", group.Type), new XElement("DataList", signals.Select(variable =>
                    FeeSignalContainerBuilder.Entry(variable, routesByVariable[variable.VariableGuid].First().TargetSlot,
                        group.AmbiguousBridge ? "Mehrdeutige MoveByte-Brücke: Zuordnung zwischen Interfaces prüfen." : "Aktueller Signalaustausch; nur eindeutig über MoveByte verbundene Interfaces zusammengefasst.")))));
        }
        return result;
    }

    private sealed class Group(string type, string name)
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public bool AmbiguousBridge { get; set; }
        public List<FeeContainerLiveVariable> Variables { get; } = [];
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.SpecialDevices;

/// <summary>Projects one current device instance, independently of its generation history or sibling devices.</summary>
public static class FeeSpecialDeviceLiveProjector
{
    public static IReadOnlyList<Fee2SpecialDeviceRoot> Project(IReadOnlyList<FeeLogic> logics,
        IReadOnlyList<FeeContainerLiveVariable> variables, IReadOnlyList<FeeContainerLiveAssignment> assignments)
    {
        var variableByGuid = variables.DistinctBy(item => item.VariableGuid).ToDictionary(item => item.VariableGuid);
        var byLogic = assignments.ToLookup(item => item.TargetObjectGuid);
        var result = new List<Fee2SpecialDeviceRoot>();
        foreach (var logic in logics.DistinctBy(item => item.Guid))
        {
            var definition = FeeSpecialDeviceLogicCatalog.Find(logic.LogicDefinitionName);
            if (definition is null) continue;
            var current = byLogic[logic.Guid].Where(item => variableByGuid.ContainsKey(item.VariableGuid)).Distinct().ToArray();
            var signals = current.GroupBy(item => item.VariableGuid).Select(group =>
            {
                var variable = variableByGuid[group.Key];
                return new FeeSpecialDeviceSignalSnapshot(variable.VariableGuid, variable.Signal, variable.Address,
                    variable.Usage, variable.DataType, variable.Comment, variable.Path,
                    group.Select(item => item.TargetSlot).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(slot => slot).ToArray());
            }).OrderBy(item => item.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
            var allSlots = (logic.Slots?.Keys ?? Enumerable.Empty<string>()).Where(IsPlcSlot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var connected = current.Select(item => item.TargetSlot).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inputs = allSlots.Where(slot => slot.StartsWith("PLC_IN", StringComparison.OrdinalIgnoreCase)).ToArray();
            var outputs = allSlots.Where(slot => slot.StartsWith("PLC_OUT", StringComparison.OrdinalIgnoreCase)).ToArray();
            var missing = allSlots.Where(slot => !connected.Contains(slot)).OrderBy(slot => slot).ToArray();
            var inputByte = MinimumByte(signals, input: true);
            var outputByte = MinimumByte(signals, input: false);
            var prefix = string.IsNullOrWhiteSpace(logic.Name) ? logic.GuidString : logic.Name.Trim();
            var marker = prefix.LastIndexOf(" (", StringComparison.Ordinal);
            if (marker > 0) prefix = prefix[..marker];
            var snapshot = new FeeSpecialDeviceSnapshot(FeeSpecialDeviceProvenanceCodec.CurrentSchema, prefix,
                definition.Manufacturer, definition.DeviceType, InferRobotType(signals), inputByte ?? 0, outputByte ?? 0, signals);
            result.Add(new Fee2SpecialDeviceRoot(logic.Guid, logic.Name ?? prefix, snapshot, signals.Length, missing.Length,
                HasProvenance: false, new(inputs.Length, inputs.Count(connected.Contains), outputs.Length, outputs.Count(connected.Contains), missing),
                logic.LogicDefinitionName ?? "", logic.Parent?.Name ?? "", inputByte, outputByte));
        }
        return result;
    }

    private static bool IsPlcSlot(string name) => name.StartsWith("PLC_IN", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("PLC_OUT", StringComparison.OrdinalIgnoreCase);

    public static int? MinimumByte(IEnumerable<FeeSpecialDeviceSignalSnapshot> signals, bool input)
    {
        var bytes = new List<int>();
        foreach (var signal in signals)
        {
            var text = (string.IsNullOrWhiteSpace(signal.Address) ? signal.Path : signal.Address).Trim();
            var plc = Regex.Match(text, @"^%?P?([AEIQ])[XBWDL]?(\d+)(?:\.\d+)?$", RegexOptions.IgnoreCase);
            if (plc.Success && int.TryParse(plc.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                var isInput = plc.Groups[1].Value.Equals("E", StringComparison.OrdinalIgnoreCase) || plc.Groups[1].Value.Equals("I", StringComparison.OrdinalIgnoreCase);
                if (isInput == input) bytes.Add(number);
                continue;
            }
            var numeric = Regex.Match(text, @"^(?:\$?(?:IN|OUT)|D(?:IN|OUT))?\[?(\d+)(?:\.\d+)?\]?$", RegexOptions.IgnoreCase);
            if (numeric.Success && int.TryParse(numeric.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                string.Equals(signal.Usage, input ? "Write" : "Read", StringComparison.OrdinalIgnoreCase)) bytes.Add(number);
        }
        return bytes.Count == 0 ? null : bytes.Min();
    }

    public static string? InferRobotType(IEnumerable<FeeSpecialDeviceSignalSnapshot> signals)
    {
        var addresses = signals.Select(item => item.Address).Where(address => !string.IsNullOrWhiteSpace(address)).ToArray();
        if (addresses.Any(address => address.StartsWith("$IN[", StringComparison.OrdinalIgnoreCase) || address.StartsWith("$OUT[", StringComparison.OrdinalIgnoreCase))) return "Kuka";
        if (addresses.Any(address => address.StartsWith("DIN[", StringComparison.OrdinalIgnoreCase) || address.StartsWith("DOUT[", StringComparison.OrdinalIgnoreCase))) return "Fanuc";
        return addresses.Length > 0 && addresses.All(address => double.TryParse(address, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? "ABB" : null;
    }
}

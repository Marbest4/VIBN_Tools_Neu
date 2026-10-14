using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.SpecialDevices;

public static class FeeSpecialDeviceCandidateFactory
{
    public static Fee2SpecialDeviceRoot Create(string name, string type, string logic, string parent, Guid objectGuid,
        IReadOnlyList<FeeContainerLiveVariable> variables, IReadOnlyList<FeeContainerLiveAssignment> assignments,
        Fee2SpecialDeviceSignalCoverage? coverage = null)
    {
        var definition = FeeSpecialDeviceLogicCatalog.Find(logic);
        var byGuid = variables.DistinctBy(item => item.VariableGuid).ToDictionary(item => item.VariableGuid);
        var signals = assignments.Where(item => byGuid.ContainsKey(item.VariableGuid)).GroupBy(item => item.VariableGuid).Select(group =>
        {
            var variable = byGuid[group.Key];
            return new FeeSpecialDeviceSignalSnapshot(variable.VariableGuid, variable.Signal, variable.Address, variable.Usage,
                variable.DataType, variable.Comment, variable.Path, group.Select(item => item.TargetSlot).Distinct().ToArray());
        }).ToArray();
        var input = FeeSpecialDeviceLiveProjector.MinimumByte(signals, true); var output = FeeSpecialDeviceLiveProjector.MinimumByte(signals, false);
        var snapshot = new FeeSpecialDeviceSnapshot(FeeSpecialDeviceProvenanceCodec.CurrentSchema, name,
            definition?.Manufacturer ?? "Nicht definiert", definition?.DeviceType ?? type, FeeSpecialDeviceLiveProjector.InferRobotType(signals), input ?? 0, output ?? 0, signals);
        return new(objectGuid, name, snapshot, signals.Length, coverage?.MissingSlots.Count ?? 0, false, SignalCoverage: coverage, LogicDefinitionName: logic, ParentName: parent,
            CurrentInputByte: input, CurrentOutputByte: output, IsDefinitionKnown: definition is not null,
            DefinitionWarning: definition is null ? $"Logik '{logic}' ist noch nicht in SpecialDevices definiert." : "");
    }
}

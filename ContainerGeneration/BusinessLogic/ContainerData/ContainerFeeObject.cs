using System.Xml.Serialization;

namespace VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

/// <summary>Portable FEE identity. GUIDs distinguish same-name objects.</summary>
public sealed class ContainerFeeObject
{
    public string Guid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FeeType { get; set; } = string.Empty;
    public string Role { get; set; } = "SimObject";
    public string Target { get; set; } = string.Empty;
    public string ClrType { get; set; } = string.Empty;
    [XmlArray("Slots"), XmlArrayItem("Slot")]
    public List<ContainerFeeSlot> Slots { get; set; } = [];
    public ContainerFeeObject Clone() => new()
    {
        Guid = Guid, Name = Name, FeeType = FeeType, Role = Role, Target = Target, ClrType = ClrType,
        Slots = Slots.Select(slot => new ContainerFeeSlot { Name = slot.Name, AssignedGuid = slot.AssignedGuid }).ToList()
    };
}

public sealed class ContainerFeeSlot
{
    [XmlAttribute("name")] public string Name { get; set; } = string.Empty;
    [XmlAttribute("assignedGuid")] public string AssignedGuid { get; set; } = string.Empty;
}

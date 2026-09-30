using System.Xml.Linq;

namespace VIBN_Tools.Rockwell;

internal static class RockwellSimulationTemplates
{
    public static XElement CreateSimulationModesDataType() => Parse("""
        <DataType Name="SIMULATION_MODES" Family="NoFamily" Class="User">
          <Members>
            <Member Name="ZZZZZZZZZZSIMULATION0" DataType="SINT" Dimension="0" Radix="Decimal" Hidden="true" ExternalAccess="Read/Write" />
            <Member Name="Lifetime" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="0" ExternalAccess="Read/Write" />
            <Member Name="LifetimeFeedback" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="1" ExternalAccess="Read/Write" />
            <Member Name="SimulationActive" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="2" ExternalAccess="Read/Write" />
            <Member Name="SimulationActiveRequest" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="3" ExternalAccess="Read/Write" />
            <Member Name="SimulationActiveFeedback" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="4" ExternalAccess="Read/Write" />
            <Member Name="SimulationNotActive" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="5" ExternalAccess="Read/Write" />
            <Member Name="SimulationNotActiveRequest" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="6" ExternalAccess="Read/Write" />
            <Member Name="SimulationNotActiveFeedback" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION0" BitNumber="7" ExternalAccess="Read/Write" />
            <Member Name="ZZZZZZZZZZSIMULATION9" DataType="SINT" Dimension="0" Radix="Decimal" Hidden="true" ExternalAccess="Read/Write" />
            <Member Name="SkipSafety" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION9" BitNumber="0" ExternalAccess="Read/Write" />
            <Member Name="SkipSafetyRequest" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION9" BitNumber="1" ExternalAccess="Read/Write" />
            <Member Name="SkipSafetyFeedback" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZSIMULATION9" BitNumber="2" ExternalAccess="Read/Write" />
          </Members>
        </DataType>
        """);

    public static XElement CreateSimulationModeInstruction()
    {
        var definition = new XElement("AddOnInstructionDefinition",
            new XAttribute("Name", "SimulationMode"),
            new XAttribute("Class", "Standard"),
            new XAttribute("Revision", "1.0"),
            new XAttribute("ExecutePrescan", "false"),
            new XAttribute("ExecutePostscan", "false"),
            new XAttribute("ExecuteEnableInFalse", "false"),
            new XAttribute("CreatedDate", "2025-04-08T08:30:40.453Z"),
            new XAttribute("CreatedBy", @"GROB\Tool"),
            new XAttribute("EditedDate", "2025-04-08T08:30:40.453Z"),
            new XAttribute("EditedBy", @"GROB\Tool"),
            new XElement("Parameters",
                Parameter("EnableIn", "BOOL", "Input", required: false, visible: false, "Read Only",
                    "Enable Input - System Defined Parameter"),
                Parameter("EnableOut", "BOOL", "Output", required: false, visible: false, "Read Only",
                    "Enable Output - System Defined Parameter"),
                new XElement("Parameter",
                    new XAttribute("Name", "Simulation_Modes"),
                    new XAttribute("TagType", "Base"),
                    new XAttribute("DataType", "SIMULATION_MODES"),
                    new XAttribute("Usage", "InOut"),
                    new XAttribute("Required", "true"),
                    new XAttribute("Visible", "true"),
                    new XAttribute("Constant", "false"))),
            new XElement("LocalTags",
                CreateTimerLocalTag("LifetimeTimer0", 10000),
                CreateTimerLocalTag("LifetimeTimer1", 10000),
                CreateScalarLocalTag("LifetimeOk", "BOOL", "Decimal", "0"),
                CreateScalarLocalTag("Simulation_Modes_Int", "INT", "Decimal", "0")),
            new XElement("Routines",
                new XElement("Routine", new XAttribute("Name", "Logic"), new XAttribute("Type", "RLL"),
                    new XElement("RLLContent",
                        Rung(0, "XIO(Simulation_Modes.Lifetime)TON(LifetimeTimer0,?,?);"),
                        Rung(1, "XIC(Simulation_Modes.Lifetime)TON(LifetimeTimer1,?,?);"),
                        Rung(2, "XIC(Simulation_Modes.Lifetime)OTL(LifetimeOk);"),
                        Rung(3, "[XIC(LifetimeTimer0.DN),XIC(LifetimeTimer1.DN)]OTU(LifetimeOk);"),
                        Rung(4, "XIC(Simulation_Modes.Lifetime)OTE(Simulation_Modes.LifetimeFeedback);"),
                        Rung(5, "XIC(Simulation_Modes.SimulationNotActiveRequest)MOV(0,Simulation_Modes_Int);"),
                        Rung(6, "XIC(Simulation_Modes.SimulationActiveRequest)MOV(1,Simulation_Modes_Int);"),
                        Rung(7, "XIC(Simulation_Modes.SkipSafetyRequest)MOV(2,Simulation_Modes_Int);"),
                        Rung(8, "[CMP(Simulation_Modes_Int = 0),XIO(LifetimeOk)]OTU(Simulation_Modes.SimulationActive)OTU(Simulation_Modes.SkipSafety)OTL(Simulation_Modes.SimulationNotActive);"),
                        Rung(9, "XIC(LifetimeOk)CMP(Simulation_Modes_Int = 1)OTU(Simulation_Modes.SimulationNotActive)OTU(Simulation_Modes.SkipSafety)OTL(Simulation_Modes.SimulationActive);"),
                        Rung(10, "XIC(LifetimeOk)CMP(Simulation_Modes_Int = 2)OTU(Simulation_Modes.SimulationNotActive)OTL(Simulation_Modes.SimulationActive)OTL(Simulation_Modes.SkipSafety);"),
                        Rung(11, "XIC(Simulation_Modes.SimulationNotActive)OTE(Simulation_Modes.SimulationNotActiveFeedback);"),
                        Rung(12, "XIC(Simulation_Modes.SimulationActive)XIO(Simulation_Modes.SkipSafety)OTE(Simulation_Modes.SimulationActiveFeedback);"),
                        Rung(13, "XIC(Simulation_Modes.SkipSafety)OTE(Simulation_Modes.SkipSafetyFeedback);")))));
        return definition;
    }

    public static XElement CreateSimulationModesTag(bool safety)
    {
        var tag = new XElement("Tag",
            new XAttribute("Name", safety ? "s_TBD_Simulation_Modes" : "TBD_Simulation_Modes"),
            new XAttribute("Class", safety ? "Safety" : "Standard"),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", "SIMULATION_MODES"),
            new XAttribute("Constant", "false"),
            new XAttribute("ExternalAccess", "Read/Write"),
            Data("L5K", new XCData("[0,0]")),
            Data("Decorated", new XElement("Structure",
                new XAttribute("DataType", "SIMULATION_MODES"),
                SimulationModeMembers.Select(name => new XElement("DataValueMember",
                    new XAttribute("Name", name),
                    new XAttribute("DataType", "BOOL"),
                    new XAttribute("Value", "0"))))));
        return tag;
    }

    public static XElement CreateSimulationTimerTag() =>
        new("Tag",
            new XAttribute("Name", "Sim_Called_TBDSim"),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", "TIMER"),
            new XAttribute("Constant", "false"),
            new XAttribute("ExternalAccess", "Read/Write"),
            Data("L5K", new XCData("[0,1000,0]")),
            Data("Decorated", TimerStructure(1000)));

    public static XElement CreateSimulationModeCallTag() =>
        new("Tag",
            new XAttribute("Name", "SimMode"),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", "SimulationMode"),
            new XAttribute("Constant", "false"),
            new XAttribute("ExternalAccess", "Read/Write"),
            Data("L5K", new XCData("[7,[-1071452801,10000,1639],[2287381,10000,0],1]")),
            Data("Decorated", new XElement("Structure",
                new XAttribute("DataType", "SimulationMode"),
                ValueMember("EnableIn", "BOOL", "1"),
                ValueMember("EnableOut", "BOOL", "1"))));

    public static IReadOnlyList<XElement> CreateHeaderRungs(string simulationTag, bool includeSimulationModeCall) =>
    [
        CommentedRung(0, "TON(Sim_Called_TBDSim,?,?)XIC(Sim_Called_TBDSim.DN)RES(Sim_Called_TBDSim);"),
        CommentedRung(1, includeSimulationModeCall
            ? $"SimulationMode(SimMode,{simulationTag});"
            : "NOP();"),
        CommentedRung(2, $"XIO({simulationTag}.SimulationActive)TND();")
    ];

    private static readonly string[] SimulationModeMembers =
    [
        "Lifetime", "LifetimeFeedback", "SimulationActive", "SimulationActiveRequest",
        "SimulationActiveFeedback", "SimulationNotActive", "SimulationNotActiveRequest",
        "SimulationNotActiveFeedback", "SkipSafety", "SkipSafetyRequest", "SkipSafetyFeedback"
    ];

    private static XElement Parameter(
        string name, string dataType, string usage, bool required, bool visible,
        string externalAccess, string description) =>
        new("Parameter",
            new XAttribute("Name", name),
            new XAttribute("TagType", "Base"),
            new XAttribute("DataType", dataType),
            new XAttribute("Usage", usage),
            new XAttribute("Radix", "Decimal"),
            new XAttribute("Required", required.ToString().ToLowerInvariant()),
            new XAttribute("Visible", visible.ToString().ToLowerInvariant()),
            new XAttribute("ExternalAccess", externalAccess),
            new XElement("Description", new XCData(description)));

    private static XElement CreateTimerLocalTag(string name, int preset) =>
        new("LocalTag",
            new XAttribute("Name", name),
            new XAttribute("DataType", "TIMER"),
            new XAttribute("ExternalAccess", "None"),
            DefaultData("L5K", new XCData($"[0,{preset},0]")),
            DefaultData("Decorated", TimerStructure(preset)));

    private static XElement CreateScalarLocalTag(string name, string type, string radix, string value) =>
        new("LocalTag",
            new XAttribute("Name", name),
            new XAttribute("DataType", type),
            new XAttribute("Radix", radix),
            new XAttribute("ExternalAccess", "None"),
            DefaultData("L5K", new XCData(value)),
            DefaultData("Decorated", new XElement("DataValue",
                new XAttribute("DataType", type),
                new XAttribute("Radix", radix),
                new XAttribute("Value", value))));

    private static XElement TimerStructure(int preset) =>
        new("Structure",
            new XAttribute("DataType", "TIMER"),
            ValueMember("PRE", "DINT", preset.ToString(), "Decimal"),
            ValueMember("ACC", "DINT", "0", "Decimal"),
            ValueMember("EN", "BOOL", "0"),
            ValueMember("TT", "BOOL", "0"),
            ValueMember("DN", "BOOL", "0"));

    private static XElement ValueMember(string name, string type, string value, string? radix = null)
    {
        var element = new XElement("DataValueMember",
            new XAttribute("Name", name),
            new XAttribute("DataType", type));
        if (radix is not null)
            element.Add(new XAttribute("Radix", radix));
        element.Add(new XAttribute("Value", value));
        return element;
    }

    private static XElement Data(string format, object content) =>
        new("Data", new XAttribute("Format", format), content);

    private static XElement DefaultData(string format, object content) =>
        new("DefaultData", new XAttribute("Format", format), content);

    private static XElement Rung(int number, string text) =>
        new("Rung",
            new XAttribute("Number", number),
            new XAttribute("Type", "N"),
            new XElement("Text", new XCData(text)));

    private static XElement CommentedRung(int number, string text) =>
        new("Rung",
            new XAttribute("Number", number),
            new XAttribute("Type", "N"),
            new XElement("Comment", new XCData("################################################################\n# Delete after Simulation\n################################################################")),
            new XElement("Text", new XCData(text)));

    private static XElement Parse(string xml) => XElement.Parse(xml, LoadOptions.PreserveWhitespace);
}

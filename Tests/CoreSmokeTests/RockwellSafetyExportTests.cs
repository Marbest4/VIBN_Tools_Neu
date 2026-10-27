using NPOI.XSSF.UserModel;
using VIBN_Tools.Rockwell;

internal static class RockwellSafetyExportTests
{
    public static async Task VerifyAsync(string temporaryRoot)
    {
        await VerifyGateBoxModuleAndAliasAsync(temporaryRoot);
        var source = Path.Combine(temporaryRoot, "SafetyInterface.L5X");
        const string xml = """
            <RSLogix5000Content><Controller Name="SafetyController"><Tags>
              <Tag Name="BK01" Class="Standard"><Data Format="Decorated"><Structure>
                <StructureMember Name="I1">
                  <DataValueMember Name="D1" DataType="BOOL" Value="0" />
                  <DataValueMember Name="VS100G1" DataType="BOOL" Value="0" />
                  <DataValueMember Name="VStatus" DataType="BOOL" Value="0" />
                  <DataValueMember Name="Diagnostics" DataType="BOOL" Value="0" />
                </StructureMember>
                <StructureMember Name="O1"><DataValueMember Name="VS2" DataType="BOOL" Value="0" /></StructureMember>
                <StructureMember Name="Safety">
                  <StructureMember Name="I1"><DataValueMember Name="D2" DataType="BOOL" Value="0" /></StructureMember>
                </StructureMember>
              </Structure></Data></Tag>
              <Tag Name="VS100G1" Class="Safety"><Data Format="Decorated"><Structure>
                <StructureMember Name="Inputs">
                  <StructureMember Name="Channel"><DataValueMember Name="EStop" DataType="BOOL" Value="0" /></StructureMember>
                  <ArrayMember Name="Flags" DataType="BOOL" Dimensions="2">
                    <Element Index="[0]" Value="0" />
                    <Element Index="[1]" Value="1" />
                  </ArrayMember>
                </StructureMember>
                <StructureMember Name="Outputs">
                  <DataValueMember Name="Reset" DataType="BOOL" Value="0" />
                  <DataValueMember Name="Count" DataType="DINT" Value="0" />
                  <DataValueMember Name="Text" DataType="STRING" Value="ignored" />
                </StructureMember>
              </Structure></Data></Tag>
              <Tag Name="SafePorts"><Data Format="Decorated"><Structure>
                <StructureMember Name="SI1"><DataValueMember Name="Closed" DataType="BOOL" Value="0" /></StructureMember>
                <StructureMember Name="SO1"><DataValueMember Name="Enable" DataType="BOOL" Value="0" /></StructureMember>
              </Structure></Data></Tag>
              <Tag Name="s_CustomIo"><Data Format="Decorated"><Structure>
                <StructureMember Name="I1"><DataValueMember Name="Custom" DataType="BOOL" Value="0" /></StructureMember>
              </Structure></Data></Tag>
              <Tag Name="Debug" Class="Safety"><Data Format="Decorated"><Structure>
                <StructureMember Name="I1"><DataValueMember Name="D1" DataType="BOOL" Value="0" /></StructureMember>
              </Structure></Data></Tag>
              <Tag Name="VS100G1_Standard"><Data Format="Decorated"><Structure>
                <StructureMember Name="I1"><DataValueMember Name="D1" DataType="BOOL" Value="0" /></StructureMember>
              </Structure></Data></Tag>
              <Tag Name="SimulationIo" Class="Safety"><Description>Simulation</Description><Data Format="Decorated"><Structure>
                <StructureMember Name="I1"><DataValueMember Name="D1" DataType="BOOL" Value="0" /></StructureMember>
              </Structure></Data></Tag>
              <Tag Name="UnknownDirection" Class="Safety" DataType="BOOL"><Data Format="Decorated"><DataValue DataType="BOOL" Value="0" /></Data></Tag>
            </Tags><Programs><Program Name="s_Station" Class="Safety"><Tags>
              <Tag Name="Alias" AliasFor="VS100G1"><Comments>
                <Comment Operand=".Inputs.Channel.EStop">Emergency stop</Comment>
              </Comments></Tag>
            </Tags></Program></Programs></Controller></RSLogix5000Content>
            """;
        await File.WriteAllTextAsync(source, xml);
        var standard = AllenBradleyInterfaceExcelExporter.ReadRows(source, null, out var standardWarnings);
        var expectedStandard = new[] { "BK01.I1.D1", "BK01.I1.VS100G1", "BK01.O1.VS2" };
        Assert(standard.Select(row => row.Tag).ToHashSet().SetEquals(expectedStandard) && standardWarnings.Count == 0,
            "The standard-only export included Safety signals, rejected alphanumeric VS names, or exported unrelated names.");

        var all = AllenBradleyInterfaceExcelExporter.ReadRows(source, null, out var warnings, includeSafetySignals: true);
        var expectedSafety = new[]
        {
            "BK01.Safety.I1.D2", "VS100G1.Inputs.Channel.EStop", "VS100G1.Inputs.Flags[0]", "VS100G1.Inputs.Flags[1]",
            "VS100G1.Outputs.Reset", "VS100G1.Outputs.Count", "SafePorts.SI1.Closed", "SafePorts.SO1.Enable", "s_CustomIo.I1.Custom"
        };
        Assert(all.Select(row => row.Tag).ToHashSet().SetEquals(expectedStandard.Concat(expectedSafety)),
            "The Safety export lost nested inputs, array elements, custom members, or source exclusions.");
        Assert(all.Single(row => row.Tag == "VS100G1.Inputs.Channel.EStop") is { Usage: "Write", Comment: "Emergency stop" } &&
               all.Single(row => row.Tag == "VS100G1.Outputs.Count") is { Usage: "Read", DataType: "DINT" },
            "Safety direction, comments, or actual Logix data types were not retained.");
        Assert(warnings.Any(warning => warning.Contains("UnknownDirection", StringComparison.Ordinal)) &&
               warnings.Any(warning => warning.Contains("VS100G1.Outputs.Text", StringComparison.Ordinal)),
            "Skipped Safety signals lack their exact path and reason.");

        var withoutSafetyPath = Path.Combine(temporaryRoot, "WithoutSafety.xlsx");
        var withSafetyPath = Path.Combine(temporaryRoot, "WithSafety.xlsx");
        Assert(AllenBradleyInterfaceExcelExporter.Export(source, withoutSafetyPath).ExportedSignalCount == standard.Count &&
               AllenBradleyInterfaceExcelExporter.Export(source, withSafetyPath, includeSafetySignals: true).ExportedSignalCount == all.Count,
            "The export entry point ignored the selected Safety option.");
        using (var stream = File.OpenRead(withSafetyPath))
        using (var workbook = new XSSFWorkbook(stream))
        {
            var sheet = workbook.GetSheet("InterfaceSimExport") ?? throw new InvalidOperationException("Interface sheet is missing.");
            var rows = Enumerable.Range(1, sheet.LastRowNum).Select(index =>
                sheet.GetRow(index) ?? throw new InvalidOperationException("Interface row is missing.")).ToArray();
            var stop = rows.Single(row => row.GetCell(1).StringCellValue == "VS100G1.Inputs.Channel.EStop");
            var count = rows.Single(row => row.GetCell(1).StringCellValue == "VS100G1.Outputs.Count");
            Assert(sheet.GetRow(0).LastCellNum == 8 && stop.GetCell(3).StringCellValue == "Emergency stop" &&
                   stop.GetCell(4).StringCellValue == "Write" && count.GetCell(2).StringCellValue == "DINT" &&
                   count.GetCell(4).StringCellValue == "Read" && count.GetCell(6).StringCellValue == "0",
                "The Safety workbook violates the interface format or lost signal details.");
        }
        Assert(await File.ReadAllTextAsync(source) == xml, "Safety extraction modified the source L5X.");
    }

    private static async Task VerifyGateBoxModuleAndAliasAsync(string temporaryRoot)
    {
        // Reduced from the reported 1734-IB8S module and G1 alias. Keep the
        // custom Safety member names, alias casing, and real PointIO slot.
        const string xml = """
            <RSLogix5000Content><Controller Name="GateBox"><Modules>
              <Module Name="sz_VS100G1_1" CatalogNumber="1734-IB8S/A" ParentModule="sz_VS100G1" SafetyNetwork="16#0000_401d_035d_9681">
                <Ports><Port Id="1" Address="1" Type="PointIO" Upstream="true" /></Ports>
                <Communications>
                  <ConfigData><Data Format="Decorated"><Structure><DataValueMember Name="Config" DataType="DINT" Value="0" /></Structure></Data></ConfigData>
                  <Connections>
                    <Connection Name="Input" Type="SafetyInput"><InputTag ExternalAccess="Read/Write">
                      <Data Format="Decorated"><Structure DataType="AB:1734_IB8S_Safety5:I:0">
                        <DataValueMember Name="Pt00Data" DataType="BOOL" Value="0" />
                        <DataValueMember Name="Pt07Data" DataType="BOOL" Value="0" />
                        <DataValueMember Name="Pt00TestOutputStatus" DataType="BOOL" Value="0" />
                      </Structure></Data>
                    </InputTag></Connection>
                    <Connection Name="Output" Type="SafetyOutput"><OutputTag ExternalAccess="Read/Write">
                      <Data Format="L5K">[0]</Data>
                      <Data Format="Decorated"><Structure DataType="AB:1734_IB8S:O:0">
                        <DataValueMember Name="Test00Data" DataType="BOOL" Value="0" />
                      </Structure></Data>
                    </OutputTag></Connection>
                  </Connections>
                </Communications>
              </Module>
              <Module Name="VS100BK11" ParentModule="Local"><Ports><Port Address="192.168.1.70" Type="Ethernet" Upstream="true" /></Ports>
                <Communications><ConfigTag><Data Format="Decorated"><Structure><DataValueMember Name="Configuration" DataType="BOOL" Value="0" /></Structure></Data></ConfigTag>
                  <Connections><Connection Name="Standard" Type="Output">
                    <InputTag><Data Format="Decorated"><Structure><ArrayMember Name="Data" DataType="INT"><Element Index="[0]" Value="0" /></ArrayMember></Structure></Data></InputTag>
                  </Connection></Connections>
                </Communications>
              </Module>
            </Modules><Tags>
              <Tag Name="VS100G1" Class="Standard"><Comments><Comment Operand=".O3.D08">Stacklight</Comment></Comments>
                <Data Format="Decorated"><Structure>
                  <StructureMember Name="Safety"><StructureMember Name="I">
                    <DataValueMember Name="SW1CLSDCHA" DataType="BOOL" Value="0" />
                    <DataValueMember Name="RESETPB" DataType="BOOL" Value="0" />
                  </StructureMember></StructureMember>
                  <StructureMember Name="O3"><DataValueMember Name="D08" DataType="BOOL" Value="0" /></StructureMember>
                </Structure></Data>
              </Tag>
              <Tag Name="G1" TagType="Alias" AliasFor="vs100g1"><Comments>
                <Comment Operand=".SAFETY.I.SW1CLSDCHA">GATE SWITCH 1 CHANNEL A</Comment>
                <Comment Operand=".SAFETY.I.RESETPB">RESET PB</Comment>
              </Comments></Tag>
            </Tags></Controller></RSLogix5000Content>
            """;
        var source = Path.Combine(temporaryRoot, "GateBox.L5X");
        await File.WriteAllTextAsync(source, xml);
        var standard = AllenBradleyInterfaceExcelExporter.ReadRows(source, null, out _);
        Assert(standard.Single() is { Tag: "VS100G1.O3.D08", Comment: "Stacklight", Usage: "Read" },
            "The Safety toggle also hid the standard side of a mixed GateBox tag.");
        var all = AllenBradleyInterfaceExcelExporter.ReadRows(source, null, out var warnings, includeSafetySignals: true);
        var expected = new[]
        {
            "VS100G1.O3.D08", "VS100G1.Safety.I.SW1CLSDCHA", "VS100G1.Safety.I.RESETPB",
            "sz_VS100G1:1:I.Pt00Data", "sz_VS100G1:1:I.Pt07Data", "sz_VS100G1:1:I.Pt00TestOutputStatus", "sz_VS100G1:1:O.Test00Data"
        };
        Assert(all.Select(row => row.Tag).ToHashSet().SetEquals(expected) && warnings.Count == 0,
            "GateBox Safety signals, physical PointIO addresses, or standard/configuration exclusions are incorrect.");
        Assert(all.Single(row => row.Tag == "VS100G1.Safety.I.SW1CLSDCHA").Comment == "GATE SWITCH 1 CHANNEL A" &&
               all.Single(row => row.Tag == "VS100G1.Safety.I.RESETPB").Comment == "RESET PB" &&
               all.Single(row => row.Tag == "sz_VS100G1:1:I.Pt07Data").Usage == "Write" &&
               all.Single(row => row.Tag == "sz_VS100G1:1:O.Test00Data").Usage == "Read",
            "Controller alias comments or Safety module directions were lost.");

        // Some exports identify Safety through the connection alone.
        var connectionOnly = Path.Combine(temporaryRoot, "GateBoxConnectionSafety.L5X");
        await File.WriteAllTextAsync(connectionOnly, xml.Replace(" SafetyNetwork=\"16#0000_401d_035d_9681\"", "", StringComparison.Ordinal));
        Assert(AllenBradleyInterfaceExcelExporter.ReadRows(connectionOnly, null, out _, includeSafetySignals: true)
                .Select(row => row.Tag).ToHashSet().SetEquals(expected), "SafetyInput/SafetyOutput connection metadata was ignored.");
        Assert(await File.ReadAllTextAsync(source) == xml, "Reading the GateBox changed its source L5X.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

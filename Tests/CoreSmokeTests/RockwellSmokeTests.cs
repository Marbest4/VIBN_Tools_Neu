using System.Xml.Linq;
using NPOI.XSSF.UserModel;
using VIBN_Tools.Rockwell;

internal static class RockwellSmokeTests
{
    public static async Task VerifyAsync(string temporaryRoot)
    {
        var source = Path.Combine(temporaryRoot, "Controller.L5X");
        await File.WriteAllTextAsync(source, """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <RSLogix5000Content SchemaRevision="1.0">
              <Controller Name="TestController">
                <DataTypes />
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>
                  <Tag Name="BK01" TagType="Base" DataType="TEST_IO">
                    <Data Format="Decorated"><Structure DataType="TEST_IO">
                      <StructureMember Name="I1"><DataValueMember Name="D1" DataType="BOOL" Value="0" /></StructureMember>
                      <StructureMember Name="O1"><DataValueMember Name="VS2" DataType="BOOL" Value="0" /></StructureMember>
                    </Structure></Data>
                  </Tag>
                  <Tag Name="Debug" TagType="Base" DataType="TEST_IO">
                    <Data Format="Decorated"><Structure><StructureMember Name="I1"><DataValueMember Name="D1" DataType="BOOL" Value="0" /></StructureMember></Structure></Data>
                  </Tag>
                </Tags>
                <Programs>
                  <Program Name="Station01">
                    <Tags>
                      <Tag Name="BK01_Alias" AliasFor="BK01">
                        <Data><Structure><DataValueMember Operand=".I1.D1"><Description><![CDATA[Input comment]]></Description></DataValueMember></Structure></Data>
                      </Tag>
                    </Tags>
                    <Routines>
                      <Routine Name="A000_Main" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[JSR(B001_MapInputs,0);]]></Text></Rung></RLLContent></Routine>
                      <Routine Name="B001_MapInputs" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[XIC(InputOK)OTE(Target);]]></Text></Rung></RLLContent></Routine>
                    </Routines>
                  </Program>
                  <Program Name="s_Station01" Class="Safety">
                    <Tags />
                    <Routines>
                      <Routine Name="s_A000_Main" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[JSR(s_B001_MapInputs,0);]]></Text></Rung></RLLContent></Routine>
                      <Routine Name="s_B001_MapInputs" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[XIC(SafetyOK)OTE(Target);]]></Text></Rung></RLLContent></Routine>
                    </Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

        var original = await File.ReadAllTextAsync(source);
        var interfacePath = Path.Combine(temporaryRoot, "Controller_Interface.xlsx");
        var interfaceResult = AllenBradleyInterfaceExcelExporter.Export(source, interfacePath);
        Assert(interfaceResult.ExportedSignalCount == 2, "Allen-Bradley interface export did not find the expected D/VS signals.");
        using (var stream = File.OpenRead(interfacePath))
        using (var workbook = new XSSFWorkbook(stream))
        {
            var sheet = workbook.GetSheet("InterfaceSimExport");
            Assert(sheet is not null && sheet.LastRowNum == 2, "Allen-Bradley interface workbook has an invalid row count.");
            Assert(sheet!.GetRow(0).GetCell(1).StringCellValue == "Tag", "Allen-Bradley interface header is invalid.");
            Assert(sheet.GetRow(1).GetCell(1).StringCellValue == "BK01.I1.D1" &&
                   sheet.GetRow(1).GetCell(3).StringCellValue == "Input comment" &&
                   sheet.GetRow(1).GetCell(4).StringCellValue == "Write",
                "Allen-Bradley input tag, comment or usage was not exported correctly.");
            Assert(sheet.GetRow(2).GetCell(1).StringCellValue == "BK01.O1.VS2" &&
                   sheet.GetRow(2).GetCell(4).StringCellValue == "Read",
                "Allen-Bradley output tag or usage was not exported correctly.");
        }
        Assert(await File.ReadAllTextAsync(source) == original, "Allen-Bradley interface export must not alter the source L5X.");

        var editor = RockwellProjectEditor.Load(source);
        var gccs = RockwellStandardCatalog.All.Single();
        Assert(gccs.Id == "GCCS", "The verified Rockwell standard catalog should expose GCCS explicitly.");
        var basics = gccs.ApplyStage(editor, 1);
        Assert(basics.AddedItems == 4, "Rockwell basic integration should add data type, AOI and both controller tags.");
        Assert(editor.EnsureSimulationBasics().AddedItems == 0, "Rockwell basic integration must be idempotent.");
        var standard = gccs.ApplyStage(editor, 2);
        var safety = gccs.ApplyStage(editor, 3);
        Assert(standard.Changed && safety.Changed, "Rockwell standard and safety routines should be generated.");
        Assert(!editor.EnsureInputSimulation(safety: false).Changed, "Rockwell A001 generation must be idempotent.");

        var output = editor.SaveGenerated();
        Assert(await File.ReadAllTextAsync(source) == original, "Rockwell integration must never overwrite the source L5X.");
        var document = XDocument.Load(output);
        var names = document.Descendants()
            .Select(element => element.Attribute("Name")?.Value)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(names.Contains("SIMULATION_MODES"), "SIMULATION_MODES was not generated.");
        Assert(names.Contains("SimulationMode"), "SimulationMode AOI was not generated.");
        Assert(names.Contains("A001_Simulation"), "Standard A001 routine was not generated.");
        Assert(names.Contains("s_A001_Simulation"), "Safety A001 routine was not generated.");
        var rungText = string.Join("\n", document.Descendants("Text").Select(element => element.Value));
        Assert(rungText.Contains("TBD_Simulation_Modes.SimulationActive", StringComparison.Ordinal),
            "Standard main routine was not guarded by simulation mode.");
        Assert(rungText.Contains("s_TBD_Simulation_Modes.SimulationActive", StringComparison.Ordinal),
            "Safety main routine was not guarded by simulation mode.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

using System.Xml.Linq;
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
                <Tags />
                <Programs>
                  <Program Name="Station01">
                    <Tags />
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
        var editor = RockwellProjectEditor.Load(source);
        var basics = editor.EnsureSimulationBasics();
        Assert(basics.AddedItems == 4, "Rockwell basic integration should add data type, AOI and both controller tags.");
        Assert(editor.EnsureSimulationBasics().AddedItems == 0, "Rockwell basic integration must be idempotent.");
        var standard = editor.EnsureInputSimulation(safety: false);
        var safety = editor.EnsureInputSimulation(safety: true);
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

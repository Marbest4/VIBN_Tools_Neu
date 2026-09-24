using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.Rockwell;

public sealed record RockwellProjectSummary(
    string ControllerName,
    int ProgramCount,
    int StandardProgramCount,
    int SafetyProgramCount,
    int DataTypeCount,
    int AddOnInstructionCount,
    bool HasSimulationBasics,
    int StandardSimulationRoutineCount,
    int SafetySimulationRoutineCount);

public sealed record RockwellEditResult(
    int AddedItems,
    int UpdatedItems,
    IReadOnlyList<string> Messages)
{
    public bool Changed => AddedItems > 0 || UpdatedItems > 0;
}

/// <summary>
/// Schema-aware editor for Studio 5000 L5X exports. The editor keeps a single
/// in-memory XML document, makes every operation idempotent and writes only a
/// new generated file; the selected source file is never overwritten.
/// </summary>
public sealed class RockwellProjectEditor
{
    private const string StandardSimulationRoutine = "A001_Simulation";
    private const string SafetySimulationRoutine = "s_A001_Simulation";
    private readonly XDocument _document;
    private readonly XElement _controller;

    private RockwellProjectEditor(string sourcePath, XDocument document, XElement controller)
    {
        SourcePath = sourcePath;
        _document = document;
        _controller = controller;
    }

    public string SourcePath { get; }

    public string? GeneratedPath { get; private set; }

    public static RockwellProjectEditor Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Eine L5X-Datei muss ausgewählt werden.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Die ausgewählte L5X-Datei wurde nicht gefunden.", path);
        if (!string.Equals(Path.GetExtension(path), ".l5x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rockwell-Projekte müssen als Studio-5000-L5X-Datei vorliegen.");

        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Die L5X-Datei ist kein gültiges XML (Zeile {exception.LineNumber}, Position {exception.LinePosition}).",
                exception);
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "RSLogix5000Content", StringComparison.Ordinal))
            throw new InvalidDataException("Das XML ist kein RSLogix5000Content/L5X-Export.");
        var controller = document.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Controller", StringComparison.Ordinal));
        if (controller is null)
            throw new InvalidDataException("Die L5X-Datei enthält keinen Controller-Knoten.");
        return new RockwellProjectEditor(Path.GetFullPath(path), document, controller);
    }

    public RockwellProjectSummary GetSummary()
    {
        var programs = ChildrenOf(RequireSection("Programs"), "Program").ToArray();
        return new RockwellProjectSummary(
            Attribute(_controller, "Name"),
            programs.Length,
            programs.Count(IsStandardProgram),
            programs.Count(IsSafetyProgram),
            ChildrenOf(RequireSection("DataTypes"), "DataType").Count(),
            ChildrenOf(RequireSection("AddOnInstructionDefinitions"), "AddOnInstructionDefinition").Count(),
            FindNamed(RequireSection("DataTypes"), "DataType", "SIMULATION_MODES") is not null &&
            FindNamed(RequireSection("AddOnInstructionDefinitions"), "AddOnInstructionDefinition", "SimulationMode") is not null,
            programs.Count(program => FindRoutine(program, StandardSimulationRoutine) is not null),
            programs.Count(program => FindRoutine(program, SafetySimulationRoutine) is not null));
    }

    public RockwellEditResult EnsureSimulationBasics()
    {
        var messages = new List<string>();
        var added = 0;
        added += EnsureNamed(
            RequireSection("DataTypes"),
            "DataType",
            "SIMULATION_MODES",
            RockwellSimulationTemplates.CreateSimulationModesDataType,
            messages);
        added += EnsureNamed(
            RequireSection("AddOnInstructionDefinitions"),
            "AddOnInstructionDefinition",
            "SimulationMode",
            RockwellSimulationTemplates.CreateSimulationModeInstruction,
            messages);
        var tags = RequireSection("Tags");
        added += EnsureNamed(tags, "Tag", "TBD_Simulation_Modes",
            () => RockwellSimulationTemplates.CreateSimulationModesTag(safety: false), messages);

        var hasSafetyProgram = ChildrenOf(RequireSection("Programs"), "Program").Any(IsSafetyProgram);
        if (hasSafetyProgram)
        {
            added += EnsureNamed(tags, "Tag", "s_TBD_Simulation_Modes",
                () => RockwellSimulationTemplates.CreateSimulationModesTag(safety: true), messages);
        }
        else
        {
            messages.Add("Kein Safety-Programm erkannt; der Safety-Simulation-Tag wurde nicht angelegt.");
        }

        if (added == 0)
            messages.Add("Die Basic-Simulation war bereits vollständig vorhanden.");
        return new RockwellEditResult(added, 0, messages);
    }

    public RockwellEditResult EnsureInputSimulation(bool safety)
    {
        var basics = EnsureSimulationBasics();
        var messages = basics.Messages.ToList();
        var added = basics.AddedItems;
        var updated = basics.UpdatedItems;
        var generatedRoutineName = safety ? SafetySimulationRoutine : StandardSimulationRoutine;
        var simulationTag = safety ? "s_TBD_Simulation_Modes" : "TBD_Simulation_Modes";
        var simulationCallAdded = false;
        var candidates = ChildrenOf(RequireSection("Programs"), "Program")
            .Where(safety ? IsSafetyProgram : IsStandardProgram)
            .ToArray();

        foreach (var program in candidates)
        {
            var programName = Attribute(program, "Name");
            if (FindRoutine(program, generatedRoutineName) is not null)
            {
                messages.Add($"{programName}: {generatedRoutineName} ist bereits vorhanden.");
                continue;
            }

            var inputRoutine = FindInputMappingRoutine(program, safety);
            var mainRoutine = FindRoutine(program, "A000_Main") ??
                              (safety ? FindRoutine(program, "s_A000_Main") : null);
            if (inputRoutine is null || mainRoutine is null)
            {
                messages.Add($"{programName}: B001_MapInputs und/oder A000_Main fehlen; keine A001-Routine erzeugt.");
                continue;
            }

            var routines = DirectChild(program, "Routines") ??
                throw new InvalidDataException($"Programm '{programName}' enthält keinen Routines-Knoten.");
            routines.Add(CreateSimulationRoutine(
                inputRoutine,
                generatedRoutineName,
                simulationTag,
                includeSimulationModeCall: !safety && !simulationCallAdded,
                messages));
            simulationCallAdded |= !safety;
            added++;

            var tags = EnsureProgramTags(program);
            added += EnsureNamed(tags, "Tag", "Sim_Called_TBDSim",
                RockwellSimulationTemplates.CreateSimulationTimerTag, messages, programName);
            if (!safety)
            {
                added += EnsureNamed(tags, "Tag", "SimMode",
                    RockwellSimulationTemplates.CreateSimulationModeCallTag, messages, programName);
            }

            if (RewriteMainRoutine(mainRoutine, inputRoutine, generatedRoutineName, simulationTag))
                updated++;
            else
                messages.Add($"{programName}: Der Aufruf von {Attribute(inputRoutine, "Name")} wurde in A000_Main nicht gefunden.");
        }

        if (!candidates.Any())
            messages.Add(safety ? "Kein Safety-Programm gefunden." : "Kein Standardprogramm gefunden.");
        return new RockwellEditResult(added, updated, messages);
    }

    public string SaveGenerated(string? outputPath = null)
    {
        outputPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(
                Path.GetDirectoryName(SourcePath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(SourcePath)}_Generated.L5X")
            : Path.GetFullPath(outputPath);
        if (string.Equals(SourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Quelldatei wird nicht überschrieben. Bitte einen anderen Ausgabepfad wählen.");

        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Der Ausgabepfad besitzt kein gültiges Verzeichnis.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _document.Save(temporaryPath, SaveOptions.DisableFormatting);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        GeneratedPath = outputPath;
        return outputPath;
    }

    private XElement CreateSimulationRoutine(
        XElement sourceRoutine,
        string generatedName,
        string simulationTag,
        bool includeSimulationModeCall,
        ICollection<string> messages)
    {
        var sourceContent = DirectChild(sourceRoutine, "RLLContent") ??
            throw new InvalidDataException($"Routine '{Attribute(sourceRoutine, "Name")}' enthält keinen RLLContent-Knoten.");
        var content = new XElement(sourceContent.Name);
        var headerRungs = RockwellSimulationTemplates.CreateHeaderRungs(simulationTag, includeSimulationModeCall);
        foreach (var headerRung in headerRungs)
        {
            ApplyNamespace(headerRung, sourceContent.Name.Namespace);
            content.Add(headerRung);
        }
        var rungNumber = 3;
        foreach (var sourceRung in ChildrenOf(sourceContent, "Rung"))
        {
            var rung = new XElement(sourceRung);
            rung.SetAttributeValue("Number", rungNumber++);
            var text = rung.Descendants().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "Text", StringComparison.Ordinal));
            if (text is not null)
            {
                var sourceText = text.Value;
                var transformed = TransformInputRung(sourceText);
                text.ReplaceNodes(new XCData(transformed));
                if (transformed == "NOP();" && sourceText.Contains(':', StringComparison.Ordinal))
                    messages.Add($"{generatedName}: direkte Moduladresse wurde sicherheitshalber durch NOP ersetzt; Safety-Tag-Zuordnung prüfen.");
            }
            content.Add(rung);
        }
        return new XElement(sourceRoutine.Name,
            new XAttribute("Name", generatedName),
            new XAttribute("Type", Attribute(sourceRoutine, "Type", "RLL")),
            content);
    }

    private static string TransformInputRung(string source)
    {
        if (source.Contains("IOModule", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("EnetMapping", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("FLL", StringComparison.OrdinalIgnoreCase) ||
            source.Contains(':', StringComparison.Ordinal))
        {
            return "NOP();";
        }

        var result = source;
        if (source.Contains("OK", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, @"\bXI[CO]\s*\(", "OTL(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (source.Contains("Fault", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, @"\bXI[CO]\s*\(", "OTU(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return string.IsNullOrWhiteSpace(result) ? "NOP();" : result;
    }

    private static bool RewriteMainRoutine(
        XElement mainRoutine,
        XElement inputRoutine,
        string simulationRoutine,
        string simulationTag)
    {
        var inputName = Attribute(inputRoutine, "Name");
        var changed = false;
        foreach (var text in mainRoutine.Descendants().Where(element =>
                     string.Equals(element.Name.LocalName, "Text", StringComparison.Ordinal)))
        {
            var oldCall = $"JSR({inputName},0);";
            if (!text.Value.Contains(oldCall, StringComparison.OrdinalIgnoreCase))
                continue;
            var replacement =
                $"[XIO({simulationTag}.SimulationActive)JSR({inputName},0)," +
                $"XIC({simulationTag}.SimulationActive)JSR({simulationRoutine},0)];";
            text.ReplaceNodes(new XCData(ReplaceOrdinalIgnoreCase(text.Value, oldCall, replacement)));
            changed = true;
        }
        return changed;
    }

    private XElement EnsureProgramTags(XElement program)
    {
        var tags = DirectChild(program, "Tags");
        if (tags is not null)
            return tags;
        tags = new XElement(program.Name.Namespace + "Tags");
        var routines = DirectChild(program, "Routines");
        if (routines is null)
            program.Add(tags);
        else
            routines.AddBeforeSelf(tags);
        return tags;
    }

    private XElement RequireSection(string name) =>
        DirectChild(_controller, name) ??
        throw new InvalidDataException($"Die L5X-Datei enthält keinen Controller-Abschnitt '{name}'.");

    private static int EnsureNamed(
        XElement section,
        string elementName,
        string name,
        Func<XElement> factory,
        ICollection<string> messages,
        string? scope = null)
    {
        if (FindNamed(section, elementName, name) is not null)
            return 0;
        var element = factory();
        ApplyNamespace(element, section.Name.Namespace);
        section.Add(element);
        messages.Add($"{(string.IsNullOrWhiteSpace(scope) ? string.Empty : scope + ": ")}{name} hinzugefügt.");
        return 1;
    }

    private static XElement? FindInputMappingRoutine(XElement program, bool safety)
    {
        var names = safety
            ? new[] { "s_B001_MapInputs", "B001_MapInputs" }
            : new[] { "B001_MapInputs" };
        return names.Select(name => FindRoutine(program, name)).FirstOrDefault(item => item is not null);
    }

    private static XElement? FindRoutine(XElement program, string name)
    {
        var routines = DirectChild(program, "Routines");
        return routines is null ? null : FindNamed(routines, "Routine", name);
    }

    private static XElement? FindNamed(XElement section, string elementName, string name) =>
        ChildrenOf(section, elementName).FirstOrDefault(element =>
            string.Equals(Attribute(element, "Name"), name, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafetyProgram(XElement program) =>
        Attribute(program, "Name").StartsWith("s_", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Attribute(program, "Class"), "Safety", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandardProgram(XElement program) => !IsSafetyProgram(program);

    private static XElement? DirectChild(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));

    private static IEnumerable<XElement> ChildrenOf(XElement parent, string localName) =>
        parent.Elements().Where(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));

    private static string Attribute(XElement element, string name, string fallback = "") =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))?.Value ?? fallback;

    private static string ReplaceOrdinalIgnoreCase(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? source : source[..index] + newValue + source[(index + oldValue.Length)..];
    }

    private static void ApplyNamespace(XElement element, XNamespace xmlNamespace)
    {
        if (xmlNamespace == XNamespace.None)
            return;
        foreach (var descendant in element.DescendantsAndSelf())
            descendant.Name = xmlNamespace + descendant.Name.LocalName;
    }
}

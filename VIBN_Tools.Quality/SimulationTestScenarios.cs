using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.Quality;

public sealed record SimulationTestStep(string Action, string Target, string Value);

public sealed record SimulationTestAssertion(string Target, string Expected, string Reason);

public sealed record SimulationTestScenario(
    string Id,
    string Container,
    string ContainerType,
    string Name,
    IReadOnlyList<SimulationTestStep> Arrange,
    IReadOnlyList<SimulationTestStep> Act,
    IReadOnlyList<SimulationTestAssertion> Assert,
    bool RequiresDomainReview);

public sealed record SimulationScenarioSet(string SourcePath, IReadOnlyList<SimulationTestScenario> Scenarios, IReadOnlyList<QualityFinding> Findings);

public sealed class SimulationTestScenarioGenerator
{
    public SimulationScenarioSet Generate(string containerXmlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerXmlPath);
        if (!File.Exists(containerXmlPath))
            throw new FileNotFoundException("ContainerFile wurde nicht gefunden.", containerXmlPath);

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50L * 1024 * 1024 };
        using var reader = XmlReader.Create(containerXmlPath, settings);
        var document = XDocument.Load(reader);
        var scenarios = new List<SimulationTestScenario>();
        var findings = new List<QualityFinding>();
        foreach (var container in document.Descendants().Where(element => element.Name.LocalName == "Container"))
        {
            var name = Value(container, "Component");
            var type = Value(container, "Type");
            var entries = container.Descendants().Where(element => element.Name.LocalName == "Entry")
                .Select(entry => new Entry(Value(entry, "ID"), Value(entry, "Signal"), Value(entry, "Slot"), Value(entry, "Address")))
                .ToArray();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            {
                findings.Add(new QualityFinding("Simulationstests", "SCENARIO_CONTAINER_INVALID", QualityStatus.Failed, "Container ohne Name oder Typ kann keinen Testfall erhalten."));
                continue;
            }
            scenarios.AddRange(CreateScenarios(name, type, entries));
        }

        if (scenarios.Count == 0)
            findings.Add(new QualityFinding("Simulationstests", "SCENARIO_NONE", QualityStatus.Warning, "Aus dem ContainerFile konnten keine Testszenarien erzeugt werden."));
        return new SimulationScenarioSet(containerXmlPath, scenarios, findings);
    }

    private static IEnumerable<SimulationTestScenario> CreateScenarios(string container, string type, IReadOnlyList<Entry> entries)
    {
        var inputs = entries.Where(entry => entry.Slot.StartsWith("PLC_IN", StringComparison.OrdinalIgnoreCase) || entry.Address.StartsWith("E", StringComparison.OrdinalIgnoreCase)).ToArray();
        var outputs = entries.Where(entry => entry.Slot.StartsWith("PLC_OUT", StringComparison.OrdinalIgnoreCase) || entry.Address.StartsWith("A", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (type.Contains("Cylinder", StringComparison.OrdinalIgnoreCase) || type.Contains("Lift", StringComparison.OrdinalIgnoreCase) || type.Contains("Door", StringComparison.OrdinalIgnoreCase))
        {
            yield return Build(container, type, "Bewegung und Endlagen", outputs, inputs, true);
            yield break;
        }
        if (type.Contains("Stop", StringComparison.OrdinalIgnoreCase))
        {
            yield return Build(container, type, "Stopper öffnen und Rückmeldung prüfen", outputs, inputs, true);
            yield break;
        }
        if (type.Contains("Sensor", StringComparison.OrdinalIgnoreCase) || type.Contains("Switch", StringComparison.OrdinalIgnoreCase))
        {
            yield return Build(container, type, "Signalzustand umschalten", outputs, inputs.Length > 0 ? inputs : entries.ToArray(), false);
            yield break;
        }
        if (type.Contains("Motion", StringComparison.OrdinalIgnoreCase) || type.Contains("Axis", StringComparison.OrdinalIgnoreCase))
        {
            yield return Build(container, type, "Sollwert und Istwert prüfen", outputs, inputs, true);
            yield break;
        }
        yield return Build(container, type, "Struktur- und Signalreaktion", outputs, inputs, true);
    }

    private static SimulationTestScenario Build(string container, string type, string name, IReadOnlyList<Entry> outputs, IReadOnlyList<Entry> inputs, bool review)
    {
        var arrange = inputs.Select(input => new SimulationTestStep("Setzen", Display(input), "Ausgangszustand")).ToArray();
        var act = outputs.Count > 0
            ? outputs.Select(output => new SimulationTestStep("Schalten", Display(output), "Testwert")).ToArray()
            : [new SimulationTestStep("Modellzustand ändern", container, "Testzustand")];
        var assertions = inputs.Count > 0
            ? inputs.Select(input => new SimulationTestAssertion(Display(input), "definierte Reaktion", $"Rückmeldung für Slot {input.Slot} prüfen")).ToArray()
            : [new SimulationTestAssertion(container, "keine Validierungsfehler", "Container besitzt keine explizite Eingangsrückmeldung")];
        return new SimulationTestScenario(
            StableId(container, type, name), container, type, name, arrange, act, assertions, review);
    }

    private static string Display(Entry entry) => $"{entry.Signal} [{entry.Slot}; {entry.Address}]";
    private static string Value(XElement parent, string localName) => parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;
    private static string StableId(params string[] parts) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\u001f", parts)))).ToLowerInvariant()[..24];
    private sealed record Entry(string Id, string Signal, string Slot, string Address);
}

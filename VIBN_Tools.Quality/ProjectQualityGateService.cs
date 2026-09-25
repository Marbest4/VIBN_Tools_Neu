using System.Xml;

namespace VIBN_Tools.Quality;

public sealed class ProjectQualityGateService
{
    private readonly SimulationTestScenarioGenerator _scenarioGenerator;
    private readonly IReadOnlyList<ISimulationPlatformAdapter> _adapters;
    private readonly QualityEvidenceStore _evidenceStore;
    private readonly ProjectProfilePolicyValidator _profilePolicyValidator;

    public ProjectQualityGateService(
        SimulationTestScenarioGenerator? scenarioGenerator = null,
        IReadOnlyList<ISimulationPlatformAdapter>? adapters = null,
        QualityEvidenceStore? evidenceStore = null,
        ProjectProfilePolicyValidator? profilePolicyValidator = null)
    {
        _scenarioGenerator = scenarioGenerator ?? new SimulationTestScenarioGenerator();
        _adapters = adapters ?? SimulationAdapterCatalog.CreateDefaults();
        _evidenceStore = evidenceStore ?? QualityEvidenceStore.Instance;
        _profilePolicyValidator = profilePolicyValidator ?? new ProjectProfilePolicyValidator();
    }

    public async Task<(QualityGateReport Report, SimulationScenarioSet? Scenarios, IReadOnlyList<SimulationAdapterProbe> AdapterProbes)> RunAsync(
        ProjectProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var findings = ProjectProfileValidator.Validate(profile).ToList();
        ValidateXml("Requirements", profile.RequirementsPath, findings);
        ValidateXml("ContainerFile", profile.ContainerPath, findings);
        if (File.Exists(profile.ContainerPath))
        {
            try
            {
                findings.AddRange(_profilePolicyValidator.Validate(profile));
            }
            catch (Exception exception) when (exception is IOException or XmlException or InvalidDataException)
            {
                findings.Add(new QualityFinding("Projektprofil", "PROFILE_POLICY_FAILED", QualityStatus.Failed, exception.Message));
            }
        }

        SimulationScenarioSet? scenarios = null;
        if (File.Exists(profile.ContainerPath))
        {
            try
            {
                scenarios = _scenarioGenerator.Generate(profile.ContainerPath);
                findings.AddRange(scenarios.Findings);
                findings.Add(new QualityFinding(
                    "Simulationstests",
                    "SCENARIOS_GENERATED",
                    scenarios.Scenarios.Count > 0 ? QualityStatus.Passed : QualityStatus.Warning,
                    $"{scenarios.Scenarios.Count} neutrale Testszenarien erzeugt.",
                    "Domänenspezifische Sollwerte vor einer Live-Ausführung freigeben."));
            }
            catch (Exception exception) when (exception is IOException or XmlException or InvalidDataException)
            {
                findings.Add(new QualityFinding("Simulationstests", "SCENARIO_GENERATION_FAILED", QualityStatus.Failed, exception.Message));
            }
        }

        var probes = new List<SimulationAdapterProbe>();
        foreach (var adapter in _adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await adapter.ProbeAsync(profile, cancellationToken);
            probes.Add(probe);
            var configured = profile.SimulationTools.Any(tool => tool.Enabled && string.Equals(tool.PlatformId, adapter.PlatformId, StringComparison.OrdinalIgnoreCase));
            if (configured)
            {
                findings.Add(new QualityFinding(
                    "Simulationsadapter",
                    probe.IsAvailable ? "ADAPTER_READY_NOT_LIVE" : "ADAPTER_UNAVAILABLE",
                    probe.IsAvailable ? QualityStatus.Warning : QualityStatus.Failed,
                    $"{probe.DisplayName}: {probe.Message}",
                    probe.IsLiveVerified ? string.Empty : "Hersteller-API mit lizenziertem Beispielprojekt live abnehmen."));
            }
        }

        var evidence = _evidenceStore.Load();
        var staleThreshold = DateTimeOffset.UtcNow.AddHours(-24);
        findings.AddRange(evidence
            .Where(item => item.TimestampUtc < staleThreshold)
            .Select(item => new QualityFinding(
                "Nachweise",
                "EVIDENCE_STALE",
                QualityStatus.Warning,
                $"Der Nachweis '{item.Area}' ist älter als 24 Stunden ({item.TimestampUtc.LocalDateTime:dd.MM.yyyy HH:mm}).",
                $"Die Aktion in '{item.Area}' erneut ausführen und danach dieses Quality Gate aktualisieren.")));
        var allStatuses = findings.Select(item => item.Status).Concat(evidence.Select(item => item.Status)).ToArray();
        var overall = allStatuses.Contains(QualityStatus.Failed)
            ? QualityStatus.Failed
            : allStatuses.Contains(QualityStatus.Warning) || allStatuses.Contains(QualityStatus.NotRun)
                ? QualityStatus.Warning
                : QualityStatus.Passed;
        var report = new QualityGateReport(
            profile.Name,
            DateTimeOffset.UtcNow,
            overall,
            findings,
            evidence,
            scenarios?.Scenarios.Count ?? 0,
            profile.Id);
        return (report, scenarios, probes);
    }

    private static void ValidateXml(string area, string path, ICollection<QualityFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50L * 1024 * 1024 });
            while (reader.Read())
            {
            }
            findings.Add(new QualityFinding(area, "XML_WELL_FORMED", QualityStatus.Passed, $"{area}-XML ist wohlgeformt."));
        }
        catch (XmlException exception)
        {
            findings.Add(new QualityFinding(area, "XML_INVALID", QualityStatus.Failed, $"{area}-XML ist ungültig: {exception.Message}"));
        }
    }
}

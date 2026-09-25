using System.Text.Encodings.Web;
using System.Text.Json;

namespace VIBN_Tools.Quality;

public enum QualityStatus
{
    Passed,
    Warning,
    Failed,
    NotRun,
}

public sealed record QualityFinding(
    string Area,
    string Code,
    QualityStatus Status,
    string Message,
    string Recommendation = "");

public sealed record QualityEvidence(
    string Area,
    string Source,
    QualityStatus Status,
    string Summary,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<QualityFinding> Findings);

public sealed record QualityGateReport(
    string ProjectName,
    DateTimeOffset CreatedUtc,
    QualityStatus OverallStatus,
    IReadOnlyList<QualityFinding> Findings,
    IReadOnlyList<QualityEvidence> Evidence,
    int GeneratedScenarioCount,
    string ProfileId)
{
    public int ErrorCount => Findings.Count(item => item.Status == QualityStatus.Failed) +
                             Evidence.Sum(item => item.Findings.Count(finding => finding.Status == QualityStatus.Failed));

    public int WarningCount => Findings.Count(item => item.Status == QualityStatus.Warning) +
                               Evidence.Sum(item => item.Findings.Count(finding => finding.Status == QualityStatus.Warning));
}

public sealed class QualityEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _sync = new();

    public QualityEvidenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VIBN_Tools",
            "quality",
            "evidence.json");
    }

    public static QualityEvidenceStore Instance { get; } = new();

    public event EventHandler? EvidenceChanged;

    public IReadOnlyList<QualityEvidence> Load()
    {
        lock (_sync)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<QualityEvidence>>(File.ReadAllText(_path), JsonOptions) ?? []
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Upsert(QualityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_sync)
        {
            var items = Load().Where(item => !string.Equals(item.Area, evidence.Area, StringComparison.OrdinalIgnoreCase))
                .Append(evidence)
                .OrderBy(item => item.Area, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AtomicJsonFile.Write(_path, items, JsonOptions);
        }
        EvidenceChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class QualityGateReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public (string JsonPath, string HtmlPath) Write(QualityGateReport report, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var fileStem = $"quality-gate-{report.CreatedUtc:yyyyMMdd-HHmmss}";
        var jsonPath = Path.Combine(outputDirectory, fileStem + ".json");
        var htmlPath = Path.Combine(outputDirectory, fileStem + ".html");
        AtomicJsonFile.Write(jsonPath, report, JsonOptions);
        var rows = report.Findings
            .Concat(report.Evidence.SelectMany(item => item.Findings))
            .Select(item => $"<tr><td>{Encode(item.Area)}</td><td>{Encode(item.Code)}</td><td>{Encode(item.Status.ToString())}</td><td>{Encode(item.Message)}</td><td>{Encode(item.Recommendation)}</td></tr>");
        var html = "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><title>VIBN Quality Gate</title>" +
                   "<style>body{font-family:Segoe UI,Arial;margin:24px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:6px;text-align:left}th{background:#eee}</style></head><body>" +
                   $"<h1>{Encode(report.ProjectName)} – Quality Gate</h1><p>Status: <strong>{Encode(report.OverallStatus.ToString())}</strong>; Fehler: {report.ErrorCount}; Warnungen: {report.WarningCount}; Testszenarien: {report.GeneratedScenarioCount}</p>" +
                   "<table><thead><tr><th>Bereich</th><th>Code</th><th>Status</th><th>Meldung</th><th>Empfehlung</th></tr></thead><tbody>" +
                   string.Join(string.Empty, rows) + "</tbody></table></body></html>";
        AtomicTextFile.Write(htmlPath, html);
        return (jsonPath, htmlPath);
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value ?? string.Empty);
}

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        AtomicTextFile.Write(path, JsonSerializer.Serialize(value, options));
    }
}

internal static class AtomicTextFile
{
    public static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}

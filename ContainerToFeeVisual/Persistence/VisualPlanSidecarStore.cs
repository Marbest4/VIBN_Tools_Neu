using System.IO;
using System.Text.Json;

namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Versioned, portable representation of user-edited visual-plan data.</summary>
internal sealed class VisualPlanSidecarDocument
{
    public const int CurrentSchemaVersion = 6;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string SourceXmlPath { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public List<VisualAssignment> Assignments { get; init; } = [];

    public List<VisualCreationRequest> CreationRequests { get; init; } = [];

    public List<VisualGenerationSelection> GenerationSelections { get; init; } = [];

    public List<VisualSignalCreationSelection> SignalCreationSelections { get; init; } = [];

    public List<VisualSignalAssignment> SignalAssignments { get; init; } = [];

    public List<VisualSlotOverride> SlotOverrides { get; init; } = [];

    public VisualExistingInterfaceSelection? ExistingInterfaceSelection { get; init; }
}

internal sealed record VisualSidecarReadResult(
    bool Success,
    VisualPlanSidecarDocument? Document,
    string SourceXmlPath,
    string Message);

/// <summary>
/// Persists visual changes next to the source XML without modifying the source.
/// Writes use a temporary file and an atomic replacement in the same directory.
/// </summary>
internal sealed class VisualPlanSidecarStore(IVisualPlanLogger logger)
{
    private const long MaximumSidecarBytes = 10L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task SaveAsync(VisualPlan plan, string sidecarPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(sidecarPath))
            throw new ArgumentException("Der Sidecar-Pfad darf nicht leer sein.", nameof(sidecarPath));

        var fullSidecarPath = Path.GetFullPath(sidecarPath);
        var directory = Path.GetDirectoryName(fullSidecarPath)
                        ?? throw new InvalidOperationException("Sidecar-Verzeichnis konnte nicht ermittelt werden.");
        Directory.CreateDirectory(directory);

        var document = new VisualPlanSidecarDocument
        {
            SourceXmlPath = Path.GetRelativePath(directory, plan.SourceXmlPath),
            SourceFingerprint = plan.SourceFingerprint,
            Assignments = [.. plan.Assignments],
            CreationRequests = [.. plan.CreationRequests],
            GenerationSelections = [.. plan.GenerationSelections],
            SignalCreationSelections = [.. plan.SignalCreationSelections],
            SignalAssignments = [.. plan.SignalAssignments],
            SlotOverrides = [.. plan.SlotOverrides],
            ExistingInterfaceSelection = plan.ExistingInterfaceSelection,
        };

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullSidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullSidecarPath, overwrite: true);
            logger.Information($"Visueller Plan gespeichert: {fullSidecarPath}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<VisualSidecarReadResult> ReadAsync(
        string sidecarPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath))
            return Failure("Der Sidecar-Pfad ist leer.");

        string fullSidecarPath;
        try
        {
            fullSidecarPath = Path.GetFullPath(sidecarPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.Error("Der Sidecar-Pfad ist ungültig.", exception);
            return Failure("Der Sidecar-Pfad ist ungültig.");
        }

        if (!File.Exists(fullSidecarPath))
            return Failure($"Der gespeicherte Plan wurde nicht gefunden: {fullSidecarPath}");

        var info = new FileInfo(fullSidecarPath);
        if (info.Length > MaximumSidecarBytes)
            return Failure("Der gespeicherte Plan überschreitet die zulässige Größe von 10 MB.");

        try
        {
            await using var stream = new FileStream(
                fullSidecarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<VisualPlanSidecarDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is null)
                return Failure("Der gespeicherte Plan ist leer oder ungültig.");
            if (document.SchemaVersion is < 1 or > VisualPlanSidecarDocument.CurrentSchemaVersion)
            {
                return Failure(
                    $"Sidecar-Schema {document.SchemaVersion} wird nicht unterstützt " +
                    $"(unterstützt: 1 bis {VisualPlanSidecarDocument.CurrentSchemaVersion}).");
            }
            if (string.IsNullOrWhiteSpace(document.SourceXmlPath) ||
                string.IsNullOrWhiteSpace(document.SourceFingerprint))
                return Failure("Der gespeicherte Plan enthält keine gültige XML-Referenz.");

            var directory = Path.GetDirectoryName(fullSidecarPath)!;
            var sourcePath = Path.IsPathRooted(document.SourceXmlPath)
                ? Path.GetFullPath(document.SourceXmlPath)
                : Path.GetFullPath(Path.Combine(directory, document.SourceXmlPath));
            return new VisualSidecarReadResult(
                true,
                document,
                sourcePath,
                "Gespeicherter Plan wurde gelesen.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            logger.Error("Der gespeicherte Plan konnte nicht gelesen werden.", exception);
            return Failure($"Der gespeicherte Plan konnte nicht gelesen werden: {exception.Message}");
        }
    }

    private static VisualSidecarReadResult Failure(string message) =>
        new(false, null, string.Empty, message);
}

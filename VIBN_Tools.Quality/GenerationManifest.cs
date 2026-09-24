using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VIBN_Tools.Quality;

public enum GenerationManifestAction
{
    Reused,
    CreatedOrCompleted,
    Linked,
    Unchanged,
    Failed,
    Pending,
}

public sealed record GenerationObjectObservation(
    string ContainerId,
    string NodeId,
    string Kind,
    string Name,
    string State,
    string ExternalId);

public sealed record GenerationManifestItem(
    string ContainerId,
    string NodeId,
    string Kind,
    string Name,
    string BeforeState,
    string AfterState,
    string ExternalId,
    GenerationManifestAction Action,
    string Message);

public sealed record GenerationManifest(
    string Id,
    string SourcePath,
    string SourceFingerprint,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    bool Success,
    string Summary,
    IReadOnlyList<GenerationManifestItem> Items,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> UnresolvedContainerIds => Items
        .Where(item => item.Action is GenerationManifestAction.Failed or GenerationManifestAction.Pending)
        .Select(item => item.ContainerId)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

public sealed class GenerationManifestBuilder
{
    public GenerationManifest Build(
        string sourcePath,
        string sourceFingerprint,
        DateTimeOffset startedUtc,
        bool success,
        string summary,
        IEnumerable<GenerationObjectObservation> before,
        IEnumerable<GenerationObjectObservation> after,
        IEnumerable<string> errors)
    {
        var beforeById = before.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var afterById = after.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var nodeIds = beforeById.Keys.Concat(afterById.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var items = nodeIds.Select(nodeId => BuildItem(beforeById.GetValueOrDefault(nodeId), afterById.GetValueOrDefault(nodeId))).ToArray();
        return new GenerationManifest(
            StableHash(sourceFingerprint, startedUtc.ToString("O")),
            sourcePath,
            sourceFingerprint,
            startedUtc,
            DateTimeOffset.UtcNow,
            success,
            summary,
            items,
            errors.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static GenerationManifestItem BuildItem(GenerationObjectObservation? before, GenerationObjectObservation? after)
    {
        var effective = after ?? before ?? throw new InvalidOperationException("Manifest item has no observation.");
        var beforeState = before?.State ?? "Nicht erfasst";
        var afterState = after?.State ?? "Fehlt";
        var action = Classify(beforeState, afterState);
        return new GenerationManifestItem(
            effective.ContainerId,
            effective.NodeId,
            effective.Kind,
            effective.Name,
            beforeState,
            afterState,
            after?.ExternalId ?? before?.ExternalId ?? string.Empty,
            action,
            $"{beforeState} → {afterState}");
    }

    private static GenerationManifestAction Classify(string before, string after)
    {
        if (IsError(after))
            return GenerationManifestAction.Failed;
        if (IsVerified(before) && IsVerified(after))
            return GenerationManifestAction.Reused;
        if (IsUnlinked(before) && IsVerified(after))
            return GenerationManifestAction.Linked;
        if (!IsVerified(before) && IsVerified(after))
            return GenerationManifestAction.CreatedOrCompleted;
        if (IsPending(after))
            return GenerationManifestAction.Pending;
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            return GenerationManifestAction.Unchanged;
        return GenerationManifestAction.Pending;
    }

    private static bool IsVerified(string value) => value.Contains("Verified", StringComparison.OrdinalIgnoreCase) || value.Contains("Verknüpft", StringComparison.OrdinalIgnoreCase);
    private static bool IsUnlinked(string value) => value.Contains("Unlinked", StringComparison.OrdinalIgnoreCase) || value.Contains("Verknüpfung fehlt", StringComparison.OrdinalIgnoreCase);
    private static bool IsPending(string value) => value.Contains("Planned", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Contains("Unlinked", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Contains("None", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Contains("Geplant", StringComparison.OrdinalIgnoreCase) ||
                                                   value.Contains("Verknüpfung fehlt", StringComparison.OrdinalIgnoreCase);
    private static bool IsError(string value) => value.Contains("Missing", StringComparison.OrdinalIgnoreCase) || value.Contains("Fehler", StringComparison.OrdinalIgnoreCase);
    private static string StableHash(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values)))).ToLowerInvariant();
}

public sealed class GenerationManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public GenerationManifestStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VIBN_Tools",
            "quality",
            "generation-manifests");
    }

    public string Save(GenerationManifest manifest)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"manifest-{manifest.CompletedUtc:yyyyMMdd-HHmmss}-{manifest.Id[..8]}.json");
        AtomicJsonFile.Write(path, manifest, JsonOptions);
        return path;
    }

    public GenerationManifest? LoadLatest(string sourceFingerprint)
    {
        if (!Directory.Exists(_directory))
            return null;
        foreach (var path in Directory.GetFiles(_directory, "manifest-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<GenerationManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null && string.Equals(manifest.SourceFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
                    return manifest;
            }
            catch (JsonException)
            {
                // A damaged older file is skipped; the next valid manifest remains available.
            }
        }
        return null;
    }
}

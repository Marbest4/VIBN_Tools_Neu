using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VIBN_Tools.ContainerGeneration.AI;

public enum RuleSuggestionStatus
{
    Pending,
    Accepted,
    Rejected
}

public sealed record RuleSuggestion(
    string Id,
    string ProposedRule,
    string ComponentType,
    string SignalText,
    string PropertyName,
    string PreviousValue,
    string NewValue,
    int Frequency,
    int RelevantCases,
    double Confidence,
    RuleSuggestionStatus Status);

public sealed record RuleSuggestionAnalysis(
    IReadOnlyList<RuleSuggestion> Suggestions,
    int ParsedEvents,
    int InvalidLines);

/// <summary>
/// Deterministic first-stage rule mining. It proposes exact signal/type/old-slot
/// rules only; general regex rules require separate evidence and user review.
/// </summary>
public sealed class RuleSuggestionService
{
    public RuleSuggestionAnalysis Analyze(
        IEnumerable<string> logFiles,
        IReadOnlyDictionary<string, RuleSuggestionStatus>? reviewedStatuses = null)
    {
        var events = new List<UserActionEvent>();
        var invalidLines = 0;
        foreach (var file in logFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file))
                continue;
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var action = JsonSerializer.Deserialize<UserActionEvent>(line);
                    if (action is not null)
                        events.Add(action);
                    else
                        invalidLines++;
                }
                catch (JsonException)
                {
                    invalidLines++;
                }
            }
        }

        var candidates = events
            .Where(IsSlotCorrection)
            .Select(action => new Candidate(
                Normalize(action.ComponentType),
                Normalize(action.SignalText),
                action.ComponentType.Trim(),
                action.SignalText.Trim(),
                EffectivePreviousValue(action),
                EffectiveNewValue(action),
                GetCaseKey(action)))
            .ToArray();

        var suggestions = candidates
            .GroupBy(candidate => new
            {
                candidate.NormalizedComponentType,
                candidate.NormalizedSignal,
                Previous = Normalize(candidate.PreviousValue),
                Next = Normalize(candidate.NewValue),
            })
            .Select(group =>
            {
                var sample = group.First();
                var relevant = candidates
                    .Where(candidate =>
                        candidate.NormalizedComponentType == sample.NormalizedComponentType &&
                        candidate.NormalizedSignal == sample.NormalizedSignal &&
                        Normalize(candidate.PreviousValue) == Normalize(sample.PreviousValue))
                    .Select(candidate => candidate.CaseKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var supporting = group
                    .Select(candidate => candidate.CaseKey)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                var id = CreateId(
                    sample.NormalizedComponentType,
                    sample.NormalizedSignal,
                    Normalize(sample.PreviousValue),
                    Normalize(sample.NewValue));
                var status = reviewedStatuses?.TryGetValue(id, out var reviewed) == true
                    ? reviewed
                    : RuleSuggestionStatus.Pending;
                return new RuleSuggestion(
                    id,
                    $"Wenn Typ = '{sample.ComponentType}' und Signal = '{sample.SignalText}', " +
                    $"Slot '{Display(sample.PreviousValue)}' durch '{sample.NewValue}' ersetzen.",
                    sample.ComponentType,
                    sample.SignalText,
                    "Slot",
                    sample.PreviousValue,
                    sample.NewValue,
                    group.Count(),
                    relevant,
                    relevant == 0 ? 0 : (double)supporting / relevant,
                    status);
            })
            .OrderByDescending(suggestion => suggestion.Confidence)
            .ThenByDescending(suggestion => suggestion.Frequency)
            .ThenBy(suggestion => suggestion.ComponentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(suggestion => suggestion.SignalText, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RuleSuggestionAnalysis(suggestions, events.Count, invalidLines);
    }

    private static bool IsSlotCorrection(UserActionEvent action)
    {
        var property = string.IsNullOrWhiteSpace(action.PropertyName) ? "Slot" : action.PropertyName;
        var previous = EffectivePreviousValue(action);
        var next = EffectiveNewValue(action);
        return string.Equals(property, "Slot", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(action.ComponentType) &&
               !string.IsNullOrWhiteSpace(action.SignalText) &&
               !string.IsNullOrWhiteSpace(next) &&
               !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase);
    }

    private static string EffectivePreviousValue(UserActionEvent action) =>
        string.IsNullOrWhiteSpace(action.PreviousValue) ? action.FromSlot ?? string.Empty : action.PreviousValue;

    private static string EffectiveNewValue(UserActionEvent action) =>
        string.IsNullOrWhiteSpace(action.NewValue) ? action.ToSlot ?? string.Empty : action.NewValue;

    private static string GetCaseKey(UserActionEvent action)
    {
        if (!string.IsNullOrWhiteSpace(action.SourceKey) || !string.IsNullOrWhiteSpace(action.SignalId))
            return $"{action.SourceKey}\u001f{action.SignalId}";
        return $"legacy:{action.GetHashCode()}";
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "leer" : value;

    private static string CreateId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values))))
            .ToLowerInvariant();

    private sealed record Candidate(
        string NormalizedComponentType,
        string NormalizedSignal,
        string ComponentType,
        string SignalText,
        string PreviousValue,
        string NewValue,
        string CaseKey);
}

public sealed class RuleSuggestionReviewStore
{
    private readonly string _path;

    public RuleSuggestionReviewStore(string? path = null)
    {
        _path = path ?? Path.Combine(ModelPaths.BaseDir, "rule_suggestion_reviews.json");
    }

    public IReadOnlyDictionary<string, RuleSuggestionStatus> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, RuleSuggestionStatus>(StringComparer.Ordinal);
        var document = JsonSerializer.Deserialize<Dictionary<string, RuleSuggestionStatus>>(
            File.ReadAllText(_path));
        return document is null
            ? new Dictionary<string, RuleSuggestionStatus>(StringComparer.Ordinal)
            : new Dictionary<string, RuleSuggestionStatus>(document, StringComparer.Ordinal);
    }

    public void Save(IReadOnlyDictionary<string, RuleSuggestionStatus> statuses)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Der Review-Pfad besitzt kein Verzeichnis.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(statuses, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

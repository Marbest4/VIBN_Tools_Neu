using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VIBN_Tools.ContainerGeneration.AI;

public sealed record ContainerTemplateSuggestion(
    string Id,
    string ContainerType,
    string SuggestedName,
    string Slots,
    string ExampleSignals,
    int SupportingContainers,
    int Observations,
    double Confidence,
    RuleSuggestionStatus Status);

public sealed record ContainerTemplateSuggestionAnalysis(
    IReadOnlyList<ContainerTemplateSuggestion> Suggestions,
    int ParsedEvents,
    int InvalidLines);

/// <summary>
/// Mines complete destination-container templates from explicit user add/move
/// actions. It deliberately proposes reviewable container types and recurring
/// slot sets; it never creates a container or edits Requirements.xml without a
/// separate user decision.
/// </summary>
public sealed class ContainerTemplateSuggestionService
{
    public ContainerTemplateSuggestionAnalysis Analyze(
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

        var usable = events.Where(action =>
                !string.IsNullOrWhiteSpace(action.ComponentType) &&
                !string.IsNullOrWhiteSpace(action.ToContainer) &&
                !string.IsNullOrWhiteSpace(action.ToSlot) &&
                (string.Equals(action.ActionType, "Add", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action.ActionType, "Move", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(action.PropertyName, "ContainerAndSlot", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var suggestions = usable
            .GroupBy(action => action.ComponentType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSuggestion(group.Key, group.ToArray(), reviewedStatuses))
            .Where(item => item.SupportingContainers >= 2 && !string.IsNullOrWhiteSpace(item.Slots))
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.SupportingContainers)
            .ThenBy(item => item.ContainerType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ContainerTemplateSuggestionAnalysis(suggestions, events.Count, invalidLines);
    }

    private static ContainerTemplateSuggestion BuildSuggestion(
        string componentType,
        IReadOnlyList<UserActionEvent> events,
        IReadOnlyDictionary<string, RuleSuggestionStatus>? reviewedStatuses)
    {
        var cases = events
            .GroupBy(action => GetCaseKey(action), StringComparer.Ordinal)
            .ToArray();
        var slotSupport = events
            .Select(action => action.ToSlot.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(slot => new
            {
                Slot = slot,
                Count = cases.Count(@case => @case.Any(action =>
                    string.Equals(action.ToSlot, slot, StringComparison.OrdinalIgnoreCase))),
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recurringSlots = slotSupport
            .Where(item => cases.Length == 1 || (double)item.Count / cases.Length >= 0.6d)
            .ToArray();
        var confidence = recurringSlots.Length == 0 || cases.Length == 0
            ? 0d
            : recurringSlots.Average(item => (double)item.Count / cases.Length);
        var id = CreateId(componentType, string.Join("|", recurringSlots.Select(item => item.Slot)));
        var status = reviewedStatuses?.TryGetValue(id, out var reviewed) == true
            ? reviewed
            : RuleSuggestionStatus.Pending;
        var exampleName = events
            .GroupBy(action => action.ToContainer, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
        return new ContainerTemplateSuggestion(
            id,
            componentType,
            exampleName,
            string.Join(", ", recurringSlots.Select(item => item.Slot)),
            string.Join(", ", events.Select(action => action.SignalText)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)),
            cases.Length,
            events.Count,
            confidence,
            status);
    }

    private static string GetCaseKey(UserActionEvent action) =>
        $"{action.SourceKey}\u001f{action.ToContainer}";

    private static string CreateId(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values))))
            .ToLowerInvariant();
}

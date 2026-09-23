using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeTopLevelBasicFrameResult(
    IReadOnlyList<Guid> Roots,
    IReadOnlyList<string> Issues);

/// <summary>
/// Selects only BasicFrames from the first scene hierarchy level. The FEE API
/// exposes a flat type query, therefore descendant relations are resolved in
/// bounded parallel reads before presenting roots to either reverse workflow.
/// </summary>
public static class FeeTopLevelBasicFrameDiscovery
{
    public static async Task<FeeTopLevelBasicFrameResult> DiscoverAsync(
        IEnumerable<string> basicFrameGuidTexts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basicFrameGuidTexts);
        var issues = new List<string>();
        var basicFrames = new List<Guid>();
        foreach (var value in basicFrameGuidTexts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(value, out var guid))
                basicFrames.Add(guid);
            else
                issues.Add($"FEE lieferte eine ungültige BasicFrame-ID: '{value}'.");
        }

        using var throttle = new SemaphoreSlim(6);
        var reads = await Task.WhenAll(basicFrames.Select(async basicFrame =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var descendants = await Services.ApiInstance!.Object
                    .GetAllChildrenFromSceneObjectAsync(basicFrame.ToString("D"));
                return (BasicFrame: basicFrame,
                    Descendants: (IReadOnlySet<Guid>)descendants
                        .Select(value => Guid.TryParse(value, out var guid) ? guid : Guid.Empty)
                        .Where(guid => guid != Guid.Empty)
                        .ToHashSet(),
                    Issue: (string?)null);
            }
            catch (Exception exception)
            {
                return (BasicFrame: basicFrame,
                    Descendants: (IReadOnlySet<Guid>)new HashSet<Guid>(),
                    Issue: (string?)$"Untergeordnete Objekte von {basicFrame:D} konnten nicht gelesen werden: {exception.Message}");
            }
            finally
            {
                throttle.Release();
            }
        }));

        issues.AddRange(reads.Select(read => read.Issue).OfType<string>());
        var descendantsByRoot = reads.ToDictionary(read => read.BasicFrame, read => read.Descendants);
        return new FeeTopLevelBasicFrameResult(
            SelectTopLevel(basicFrames, descendantsByRoot),
            issues);
    }

    public static IReadOnlyList<Guid> SelectTopLevel(
        IEnumerable<Guid> basicFrames,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> descendantsByRoot)
    {
        var candidates = basicFrames.Where(guid => guid != Guid.Empty).Distinct().ToArray();
        var nested = descendantsByRoot.Values
            .SelectMany(descendants => descendants)
            .ToHashSet();
        return candidates.Where(candidate => !nested.Contains(candidate)).ToArray();
    }
}

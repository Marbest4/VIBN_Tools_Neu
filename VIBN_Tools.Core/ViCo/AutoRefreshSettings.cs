namespace VIBN_Tools.Core.ViCo;

/// <summary>Persistent, non-secret preferences for the ViCo overview.</summary>
public sealed record ViCoAutoRefreshSettings(
    int IntervalMinutes,
    bool ShowExtendedInformation = false,
    IReadOnlyList<string>? VisibleColumns = null)
{
    public static ViCoAutoRefreshSettings Default { get; } = new(5, false, null);
}

public static class ViCoAutoRefreshPolicy
{
    public const int MinimumIntervalMinutes = 1;
    public const int MaximumIntervalMinutes = 1440;

    public static int Normalize(int intervalMinutes) => Math.Clamp(
        intervalMinutes,
        MinimumIntervalMinutes,
        MaximumIntervalMinutes);
}

public interface IViCoAutoRefreshSettingsStore
{
    Task<ViCoAutoRefreshSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ViCoAutoRefreshSettings settings, CancellationToken cancellationToken = default);
}

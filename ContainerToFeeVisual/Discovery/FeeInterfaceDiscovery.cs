using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeInterfaceDiscoveryResult(
    IReadOnlyList<VisualFeeInterface> Interfaces,
    IReadOnlyDictionary<string, FeeInterface> RuntimeInterfaces,
    IReadOnlyList<VisualFeeSignal> Signals);

/// <summary>
/// Reads existing FEE interfaces once and keeps their SDK-backed objects out
/// of the WPF presentation layer.
/// </summary>
internal sealed class FeeInterfaceDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeInterfaceDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var interfaces = await FeeInterface.GetAllInterfacesAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var unique = interfaces
            .Where(item => item.Guid != Guid.Empty)
            .GroupBy(item => item.Guid.ToString("D"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Guid)
            .ToArray();
        var runtime = unique.ToDictionary(
            item => item.Guid.ToString("D"),
            item => item,
            StringComparer.OrdinalIgnoreCase);
        var result = unique.Select(item => new VisualFeeInterface(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                item.ProviderName ?? string.Empty,
                item.Signals?.Count ?? 0))
            .ToArray();
        var signals = unique
            .SelectMany(parent => (parent.Signals ?? []).Select(signal => new VisualFeeSignal(
                signal.Guid.ToString("D"),
                parent.Guid.ToString("D"),
                parent.Name ?? string.Empty,
                signal.Tag ?? string.Empty,
                signal.Address ?? string.Empty,
                signal.Path ?? string.Empty,
                signal.IOType.ToString(),
                signal.Usage.ToString())))
            .Where(signal => signal.GuidString != Guid.Empty.ToString("D"))
            .GroupBy(signal => signal.GuidString, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(signal => signal.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(signal => signal.Location, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        logger.Information($"{result.Length} vorhandene FEE-Interfaces mit {signals.Length} Signalen gelesen.");
        return new VisualFeeInterfaceDiscoveryResult(result, runtime, signals);
    }
}

using System.Globalization;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Quality;

namespace VIBN_Tools.Application.Runtime;

public sealed class FeeSdkSignalCatalog : IFeeSignalCatalog
{
    public async Task<IReadOnlyList<FeeRuntimeSignal>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GlobalClasses.Services.Connection?.CanUseFeeFeatures != true)
            throw new InvalidOperationException("Keine bestätigte FEE-Verbindung vorhanden.");
        var interfaces = await FeeInterface.GetAllInterfacesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return interfaces.SelectMany(feeInterface => feeInterface.Signals.Select(signal =>
                new FeeRuntimeSignal(
                    signal.Guid,
                    feeInterface.Name ?? "<Interface ohne Name>",
                    signal.Tag ?? string.Empty,
                    signal.Address ?? signal.Path ?? string.Empty,
                    signal.IOType.ToString())))
            .ToArray();
    }
}

/// <summary>Reads live values from the already connected shared FEE SDK.</summary>
public sealed class FeeSdkSignalMonitor : IFeeSignalMonitor
{
    public async Task<string?> ReadAsync(Guid signalGuid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GlobalClasses.Services.ApiInstance is null || GlobalClasses.Services.Connection?.CanUseFeeFeatures != true)
            throw new InvalidOperationException("Keine bestätigte FEE-Verbindung vorhanden.");

        // The exact vendor return type differs between supported SDK builds;
        // keep that type at the adapter boundary while preserving the stable
        // Guid-based contract used by the test engine.
        dynamic interfaceApi = GlobalClasses.Services.ApiInstance.Interface;
        object? response = await interfaceApi.ReadVariableValueByGuidAsync(signalGuid);
        cancellationToken.ThrowIfCancellationRequested();
        return ExtractValue(response);
    }

    private static string? ExtractValue(object? response)
    {
        if (response is null)
            return null;
        var type = response.GetType();
        foreach (var propertyName in new[] { "Value", "VariableValue", "CurrentValue" })
        {
            var property = type.GetProperty(propertyName);
            if (property?.GetValue(response) is { } value)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return Convert.ToString(response, CultureInfo.InvariantCulture);
    }
}

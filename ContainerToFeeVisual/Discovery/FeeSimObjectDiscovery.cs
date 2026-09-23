using FS.SDK;
using FS.SDK.Scene.Objects;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeDiscoveryResult(
    IReadOnlyList<VisualFeeObject> Objects,
    IReadOnlyDictionary<string, FeeAbstractObject> RuntimeObjects);

/// <summary>Reads selectable FEE objects and keeps SDK instances out of the view model.</summary>
internal sealed class FeeSimObjectDiscovery(IVisualPlanLogger logger)
{
    public async Task<VisualFeeDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtimeObjects = (await ContainerToFeeService.GetSimObjectsFromSimultionAsync())
            .Where(item => item is not null)
            .ToList();

        // The unchanged legacy search omits Button although Button_Container
        // exposes a target. Add it only for the new visual workflow.
        runtimeObjects.AddRange(await ReadAdditionalTypeAsync(nameof(Button), cancellationToken));

        var uniqueRuntimeObjects = runtimeObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.GuidString))
            .GroupBy(item => item.GuidString, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FeeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var byId = new Dictionary<string, FeeAbstractObject>(StringComparer.Ordinal);
        var objects = new List<VisualFeeObject>(uniqueRuntimeObjects.Length);
        foreach (var runtimeObject in uniqueRuntimeObjects)
        {
            var id = CreateFeeObjectId(runtimeObject.GuidString);
            byId[id] = runtimeObject;
            objects.Add(new VisualFeeObject(
                id,
                runtimeObject.GuidString,
                runtimeObject.Name ?? string.Empty,
                runtimeObject.GetType().FullName ?? runtimeObject.GetType().Name,
                runtimeObject.FeeType ?? string.Empty,
                GetAssignableTypeNames(runtimeObject.GetType())));
        }

        logger.Information($"{objects.Count} zuweisbare FEE-SimObjects gelesen.");
        return new VisualFeeDiscoveryResult(objects, byId);
    }

    internal static string CreateFeeObjectId(string guidString) =>
        $"fee:{guidString.Trim().ToLowerInvariant()}";

    private static IReadOnlyCollection<string> GetAssignableTypeNames(Type runtimeType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Type? type = runtimeType; type is not null; type = type.BaseType)
        {
            names.Add(type.Name);
            if (type.FullName is not null)
                names.Add(type.FullName);
        }
        foreach (var interfaceType in runtimeType.GetInterfaces())
        {
            names.Add(interfaceType.Name);
            if (interfaceType.FullName is not null)
                names.Add(interfaceType.FullName);
        }
        return names;
    }

    private static async Task<IReadOnlyList<FeeAbstractObject>> ReadAdditionalTypeAsync(
        string objectType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var guids = await Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(objectType);
        cancellationToken.ThrowIfCancellationRequested();
        if (!guids.Any())
            return [];

        var guidArray = guids.ToArray();
        var names = (await Services.ApiInstance.Object.GetPropertiesAsync(
            guidArray,
            nameof(SceneObject.Name))).ToArray();
        var types = (await Services.ApiInstance.Object.GetPropertiesAsync(
            guidArray,
            nameof(SceneObject.Type))).ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        return guidArray
            .Zip(names, (guid, name) => new { Guid = guid, Name = name })
            .Zip(types, (item, type) => FeeObjectFactory.Create(
                type,
                Services.ApiInstance.XmlHelper.ConvertToString(item.Name),
                item.Guid))
            .Where(item => item is not null)
            .ToArray()!;
    }
}

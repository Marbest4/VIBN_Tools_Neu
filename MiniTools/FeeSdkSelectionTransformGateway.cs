using FS.SDK;
using FS.SDK.Components;
using FS.SDK.Mathematics;
using FS.SDK.Scene.Objects;
using FS.SDK.Scene.Objects;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.MiniTools;

/// <summary>Production adapter for the selected-object and transform APIs of FEE.</summary>
public sealed class FeeSdkSelectionTransformGateway : IFeeSelectionTransformGateway
{
    public async Task<IReadOnlyList<FeeSelectedObject>> GetSelectedObjectsAsync(
        CancellationToken cancellationToken = default)
    {
        if (Services.ApiInstance is null)
            throw new InvalidOperationException("Die FEE-SDK ist nicht verfügbar.");

        cancellationToken.ThrowIfCancellationRequested();
        var selectedIds = (await Services.ApiInstance.Object.GetAllSelectedObjectsAsync())
            .Distinct()
            .ToArray();
        if (selectedIds.Length == 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();
        var surfaceIds = (await Services.ApiInstance.Object
                .GetSceneObjectGuidsOfTypeAsync(nameof(Surface)))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var cachedObjects = (Services.FeeObjects?.AllFeeObjects ?? [])
            .GroupBy(item => item.Guid)
            .ToDictionary(group => group.Key, group => group.First());

        IReadOnlyList<string> names;
        try
        {
            names = (await Services.ApiInstance.Object
                    .GetPropertyAsync(selectedIds, nameof(SceneObject.Name)))
                .ToArray();
        }
        catch
        {
            // The positioning operation itself must remain usable if an older
            // SDK cannot batch-read names. The local scene cache is preferred.
            names = [];
        }

        return selectedIds.Select((id, index) =>
        {
            cachedObjects.TryGetValue(id, out var cached);
            var name = index < names.Count && !string.IsNullOrWhiteSpace(names[index])
                ? names[index]
                : !string.IsNullOrWhiteSpace(cached?.Name)
                    ? cached.Name
                    : $"Ausgewähltes Objekt {index + 1}";
            return new FeeSelectedObject(
                id,
                name,
                surfaceIds.Contains(id) || cached is FeeSurface);
        }).ToArray();
    }

    public async Task<bool> SetPositionAsync(
        Guid objectId,
        MiniToolsVector position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Services.ApiInstance.Object.SetPropertyAsync(
            objectId,
            nameof(SceneObject.Transform.Position),
            ToFeeVector(position),
            nameof(SceneObject.Transform));
    }

    public async Task<bool> SetSurfaceScaleAsync(
        Guid objectId,
        MiniToolsVector scale,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Services.ApiInstance.Object.SetPropertyAsync(
            objectId,
            nameof(SceneObject.Transform.LocalScale),
            ToFeeVector(scale),
            nameof(SceneObject.Transform));
    }

    private static Vector3 ToFeeVector(MiniToolsVector vector) =>
        new(vector.X, vector.Y, vector.Z);
}

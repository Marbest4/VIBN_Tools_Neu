using FS.SDK;
using FS.SDK.Scene.Objects;
using System.Xml.Linq;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFeeVisual;

internal sealed record VisualFeeDiscoveryResult(
    IReadOnlyList<VisualFeeObject> Objects,
    IReadOnlyDictionary<string, FeeAbstractObject> RuntimeObjects,
    IReadOnlyList<VisualFeeContainerObject> ContainerObjects);

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

        var logicsTask = ExistingSignalLinkAdapter.ReadExistingLogicsAsync(cancellationToken);
        var cabinetElementsTask = ExistingSignalLinkAdapter.ReadExistingCabinetElementsAsync(cancellationToken);
        var cabinetsTask = ReadNamedObjectsAsync("Cabinet", cancellationToken);
        await Task.WhenAll(logicsTask, cabinetElementsTask, cabinetsTask);

        var uniqueRuntimeObjects = runtimeObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.GuidString))
            .GroupBy(item => item.GuidString, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FeeType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await ReadCurrentSlotsAsync(uniqueRuntimeObjects, cancellationToken);

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

        var containerObjects = (await logicsTask)
            .Select(item => new VisualFeeContainerObject(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                VisualFeeContainerObjectKind.Logic,
                item.LogicDefinitionName ?? string.Empty))
            .Concat((await cabinetElementsTask).Select(item => new VisualFeeContainerObject(
                item.Guid.ToString("D"),
                item.Name ?? string.Empty,
                VisualFeeContainerObjectKind.CabinetElement,
                item.ElementType ?? string.Empty)))
            .Concat((await cabinetsTask).Select(item => new VisualFeeContainerObject(
                item.GuidString,
                item.Name,
                VisualFeeContainerObjectKind.Cabinet,
                string.Empty)))
            .ToArray();

        logger.Information(
            $"{objects.Count} zuweisbare FEE-SimObjects und {containerObjects.Length} vorhandene Logik-/Cabinet-Objekte gelesen.");
        return new VisualFeeDiscoveryResult(objects, byId, containerObjects);
    }

    internal static string CreateFeeObjectId(string guidString) =>
        $"fee:{guidString.Trim().ToLowerInvariant()}";

    private async Task ReadCurrentSlotsAsync(
        IReadOnlyList<FeeAbstractObject> objects,
        CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var chunk in objects.Chunk(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] xmlTexts;
            try
            {
                var guidTexts = chunk.Select(item => item.GuidString).ToArray();
                xmlTexts = (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(guidTexts)).ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed += chunk.Length;
                logger.Warning(
                    $"Der aktuelle Slotbestand eines FEE-SimObject-Blocks konnte nicht gelesen werden: {exception.Message}");
                continue;
            }
            for (var index = 0; index < Math.Min(chunk.Length, xmlTexts.Length); index++)
            {
                try
                {
                    chunk[index].Slots = ParseSlotAssignments(XElement.Parse(xmlTexts[index]));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                }
            }
            failed += Math.Abs(chunk.Length - xmlTexts.Length);
        }
        if (failed > 0)
            logger.Warning($"Für {failed} FEE-SimObject(s) konnte der aktuelle Slotbestand nicht gelesen werden.");
    }

    internal static Dictionary<string, Guid> ParseSlotAssignments(XElement xml)
    {
        var slots = xml.Element("Slots") ?? xml.Element("IOSlots") ??
                    xml.Descendants("Slots").FirstOrDefault() ??
                    xml.Descendants("IOSlots").FirstOrDefault();
        if (slots is null)
            return new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        return slots.Descendants("Assignment")
            .Select(item => new
            {
                Name = item.Element("SlotName")?.Value?.Trim() ?? string.Empty,
                Guid = Guid.TryParse(item.Element("AssignedGuid")?.Value, out var guid) ? guid : Guid.Empty,
            })
            .Where(item => item.Name.Length > 0 && item.Guid != Guid.Empty)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Guid, StringComparer.OrdinalIgnoreCase);
    }

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

    private static async Task<IReadOnlyList<NamedFeeObject>> ReadNamedObjectsAsync(
        string objectType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var guidStrings = (await Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(objectType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (guidStrings.Length == 0)
            return [];
        var names = (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, nameof(SceneObject.Name)))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return guidStrings.Select((guid, index) => new NamedFeeObject(
            guid,
            Services.ApiInstance.XmlHelper.ConvertToString(names[index]))).ToArray();
    }

    private sealed record NamedFeeObject(string GuidString, string Name);
}

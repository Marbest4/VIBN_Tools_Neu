using FS.SDK.Components;

namespace VIBN_Tools.GlobalClasses;

/// <summary>
/// Reads and writes FEE's TagComponent property collection. In the FEE UI
/// every dictionary entry is displayed as one property with Name and Value;
/// keeping this SDK detail in one place prevents provenance writers from
/// accidentally falling back to MarkComponent or an object-name encoding.
/// </summary>
public static class FeeTagPropertyStore
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(Guid objectGuid)
    {
        var xml = await Services.ApiInstance.Object.GetPropertyAsync(
            objectGuid,
            nameof(TagComponent.TagEntries),
            nameof(TagComponent));
        return Services.ApiInstance.XmlHelper.ConvertToDictionaryStringString(xml);
    }

    public static async Task WriteAndVerifyAsync(
        Guid objectGuid,
        IReadOnlyDictionary<string, string> properties,
        bool preserveExisting = true,
        bool verifyAfterWrite = true)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (preserveExisting)
        {
            try
            {
                foreach (var item in await ReadAsync(objectGuid))
                    merged[item.Key] = item.Value;
            }
            catch
            {
                // A newly created scene object may expose TagComponent only
                // after its first write.
            }
        }

        foreach (var item in properties)
            merged[item.Key] = item.Value;

        await Services.ApiInstance.Object.SetPropertyAsync(
            objectGuid,
            nameof(TagComponent.TagEntries),
            merged,
            nameof(TagComponent));

        if (verifyAfterWrite)
            await VerifyAsync(objectGuid, properties);
    }

    public static async Task VerifyAsync(
        Guid objectGuid,
        IReadOnlyDictionary<string, string> expectedProperties)
    {
        var written = await ReadAsync(objectGuid);
        var missing = expectedProperties.FirstOrDefault(item =>
            !written.TryGetValue(item.Key, out var value) ||
            !string.Equals(value, item.Value, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(missing.Key))
        {
            throw new InvalidOperationException(
                $"FEE TagComponent hat die Property '{missing.Key}' nicht mit dem erwarteten Wert bestätigt.");
        }
    }
}

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.ContainerToFeeVisual;

public sealed record FeeContainerSignalSource(
    string Id,
    string Signal,
    string Address,
    string DataType,
    Guid VariableGuid);

public sealed record FeeContainerSignalBinding(
    int ContainerIndex,
    int EntryIndex,
    Guid VariableGuid);

/// <summary>
/// Versioned provenance stored on the generated FEE BasicFrame. The selected
/// XML preserves canonical container semantics; signal bindings allow a later
/// overlay of current FEE variable properties.
/// </summary>
public sealed record FeeContainerProvenanceSnapshot(
    IReadOnlyDictionary<string, string> Tags,
    XDocument ContainerDocument,
    IReadOnlyList<FeeContainerSignalBinding> SignalBindings,
    int ContainerCount,
    int SignalCount,
    string SourceFingerprint);

public static class FeeContainerProvenanceCodec
{
    public const string SchemaKey = "vibn.container2fee.schema";
    public const string FormatKey = "vibn.container2fee.format";
    public const string HashKey = "vibn.container2fee.sha256";
    public const string SourceHashKey = "vibn.container2fee.source-sha256";
    public const string PartCountKey = "vibn.container2fee.part-count";
    public const string PartPrefix = "vibn.container2fee.part.";
    public const string BindingHashKey = "vibn.container2fee.bindings-sha256";
    public const string BindingPartCountKey = "vibn.container2fee.bindings-part-count";
    public const string BindingPartPrefix = "vibn.container2fee.bindings-part.";
    public const string CurrentSchema = "2";
    public const string CurrentFormat = "gzip-base64-utf8-xml";

    private const int ChunkLength = 3000;
    private const int MaximumParts = 20_000;
    private const int MaximumUncompressedBytes = 50 * 1024 * 1024;

    public static FeeContainerProvenanceSnapshot Create(
        XDocument source,
        IReadOnlySet<string> includedContainerIds,
        string sourceFingerprint,
        IReadOnlyDictionary<string, IReadOnlyList<FeeContainerSignalSource>>? signalsByContainer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(includedContainerIds);

        var projected = new XDocument(source);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var bindings = new List<FeeContainerSignalBinding>();
        var projectedContainerIndex = 0;
        foreach (var container in projected.Descendants()
                     .Where(element => element.Name.LocalName == "Container").ToArray())
        {
            var sourceId = container.Attribute("id")?.Value ?? string.Empty;
            var component = ChildValue(container, "Component");
            var type = ChildValue(container, "Type");
            var identity = $"{sourceId}\u001f{component}\u001f{type}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = ++occurrence;
            var containerId = ContainerXmlVisualPlanParser.CreateContainerId(
                sourceId, component, type, occurrence);
            if (!includedContainerIds.Contains(containerId))
            {
                container.Remove();
                continue;
            }

            if (signalsByContainer?.TryGetValue(containerId, out var sources) == true)
                BindSignals(container, projectedContainerIndex, sources, bindings);
            projectedContainerIndex++;
        }

        var xmlBytes = Encoding.UTF8.GetBytes(projected.ToString(SaveOptions.DisableFormatting));
        var bindingBytes = JsonSerializer.SerializeToUtf8Bytes(bindings);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchemaKey] = CurrentSchema,
            [FormatKey] = CurrentFormat,
            [HashKey] = Hash(xmlBytes),
            [BindingHashKey] = Hash(bindingBytes),
            [SourceHashKey] = sourceFingerprint ?? string.Empty,
        };
        AddPayload(tags, PartCountKey, PartPrefix, xmlBytes);
        AddPayload(tags, BindingPartCountKey, BindingPartPrefix, bindingBytes);
        return BuildSnapshot(tags, projected, bindings, sourceFingerprint ?? string.Empty);
    }

    public static bool TryRead(
        IReadOnlyDictionary<string, string>? tags,
        out FeeContainerProvenanceSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;
        if (tags is null || !tags.TryGetValue(SchemaKey, out var schema))
            return Fail("Der FEE-Root besitzt keine Container2FEE-Provenienz.", out error);
        if (schema is not "1" and not CurrentSchema)
            return Fail($"Die Container2FEE-Provenienzversion '{schema}' wird nicht unterstützt.", out error);
        if (!tags.TryGetValue(FormatKey, out var format) ||
            !string.Equals(format, CurrentFormat, StringComparison.Ordinal))
        {
            return Fail("Das Format der Container2FEE-Provenienz wird nicht unterstützt.", out error);
        }

        try
        {
            if (!TryReadPayload(tags, PartCountKey, PartPrefix, out var xmlBytes, out error) ||
                !VerifyHash(tags, HashKey, xmlBytes, out error))
                return false;

            IReadOnlyList<FeeContainerSignalBinding> bindings = [];
            if (schema == CurrentSchema)
            {
                if (!TryReadPayload(tags, BindingPartCountKey, BindingPartPrefix, out var bindingBytes, out error) ||
                    !VerifyHash(tags, BindingHashKey, bindingBytes, out error))
                    return false;
                bindings = JsonSerializer.Deserialize<List<FeeContainerSignalBinding>>(bindingBytes) ?? [];
            }

            using var stream = new MemoryStream(xmlBytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumUncompressedBytes,
            });
            var document = XDocument.Load(reader);
            if (!ValidateBindings(document, bindings, out error))
                return false;
            var sourceHash = tags.TryGetValue(SourceHashKey, out var value) ? value : string.Empty;
            snapshot = BuildSnapshot(tags, document, bindings, sourceHash);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidDataException or IOException or XmlException or JsonException)
        {
            error = $"Die Container2FEE-Provenienz ist beschädigt: {exception.Message}";
            return false;
        }
    }

    public static void SaveAtomically(FeeContainerProvenanceSnapshot snapshot, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Der Exportpfad besitzt kein Verzeichnis.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            snapshot.ContainerDocument.Save(temporaryPath, SaveOptions.None);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void BindSignals(
        XElement container,
        int containerIndex,
        IReadOnlyList<FeeContainerSignalSource> sources,
        ICollection<FeeContainerSignalBinding> bindings)
    {
        var used = new bool[sources.Count];
        var entries = container.Descendants()
            .Where(element => element.Name.LocalName == "Entry").ToArray();
        for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
        {
            var match = -1;
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                if (!used[sourceIndex] && IsSameSignal(entries[entryIndex], sources[sourceIndex]))
                {
                    match = sourceIndex;
                    break;
                }
            }
            if (match < 0)
                continue;
            used[match] = true;
            bindings.Add(new FeeContainerSignalBinding(
                containerIndex, entryIndex, sources[match].VariableGuid));
        }
    }

    private static bool IsSameSignal(XElement entry, FeeContainerSignalSource source) =>
        Equal(ChildValue(entry, "ID"), source.Id) &&
        Equal(ChildValue(entry, "Signal"), source.Signal) &&
        Equal(ChildValue(entry, "Address"), source.Address) &&
        Equal(ChildValue(entry, "DataType"), source.DataType);

    private static bool Equal(string left, string right) =>
        string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void AddPayload(
        IDictionary<string, string> tags,
        string countKey,
        string partPrefix,
        byte[] plainBytes)
    {
        var encoded = Convert.ToBase64String(Compress(plainBytes));
        var partCount = Math.Max(1, (encoded.Length + ChunkLength - 1) / ChunkLength);
        tags[countKey] = partCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < partCount; index++)
        {
            var offset = index * ChunkLength;
            tags[$"{partPrefix}{index:D5}"] = encoded.Substring(
                offset, Math.Min(ChunkLength, encoded.Length - offset));
        }
    }

    private static bool TryReadPayload(
        IReadOnlyDictionary<string, string> tags,
        string countKey,
        string partPrefix,
        out byte[] value,
        out string error)
    {
        value = [];
        error = string.Empty;
        if (!tags.TryGetValue(countKey, out var countText) ||
            !int.TryParse(countText, out var partCount) ||
            partCount is < 1 or > MaximumParts)
        {
            return Fail("Die Container2FEE-Provenienz enthält eine ungültige Teileanzahl.", out error);
        }

        var encoded = new StringBuilder(partCount * ChunkLength);
        for (var index = 0; index < partCount; index++)
        {
            if (!tags.TryGetValue($"{partPrefix}{index:D5}", out var part))
                return Fail($"Teil {index + 1} der Container2FEE-Provenienz fehlt.", out error);
            encoded.Append(part);
        }
        value = Decompress(Convert.FromBase64String(encoded.ToString()));
        return true;
    }

    private static bool VerifyHash(
        IReadOnlyDictionary<string, string> tags,
        string key,
        byte[] value,
        out string error)
    {
        if (!tags.TryGetValue(key, out var expectedHash) ||
            !string.Equals(expectedHash, Hash(value), StringComparison.OrdinalIgnoreCase))
            return Fail("Die Prüfsumme der Container2FEE-Provenienz ist ungültig.", out error);
        error = string.Empty;
        return true;
    }

    private static bool ValidateBindings(
        XDocument document,
        IReadOnlyList<FeeContainerSignalBinding> bindings,
        out string error)
    {
        var containers = document.Descendants()
            .Where(element => element.Name.LocalName == "Container").ToArray();
        foreach (var binding in bindings)
        {
            if (binding.VariableGuid == Guid.Empty || binding.ContainerIndex < 0 ||
                binding.ContainerIndex >= containers.Length)
            {
                return Fail("Die Container2FEE-Provenienz enthält eine ungültige Signalzuordnung.", out error);
            }
            var entryCount = containers[binding.ContainerIndex].Descendants()
                .Count(element => element.Name.LocalName == "Entry");
            if (binding.EntryIndex < 0 || binding.EntryIndex >= entryCount)
                return Fail("Die Container2FEE-Provenienz verweist auf einen nicht vorhandenen Eintrag.", out error);
        }
        error = string.Empty;
        return true;
    }

    private static FeeContainerProvenanceSnapshot BuildSnapshot(
        IReadOnlyDictionary<string, string> tags,
        XDocument document,
        IReadOnlyList<FeeContainerSignalBinding> bindings,
        string sourceFingerprint) =>
        new(
            new Dictionary<string, string>(tags, StringComparer.Ordinal),
            new XDocument(document),
            bindings.ToArray(),
            document.Descendants().Count(element => element.Name.LocalName == "Container"),
            document.Descendants().Count(element => element.Name.LocalName == "Entry"),
            sourceFingerprint);

    private static byte[] Compress(byte[] value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(value);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] value)
    {
        using var input = new MemoryStream(value, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaximumUncompressedBytes)
                throw new InvalidDataException("Die entpackte Provenienz überschreitet 50 MB.");
        }
        return output.ToArray();
    }

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value ?? string.Empty;
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.SpecialDevices;

public sealed record FeeSpecialDeviceSignalSnapshot(
    Guid VariableGuid,
    string Tag,
    string Address,
    string Usage,
    string DataType,
    string Comment);

public sealed record FeeSpecialDeviceSnapshot(
    int SchemaVersion,
    string Prefix,
    string Manufacturer,
    string DeviceType,
    string? RobotType,
    int InputByte,
    int OutputByte,
    IReadOnlyList<FeeSpecialDeviceSignalSnapshot> Signals);

/// <summary>
/// Versioned metadata for SpecialDevices2FEE roots. It intentionally records
/// only domain values needed for a deterministic reverse export; SDK object
/// names are not interpreted as a substitute for missing provenance.
/// </summary>
public static class FeeSpecialDeviceProvenanceCodec
{
    public const string SchemaKey = "vibn.specialdevices2fee.schema";
    public const string HashKey = "vibn.specialdevices2fee.sha256";
    public const string PartCountKey = "vibn.specialdevices2fee.part-count";
    public const string PartPrefix = "vibn.specialdevices2fee.part.";
    public const int CurrentSchema = 1;
    private const int ChunkLength = 3000;
    private const int MaximumParts = 1000;

    public static FeeSpecialDeviceSnapshot Create(SpecialDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new FeeSpecialDeviceSnapshot(
            CurrentSchema,
            device.DevicePrefix,
            device.DeviceManufacturer.ToString(),
            device.DeviceType.ToString(),
            device.RobotType?.ToString(),
            device.DeviceAddresses.Input,
            device.DeviceAddresses.Output,
            (device.DeviceSignals ?? Array.Empty<FeeInterfaceSignal>())
                .Select(signal => new FeeSpecialDeviceSignalSnapshot(
                    signal.Guid,
                    signal.Tag ?? string.Empty,
                    signal.Address ?? string.Empty,
                    signal.Usage.ToString(),
                    signal.IOType.ToString(),
                    signal.Comment ?? string.Empty))
                .ToArray());
    }

    public static IReadOnlyDictionary<string, string> Encode(FeeSpecialDeviceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var encoded = Convert.ToBase64String(payload);
        var partCount = Math.Max(1, (encoded.Length + ChunkLength - 1) / ChunkLength);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchemaKey] = CurrentSchema.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [HashKey] = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            [PartCountKey] = partCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        for (var index = 0; index < partCount; index++)
        {
            var offset = index * ChunkLength;
            tags[$"{PartPrefix}{index:D5}"] = encoded.Substring(
                offset,
                Math.Min(ChunkLength, encoded.Length - offset));
        }
        return tags;
    }

    public static bool TryRead(
        IReadOnlyDictionary<string, string>? tags,
        out FeeSpecialDeviceSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;
        if (tags is null || !tags.TryGetValue(SchemaKey, out var schemaText))
            return Fail("Der FEE-Root besitzt keine SpecialDevices2FEE-Provenienz.", out error);
        if (!int.TryParse(schemaText, out var schema) || schema != CurrentSchema)
            return Fail($"Die SpecialDevices2FEE-Provenienzversion '{schemaText}' wird nicht unterstützt.", out error);
        if (!tags.TryGetValue(PartCountKey, out var countText) ||
            !int.TryParse(countText, out var partCount) ||
            partCount is < 1 or > MaximumParts)
        {
            return Fail("Die SpecialDevices2FEE-Provenienz enthält eine ungültige Teileanzahl.", out error);
        }

        try
        {
            var encoded = new StringBuilder(partCount * ChunkLength);
            for (var index = 0; index < partCount; index++)
            {
                if (!tags.TryGetValue($"{PartPrefix}{index:D5}", out var part))
                    return Fail($"Teil {index + 1} der SpecialDevices2FEE-Provenienz fehlt.", out error);
                encoded.Append(part);
            }
            var payload = Convert.FromBase64String(encoded.ToString());
            var actualHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            if (!tags.TryGetValue(HashKey, out var expectedHash) ||
                !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Die Prüfsumme der SpecialDevices2FEE-Provenienz ist ungültig.", out error);
            }

            snapshot = JsonSerializer.Deserialize<FeeSpecialDeviceSnapshot>(payload);
            if (snapshot is null || snapshot.SchemaVersion != CurrentSchema ||
                string.IsNullOrWhiteSpace(snapshot.Prefix) ||
                string.IsNullOrWhiteSpace(snapshot.Manufacturer) ||
                string.IsNullOrWhiteSpace(snapshot.DeviceType))
            {
                snapshot = null;
                return Fail("Die SpecialDevices2FEE-Provenienz ist unvollständig.", out error);
            }
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            snapshot = null;
            return Fail($"Die SpecialDevices2FEE-Provenienz ist beschädigt: {exception.Message}", out error);
        }
    }

    public static void SaveAtomically(FeeSpecialDeviceSnapshot snapshot, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Der Exportpfad besitzt kein Verzeichnis.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static bool TryLoadFile(
        string sourcePath,
        out FeeSpecialDeviceSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;
        try
        {
            snapshot = JsonSerializer.Deserialize<FeeSpecialDeviceSnapshot>(File.ReadAllBytes(sourcePath));
            if (!IsValidSnapshot(snapshot))
            {
                snapshot = null;
                return Fail("Die FEE2SpecialDevices-Datei ist unvollständig oder verwendet eine nicht unterstützte Version.", out error);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            snapshot = null;
            return Fail($"Die FEE2SpecialDevices-Datei konnte nicht gelesen werden: {exception.Message}", out error);
        }
    }

    private static bool IsValidSnapshot(FeeSpecialDeviceSnapshot? snapshot) =>
        snapshot is not null &&
        snapshot.SchemaVersion == CurrentSchema &&
        !string.IsNullOrWhiteSpace(snapshot.Prefix) &&
        !string.IsNullOrWhiteSpace(snapshot.Manufacturer) &&
        !string.IsNullOrWhiteSpace(snapshot.DeviceType) &&
        snapshot.Signals is not null;

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

using System.IO;
using System.Text.Json;

namespace VIBN_Tools.Application;

/// <summary>Persisted user choice for one TIA hardware module/address row.</summary>
public sealed record TiaHardwareMapping(
    string Key,
    bool Include,
    string Prefix,
    int? InputByte,
    int? OutputByte,
    string Manufacturer,
    string DeviceType,
    string RobotType);

public interface ITiaHardwareMappingStore
{
    Task<IReadOnlyDictionary<string, TiaHardwareMapping>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyCollection<TiaHardwareMapping> mappings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Local JSON store for reviewed TIA-to-Special-Device mappings. It contains
/// no project data beyond stable hardware keys, prefixes and logic choices.
/// Writes are serialized and atomically replace the previous document.
/// </summary>
public sealed class JsonTiaHardwareMappingStore : ITiaHardwareMappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonTiaHardwareMappingStore(string filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? throw new ArgumentException("Ein Speicherpfad ist erforderlich.", nameof(filePath))
            : filePath;
    }

    public static JsonTiaHardwareMappingStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GROB",
        "VIBN_Tools",
        "tia-hardware-mappings.json"));

    public async Task<IReadOnlyDictionary<string, TiaHardwareMapping>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, TiaHardwareMapping>(StringComparer.OrdinalIgnoreCase);

            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<MappingDocument>(
                stream,
                JsonOptions,
                cancellationToken) ?? new MappingDocument();

            return document.Mappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Key))
                .GroupBy(mapping => mapping.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<TiaHardwareMapping> mappings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Der Zuordnungspfad enthält kein Verzeichnis.");
            Directory.CreateDirectory(directory);

            var document = new MappingDocument
            {
                Mappings = mappings
                    .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Key))
                    .OrderBy(mapping => mapping.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            var temporaryFile = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryFile,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryFile, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryFile))
                    File.Delete(temporaryFile);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class MappingDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public List<TiaHardwareMapping> Mappings { get; set; } = new();
    }
}

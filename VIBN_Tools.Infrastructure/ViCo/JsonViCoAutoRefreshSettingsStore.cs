using System.Text.Json;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

/// <summary>Stores the ViCo refresh interval atomically in the local user profile.</summary>
public sealed class JsonViCoAutoRefreshSettingsStore : IViCoAutoRefreshSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonViCoAutoRefreshSettingsStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Ein Einstellungen-Pfad ist erforderlich.", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<ViCoAutoRefreshSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return ViCoAutoRefreshSettings.Default;

            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<ViCoAutoRefreshSettings>(
                stream,
                JsonOptions,
                cancellationToken);
            return new ViCoAutoRefreshSettings(
                ViCoAutoRefreshPolicy.Normalize(
                    settings?.IntervalMinutes ?? ViCoAutoRefreshSettings.Default.IntervalMinutes),
                settings?.ShowExtendedInformation ?? ViCoAutoRefreshSettings.Default.ShowExtendedInformation,
                settings?.VisibleColumns?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
        catch (JsonException)
        {
            return ViCoAutoRefreshSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        ViCoAutoRefreshSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var normalized = new ViCoAutoRefreshSettings(
                ViCoAutoRefreshPolicy.Normalize(settings.IntervalMinutes),
                settings.ShowExtendedInformation,
                settings.VisibleColumns?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            var temporaryFile = _filePath + ".tmp";
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
            }
            File.Move(temporaryFile, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

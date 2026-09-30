using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace VIBN_Tools.Core.Diagnostics;

public sealed record PerformanceMeasurement(
    DateTime TimestampUtc,
    string Area,
    string Operation,
    long ElapsedMilliseconds,
    bool Success);

public sealed record PerformanceSummary(
    string Area,
    string Operation,
    int Count,
    double AverageMilliseconds,
    long MaximumMilliseconds,
    long LastMilliseconds,
    int FailureCount);

/// <summary>
/// Opt-in timing recorder for complete user workflows. Measurements are kept
/// in memory and appended as JSONL so performance regressions can be compared
/// across builds without adding a profiler dependency to production systems.
/// </summary>
public sealed class PerformanceMeasurementService
{
    private readonly ConcurrentQueue<PerformanceMeasurement> _measurements = new();
    private readonly object _writeLock = new();
    private readonly string _directory;
    private readonly string _settingsPath;
    private bool _enabled;

    public PerformanceMeasurementService(string? directory = null, bool? enabled = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VIBN_Tools",
            "diagnostics",
            "performance");
        _settingsPath = Path.Combine(_directory, "settings.json");
        _enabled = enabled ?? LoadEnabled();
    }

    public static PerformanceMeasurementService Instance { get; } = new();

    public string LogDirectory => _directory;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { Enabled = value }));
        }
    }

    public PerformanceMeasurementScope Start(string area, string operation) =>
        new(this, area, operation, Enabled);

    public IReadOnlyList<PerformanceSummary> GetSummaries() => _measurements
        .GroupBy(item => new { item.Area, item.Operation })
        .Select(group => new PerformanceSummary(
            group.Key.Area,
            group.Key.Operation,
            group.Count(),
            group.Average(item => item.ElapsedMilliseconds),
            group.Max(item => item.ElapsedMilliseconds),
            group.OrderBy(item => item.TimestampUtc).Last().ElapsedMilliseconds,
            group.Count(item => !item.Success)))
        .OrderByDescending(item => item.AverageMilliseconds)
        .ThenBy(item => item.Area, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void Clear() => _measurements.Clear();

    internal void Record(string area, string operation, long elapsedMilliseconds, bool success)
    {
        var measurement = new PerformanceMeasurement(
            DateTime.UtcNow,
            area,
            operation,
            elapsedMilliseconds,
            success);
        _measurements.Enqueue(measurement);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"performance-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        lock (_writeLock)
            File.AppendAllText(path, JsonSerializer.Serialize(measurement) + Environment.NewLine);
    }

    private bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            return document.RootElement.TryGetProperty("Enabled", out var enabled) && enabled.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}

public sealed class PerformanceMeasurementScope : IDisposable
{
    private readonly PerformanceMeasurementService _service;
    private readonly string _area;
    private readonly string _operation;
    private readonly Stopwatch? _stopwatch;
    private bool _failed;
    private bool _disposed;

    internal PerformanceMeasurementScope(
        PerformanceMeasurementService service,
        string area,
        string operation,
        bool enabled)
    {
        _service = service;
        _area = area;
        _operation = operation;
        _stopwatch = enabled ? Stopwatch.StartNew() : null;
    }

    public void MarkFailed() => _failed = true;

    public void Dispose()
    {
        if (_disposed || _stopwatch is null)
            return;
        _disposed = true;
        _stopwatch.Stop();
        _service.Record(_area, _operation, _stopwatch.ElapsedMilliseconds, !_failed);
    }
}

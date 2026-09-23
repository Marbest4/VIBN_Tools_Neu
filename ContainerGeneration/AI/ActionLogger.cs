using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;

namespace VIBN_Tools.ContainerGeneration.AI;

/// <summary>
/// Protokolliert alle Drag-and-Drop-Aktionen sofort auf Disk.
///
/// SPEICHERORT (GEAENDERT):
///   Vorher: %AppData%\VIBN_Tools\ContainerGeneration\AI\learning\actions\
///   Jetzt:  {ExeDir}\vibn_ai_data\actions\   (via ModelPaths.ActionsDir)
///
/// Jede relevante strukturierte Änderung wird sofort per File.AppendAllText()
/// als JSONL-Zeile gespeichert – kein Puffer, kein Delay.
/// Eine Datei pro Tag: YYYYMMDD.jsonl
///
/// WANN werden Aktionen gespeichert?
///   - Sofort wenn ContainerGeneration eine Benutzeraktion meldet.
///   - Das Modell liest die Logs erst beim naechsten Training oder Check.
///   - Es gibt keinen "Live-Lerneffekt" waehrend der Sitzung –
///     erst nach dem naechsten Train() kennt das Modell die neuen Logs.
/// </summary>
public sealed class ActionLogger
{
    private readonly string _logDir;
    private readonly object _writeLock = new();

    // Fasst Remove→Add Paare innerhalb von 500ms zu einer MOVE-Aktion zusammen
    private readonly ConcurrentDictionary<string, PendingMove> _pending = new();
    private readonly TimeSpan _window = TimeSpan.FromMilliseconds(500);

    public ActionLogger()
    {
        // Zentraler Pfad aus ModelPaths – relativ zur .exe
        _logDir = ModelPaths.ActionsDir;
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>
    /// Optionaler Konstruktor fuer Tests oder abweichende Pfade.
    /// </summary>
    public ActionLogger(string customLogDir)
    {
        _logDir = customLogDir;
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    // ── Aufruf bei REMOVE (Signal wird aus Container gezogen) ─────────
    public void LogRemoved(
        string containerName,
        string componentType,
        ContainerEntry entry,
        string sourceKey = "")
    {
        var signalId = entry.EnsureSignalId();
        _pending[signalId] = new PendingMove(
            DateTime.UtcNow,
            containerName,
            componentType,
            entry.Slot,
            entry.Signal,
            sourceKey);
    }

    // ── Aufruf bei ADD (Signal wird in Container abgelegt) ────────────
    public void LogAdded(string containerName, string componentType, ContainerEntry entry,
        string? ruleSuggestion, string? mlTop1, float? mlScore, string sourceKey = "")
    {
        var now = DateTime.UtcNow;
        var signalId = entry.EnsureSignalId();
        if (_pending.TryRemove(signalId, out var prev) && (now - prev.Timestamp) <= _window)
        {
            // Remove→Add innerhalb 500ms = als MOVE protokollieren
            Write(new UserActionEvent(
                signalId, entry.Signal,
                prev.Container, prev.ComponentType, prev.Slot,
                containerName, componentType, entry.Slot,
                ruleSuggestion ?? "",
                mlTop1, mlScore ?? 0f,
                SchemaVersion: 2,
                TimestampUtc: now,
                ActionType: "Move",
                PropertyName: "ContainerAndSlot",
                PreviousValue: $"{prev.Container}|{prev.ComponentType}|{prev.Slot}",
                NewValue: $"{containerName}|{componentType}|{entry.Slot}",
                SourceKey: string.IsNullOrWhiteSpace(sourceKey) ? prev.SourceKey : sourceKey));
        }
        else
        {
            // Reiner ADD (z.B. aus Unassigned-Liste)
            Write(new UserActionEvent(
                signalId, entry.Signal,
                "", "", "",
                containerName, componentType, entry.Slot,
                ruleSuggestion ?? "",
                mlTop1, mlScore ?? 0f,
                SchemaVersion: 2,
                TimestampUtc: now,
                ActionType: "Add",
                PropertyName: "ContainerAndSlot",
                NewValue: $"{containerName}|{componentType}|{entry.Slot}",
                SourceKey: sourceKey));
        }
    }

    // ── Aufruf bei SLOT-AENDERUNG (Dropdown-Auswahl im Container) ────
    public void LogSlotChange(string containerName, string componentType, ContainerEntry entry,
        string oldSlot, string? mlTop1, float? mlScore, string sourceKey = "")
        => Write(new UserActionEvent(
            entry.EnsureSignalId(), entry.Signal,
            containerName, componentType, oldSlot,
            containerName, componentType, entry.Slot,
            "", mlTop1, mlScore ?? 0f,
            SchemaVersion: 2,
            TimestampUtc: DateTime.UtcNow,
            ActionType: "PropertyChange",
            PropertyName: nameof(ContainerEntry.Slot),
            PreviousValue: oldSlot,
            NewValue: entry.Slot,
            SourceKey: sourceKey));

    public void LogPropertyChange(
        string containerName,
        string componentType,
        ContainerEntry entry,
        string propertyName,
        object? previousValue,
        object? newValue,
        string sourceKey)
        => Write(new UserActionEvent(
            entry.EnsureSignalId(), entry.Signal,
            containerName, componentType, entry.Slot,
            containerName, componentType, entry.Slot,
            "", null, 0f,
            SchemaVersion: 2,
            TimestampUtc: DateTime.UtcNow,
            ActionType: "PropertyChange",
            PropertyName: propertyName,
            PreviousValue: previousValue?.ToString() ?? string.Empty,
            NewValue: newValue?.ToString() ?? string.Empty,
            SourceKey: sourceKey ?? string.Empty));

    public void LogContainerPropertyChange(
        string containerName,
        string componentType,
        string propertyName,
        object? previousValue,
        object? newValue,
        string sourceKey)
        => Write(new UserActionEvent(
            string.Empty, string.Empty,
            containerName, componentType, string.Empty,
            containerName, componentType, string.Empty,
            string.Empty, null, 0f,
            SchemaVersion: 2,
            TimestampUtc: DateTime.UtcNow,
            ActionType: "PropertyChange",
            PropertyName: propertyName,
            PreviousValue: previousValue?.ToString() ?? string.Empty,
            NewValue: newValue?.ToString() ?? string.Empty,
            SourceKey: sourceKey ?? string.Empty));

    // ── Sofortige Disk-Schreibung ─────────────────────────────────────
    private void Write(UserActionEvent evt)
    {
        var file = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
        lock (_writeLock)
            File.AppendAllText(file, JsonSerializer.Serialize(evt) + Environment.NewLine);
    }

    private record PendingMove(
        DateTime Timestamp,
        string Container,
        string ComponentType,
        string Slot,
        string SignalText,
        string SourceKey);
}

/// <summary>
/// Einzelne protokollierte Aktion. Wird als JSON-Zeile gespeichert.
/// </summary>
public record UserActionEvent(
    string SignalId,
    string SignalText,
    string FromContainer,
    string FromComponentType,
    string FromSlot,
    string ToContainer,
    string ComponentType,
    string ToSlot,
    string RuleSuggestion,
    string? MlTop1,
    float MlTop1Score,
    int SchemaVersion = 1,
    DateTime TimestampUtc = default,
    string ActionType = "Legacy",
    string PropertyName = "Slot",
    string PreviousValue = "",
    string NewValue = "",
    string SourceKey = ""
);

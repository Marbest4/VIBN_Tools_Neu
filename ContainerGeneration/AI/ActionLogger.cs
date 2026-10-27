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
/// Einzeländerungen werden sofort gespeichert. Eine abgeschlossene Arbeitsaktion
/// schreibt ihre JSONL-Zeilen gemeinsam; abgebrochene Aktionen werden verworfen.
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
    private readonly List<UserActionEvent> _batchEvents = [];
    private int _batchDepth;
    private bool _batchAborted;
    public Exception? LastWriteError { get; private set; }

    // Fasst Remove→Add Paare innerhalb von 500ms zu einer MOVE-Aktion zusammen
    private readonly ConcurrentDictionary<string, PendingMove> _pending = new();
    private readonly TimeSpan _window = TimeSpan.FromMilliseconds(500);

    public ActionLogger()
    {
        // Zentraler Pfad aus ModelPaths – relativ zur .exe
        _logDir = ModelPaths.ActionsDir;
    }

    /// <summary>
    /// Optionaler Konstruktor fuer Tests oder abweichende Pfade.
    /// </summary>
    public ActionLogger(string customLogDir)
    {
        _logDir = customLogDir;
    }

    public string LogDirectory => _logDir;

    public ActionBatch BeginBatch()
    {
        lock (_writeLock)
        {
            if (_batchDepth++ == 0)
            {
                _batchEvents.Clear(); _pending.Clear(); _batchAborted = false;
                LastWriteError = null;
            }
        }
        return new ActionBatch(this);
    }

    public sealed class ActionBatch(ActionLogger owner) : IDisposable
    {
        private ActionLogger? _owner = owner;
        private bool _completed;
        public void Complete() => _completed = true;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._writeLock)
            {
                current._batchAborted |= !_completed;
                if (--current._batchDepth > 0) return;
                if (!current._batchAborted && current._batchEvents.Count > 0)
                    current.WriteEvents(current._batchEvents);
                current._batchEvents.Clear(); current._pending.Clear();
            }
        }
    }

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
        if (_pending.TryRemove(signalId, out var prev) && (_batchDepth > 0 || (now - prev.Timestamp) <= _window))
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
        lock (_writeLock)
        {
            if (_batchDepth > 0) _batchEvents.Add(evt);
            else WriteEvents([evt]);
        }
    }

    private void WriteEvents(IEnumerable<UserActionEvent> events)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            var file = Path.Combine(_logDir, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");
            File.AppendAllText(file, string.Join(Environment.NewLine, events.Select(item => JsonSerializer.Serialize(item))) + Environment.NewLine);
            LastWriteError = null;
        }
        catch (Exception exception) when (Models.ContainerGenerationExceptionPolicy.IsRecoverable(exception))
        {
            LastWriteError = exception;
            NLog.LogManager.GetCurrentClassLogger().Warn(exception, "ContainerGeneration action log could not be written.");
        }
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

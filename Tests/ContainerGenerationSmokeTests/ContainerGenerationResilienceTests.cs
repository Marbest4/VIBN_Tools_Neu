using System.Collections.ObjectModel;
using System.Text.Json;
using VIBN_Tools.ContainerGeneration.AI;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.Models;

namespace VIBN_Tools.ContainerGeneration.SmokeTests;

internal static class ContainerGenerationResilienceTests
{
    public static void Verify()
    {
        VerifyBatchMoves();
        VerifyAcknowledgment();
        VerifyActionLogging();
        if (!ContainerGenerationExceptionPolicy.IsRecoverable(new IOException("read")) ||
            ContainerGenerationExceptionPolicy.IsRecoverable(new AggregateException(new OutOfMemoryException())))
            throw new InvalidOperationException("Fatal failures and recoverable operations are not distinguished.");
    }

    private static void VerifyBatchMoves()
    {
        var containers = new ObservableCollection<ContainerData>();
        for (var index = 0; index < 10; index++)
        {
            var source = new CountedContainer { Component = "Source" + index };
            using (source.DeferUpdates())
                for (var signal = 0; signal < 100; signal++) source.DataList.Add(Entry($"S{index}_{signal}"));
            source.ValidationPasses = 0;
            containers.Add(source);
        }
        var sources = containers.Cast<CountedContainer>().ToArray();
        var selected = sources.SelectMany(container => container.DataList).ToArray();
        var target = new CountedContainer { Component = "Target" };
        var duplicate = selected[0].Clone();
        var separate = selected[0].Clone(); separate.SignalId = "separate-identity";
        var unassigned = new ObservableCollection<ContainerEntry> { duplicate };
        var filtered = new ObservableCollection<ContainerEntry> { separate };
        GenerationWorkspaceEditor.MoveToContainerBatch(selected, target, containers, unassigned, filtered);
        if (containers.Count != 1 || !ReferenceEquals(containers[0], target) || target.DataList.Count != 1000 ||
            unassigned.Count != 0 || filtered.Count != 1 || !ReferenceEquals(filtered[0], separate) ||
            target.DataList.Select(entry => entry.SignalId).Distinct().Count() != 1000 || !target.IsValid ||
            sources.Any(source => source.ValidationPasses > 1) || target.ValidationPasses > 1)
            throw new InvalidOperationException("Batch movement lost identities, left duplicate locations, or validated once per signal.");
        GenerationWorkspaceEditor.MoveToFilteredBatch(selected.Take(500), containers, unassigned, filtered);
        if (target.DataList.Count != 500 || filtered.Count != 501 || selected.Take(500).Any(entry => entry.Slot != ""))
            throw new InvalidOperationException("Moving a batch to the filtered list failed.");
        GenerationWorkspaceEditor.MoveToUnassignedBatch([selected[0]], containers, unassigned, filtered);
        if (unassigned.Count != 1 || filtered.Contains(selected[0]))
            throw new InvalidOperationException("A batch signal exists in two open lists.");
    }

    private static void VerifyAcknowledgment()
    {
        var entry = Entry("Changed"); entry.ReviewState = ContainerEntryReviewState.SourceChanged;
        var container = new ContainerData(); container.DataList.Add(entry);
        entry.IsChangeAcknowledged = true;
        if (container.HasDetectedChanges || container.RequiresReview || !container.HasAcknowledgedChanges ||
            entry.ReviewState != ContainerEntryReviewState.SourceChanged || !entry.Clone().IsChangeAcknowledged)
            throw new InvalidOperationException("Acknowledgment did not neutralize the marker while retaining its history.");
        var undo = WorkspaceUndoState.Capture("confirm", [container], [], []);
        if (!undo.Containers.Single().DataList.Single().IsChangeAcknowledged)
            throw new InvalidOperationException("Undo snapshots lost acknowledged changes.");
        var path = Path.Combine(Path.GetTempPath(), "vibn-confirmed-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var saved = new SavedData { FilePath = path, ContainerList = [container] };
            saved.CaptureEntryStates(); saved.SetSettings();
            if (!SavedData.DeserializeProject(path).ContainerList.Single().DataList.Single().IsChangeAcknowledged)
                throw new InvalidOperationException("Saving/reloading lost the confirmation state.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        entry.Address = "%Q0.0"; entry.IsChangeAcknowledged = true;
        if (!container.HasWarnings || !container.RequiresReview)
            throw new InvalidOperationException("Acknowledgment suppressed a signal assignment warning.");
        entry.ValidationError = "Invalid slot";
        if (!container.HasErrors) throw new InvalidOperationException("Acknowledgment suppressed validation errors.");
        entry.Signal = "New edit";
        if (entry.IsChangeAcknowledged || !entry.HasUnconfirmedChange)
            throw new InvalidOperationException("Editing a confirmed signal did not reopen its change marker.");
        container.DataList.Clear(); entry.Signal = "Detached";
        if (container.HasDetectedChanges) throw new InvalidOperationException("Reset left removed signal subscriptions attached.");
    }

    private static void VerifyActionLogging()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibn-action-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new ActionLogger(Path.Combine(root, "logs"));
            using (var batch = logger.BeginBatch())
            {
                for (var index = 0; index < 50; index++)
                {
                    var entry = Entry("Log" + index);
                    logger.LogRemoved("Old", "Sensor", entry);
                    logger.LogAdded("New", "Sensor", entry, entry.Slot, null, null);
                }
                if (Directory.Exists(logger.LogDirectory)) throw new InvalidOperationException("A pending batch wrote individual files.");
                batch.Complete();
            }
            var file = Directory.GetFiles(logger.LogDirectory, "*.jsonl").Single();
            var events = File.ReadLines(file).Select(line => JsonSerializer.Deserialize<UserActionEvent>(line)!).ToArray();
            if (events.Length != 50 || events.Any(item => item.ActionType != "Move"))
                throw new InvalidOperationException("Batched action logs lost move events.");
            using (logger.BeginBatch()) logger.LogAdded("Aborted", "Sensor", Entry("aborted"), "", null, null);
            if (File.ReadLines(file).Count() != 50) throw new InvalidOperationException("An aborted workspace batch was logged as completed.");
            var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "file");
            var unavailable = new ActionLogger(blocked);
            unavailable.LogAdded("Target", "Sensor", Entry("blocked"), "", null, null);
            if (unavailable.LastWriteError is null) throw new InvalidOperationException("A failed action log was silently reported as written.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ContainerEntry Entry(string name) => new()
    { SignalId = "ID-" + name, ID = name, Signal = name, Address = "%I0.0", Slot = "PLC_IN_Test", DataType = "Bool" };

    private sealed class CountedContainer : ContainerData
    {
        public int ValidationPasses { get; set; }
        protected override void NotifyOfPropertyChange(string propertyName)
        {
            if (propertyName == nameof(ValidationError)) ValidationPasses++;
            base.NotifyOfPropertyChange(propertyName);
        }
    }
}

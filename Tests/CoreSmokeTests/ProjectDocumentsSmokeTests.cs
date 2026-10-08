using VIBN_Tools.Application.VM;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.Infrastructure.ViCo;

internal static class ProjectDocumentsSmokeTests
{
    public static async Task VerifyAsync(string root)
    {
        var simulation = Path.Combine(root, "documents", "First");
        var folder = Path.Combine(simulation, "00_Documents");
        Directory.CreateDirectory(Path.Combine(folder, "Nested"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Readme.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(folder, "Nested", "Drawing.pdf"), "pdf");
        var service = new FileSystemProjectDocumentsService();
        var result = await service.ReadAsync(simulation);
        Assert(result.Status == ViCoProjectDocumentsStatus.Available && result.FolderPath == folder &&
            result.Files.SequenceEqual(new[] { Path.Combine("Nested", "Drawing.pdf"), "Readme.txt" }),
            "Documents did not use the simulation project path or preserve nested relative filenames.");
        var empty = Path.Combine(root, "documents", "Empty"); Directory.CreateDirectory(Path.Combine(empty, "00_Documents"));
        Assert((await service.ReadAsync(empty)).Status == ViCoProjectDocumentsStatus.Empty, "An empty folder was reported as missing.");
        Assert((await service.ReadAsync(Path.Combine(root, "documents", "Missing"))).Status == ViCoProjectDocumentsStatus.Missing, "A missing folder failed silently.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ReadAsync(simulation, cancelled.Token); throw new InvalidOperationException("Cancelled document scanning ran."); }
        catch (OperationCanceledException) { }

        var scanner = new ControlledDocuments();
        var preferences = new DocumentsPreferences();
        var workstation = new ViCoWorkstation("PC1", "PC1", "", "", "", "", ["First", "Second"], []);
        using var vm = new ViCoSearchPageVM(new SnapshotCatalog(workstation), new ViCoWorkstationSearch(),
            _ => Task.FromResult<IViCoRelatedPathResolver>(new DocumentsResolver(simulation)),
            new OfflineNetworkAvailabilityService(), new NullRemoteDesktopService(), new NullRemoteSessionService(),
            new NullExternalPathLauncher(), new DisabledOnlineRefreshService(), new NullWorkstationConfigurationService(),
            preferences, new JsonViCoLastActiveSnapshotStore(Path.Combine(root, "documents", "snapshot.json")),
            new ViCoWorkspaceContext(), _ => { }, documentsService: scanner);
        await vm.InitializeAsync();
        Assert(vm.DocumentsColumn.IsVisible && !vm.WorkingColumn.IsVisible,
            "Migrating the documents column hid it or discarded existing column preferences.");
        await scanner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.SelectedProject = "Second";
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (vm.Results.Single().DocumentsSummary != "new.pdf" && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert(vm.Results.Single().DocumentsSummary == "new.pdf", "Switching the selected project did not update its documents.");
        scanner.FirstCompleted.SetResult(new ViCoProjectDocumentsResult(folder, ["old.pdf"], ViCoProjectDocumentsStatus.Available));
        await Task.Delay(50);
        Assert(vm.Results.Single().DocumentsSummary == "new.pdf" && scanner.LastPath.EndsWith("Second", StringComparison.Ordinal),
            "A stale folder scan overwrote the newly selected project or used a different Simulation path.");
        vm.DocumentsColumn.IsVisible = false;
        Assert(preferences.Settings.ColumnLayoutVersion == 1 && preferences.Settings.VisibleColumns?.Contains("documents") == false,
            "An explicit documents visibility choice was not persisted with the layout version.");
        vm.DocumentsColumn.IsVisible = true;
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class DocumentsPreferences : IViCoAutoRefreshSettingsStore
    {
        public ViCoAutoRefreshSettings Settings { get; private set; } = new(60, VisibleColumns: ["pc"]);
        public Task<ViCoAutoRefreshSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(ViCoAutoRefreshSettings settings, CancellationToken cancellationToken = default)
        { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class DocumentsResolver(string first) : IViCoRelatedPathResolver
    {
        public string? Resolve(ViCoWorkstation workstation, string project, ViCoRelatedPathKind kind) =>
            kind == ViCoRelatedPathKind.Simulation ? project == "First" ? first : Path.Combine(Path.GetDirectoryName(first)!, "Second") : null;
    }

    private sealed class ControlledDocuments : IViCoProjectDocumentsService
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ViCoProjectDocumentsResult> FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastPath { get; private set; } = "";
        public Task<ViCoProjectDocumentsResult> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            LastPath = path;
            if (path.EndsWith("First", StringComparison.Ordinal)) { FirstStarted.TrySetResult(); return FirstCompleted.Task; }
            return Task.FromResult(new ViCoProjectDocumentsResult(Path.Combine(path, "00_Documents"), ["new.pdf"], ViCoProjectDocumentsStatus.Available));
        }
    }
}

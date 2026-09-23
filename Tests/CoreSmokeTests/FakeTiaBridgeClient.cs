using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;

internal sealed class FakeTiaBridgeClient : ITiaBridgeClient
{
    public bool IsConnected => true;

    public TiaProjectTree Blocks { get; } = new();

    public TiaProjectTree DataTypes { get; } = new();

    public List<(string Folder, string File)> ImportedBlocks { get; } = new();

    public List<(string Folder, string File)> ImportedDataTypes { get; } = new();

    public bool Saved { get; private set; }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task SelectVersionAsync(string version, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AttachAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<TiaPlcInfo>> ListPlcsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TiaPlcInfo>>(Array.Empty<TiaPlcInfo>());

    public Task SelectPlcAsync(int plcIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public List<TiaHardwareModuleInfo> HardwareModules { get; } = new();

    public List<TiaAxisInfo> AxisItems { get; } =
    [
        new TiaAxisInfo { Id = "Technology/AxisX", Name = "AxisX", TechnologyType = "PositioningAxis", GroupPath = "Technology" }
    ];

    public IReadOnlyCollection<string> ConfiguredAxisIds { get; private set; } = Array.Empty<string>();

    public Task<IReadOnlyList<TiaHardwareModuleInfo>> ListHardwareAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TiaHardwareModuleInfo>>(HardwareModules);

    public Task<TiaProjectTree> ListProgramBlocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Blocks);

    public Task<TiaProjectTree> ListDataTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DataTypes);

    public Task ImportBlockAsync(string folderPath, string filePath, CancellationToken cancellationToken = default)
    {
        ImportedBlocks.Add((folderPath, filePath));
        return Task.CompletedTask;
    }

    public Task ExportBlockAsync(
        string folderPath,
        string blockName,
        string filePath,
        CancellationToken cancellationToken = default) => WriteExportAsync(filePath, blockName, cancellationToken);

    public Task ImportDataTypeAsync(string folderPath, string filePath, CancellationToken cancellationToken = default)
    {
        ImportedDataTypes.Add((folderPath, filePath));
        return Task.CompletedTask;
    }

    public Task ExportDataTypeAsync(
        string folderPath,
        string dataTypeName,
        string filePath,
        CancellationToken cancellationToken = default) => WriteExportAsync(filePath, dataTypeName, cancellationToken);

    public Task CreateBlockFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        AddFolder(Blocks, parentPath, name);
        return Task.CompletedTask;
    }

    public Task CreateDataTypeFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        AddFolder(DataTypes, parentPath, name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TiaAxisInfo>> ListAxesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TiaAxisInfo>>(AxisItems);

    public Task<IReadOnlyList<TiaAxisInfo>> ConfigureAxesAsync(
        IReadOnlyCollection<string> axisIds,
        CancellationToken cancellationToken = default)
    {
        ConfiguredAxisIds = axisIds.ToArray();
        return Task.FromResult<IReadOnlyList<TiaAxisInfo>>(
            AxisItems.Where(axis => axisIds.Contains(axis.Id, StringComparer.OrdinalIgnoreCase)).ToArray());
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saved = true;
        return Task.CompletedTask;
    }

    private static void AddFolder(TiaProjectTree tree, string parentPath, string name)
    {
        var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
        if (tree.Folders.All(folder => !string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)))
            tree.Folders.Add(new TiaFolderInfo { Name = name, Path = path });
    }

    private static async Task WriteExportAsync(string path, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, value, cancellationToken);
    }
}

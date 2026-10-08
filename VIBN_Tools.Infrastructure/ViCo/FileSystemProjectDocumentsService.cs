using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

/// <summary>Reads server project documents without depending on a workstation's online state.</summary>
public sealed class FileSystemProjectDocumentsService : IViCoProjectDocumentsService
{
    private readonly SemaphoreSlim _reads = new(4, 4);

    public async Task<ViCoProjectDocumentsResult> ReadAsync(string simulationPath, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(simulationPath, "00_Documents");
        await _reads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var files = new List<string>();
                    foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                             { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files.Add(Path.GetRelativePath(folder, path));
                    }
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                    return new ViCoProjectDocumentsResult(folder, files,
                        files.Count == 0 ? ViCoProjectDocumentsStatus.Empty : ViCoProjectDocumentsStatus.Available);
                }
                catch (DirectoryNotFoundException) { return new ViCoProjectDocumentsResult(folder, [], ViCoProjectDocumentsStatus.Missing); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                    return new ViCoProjectDocumentsResult(folder, [], ViCoProjectDocumentsStatus.Unavailable, exception.Message);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _reads.Release(); }
    }
}

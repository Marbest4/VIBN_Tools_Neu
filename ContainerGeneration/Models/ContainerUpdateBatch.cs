namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>Always releases every container's deferred update, even if a notification fails.</summary>
public sealed class ContainerUpdateBatch : IDisposable
{
    private IDisposable[]? _scopes;
    public ContainerUpdateBatch(IEnumerable<ContainerData> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);
        var items = containers.Distinct().ToArray();
        if (items.Any(item => item is null)) throw new ArgumentException("A container batch contains null.", nameof(containers));
        _scopes = items.Select(container => container.DeferUpdates()).ToArray();
    }
    public void Dispose()
    {
        var scopes = Interlocked.Exchange(ref _scopes, null);
        if (scopes is null) return;
        List<Exception>? errors = null;
        foreach (var scope in scopes)
        {
            try { scope.Dispose(); }
            catch (Exception exception) when (ContainerGenerationExceptionPolicy.IsRecoverable(exception))
            { (errors ??= []).Add(exception); }
        }
        if (errors is not null) throw new AggregateException("Container updates could not be completed.", errors);
    }
}

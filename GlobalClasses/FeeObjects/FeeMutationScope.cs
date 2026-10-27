namespace VIBN_Tools.GlobalClasses.FeeObjects;

/// <summary>Serializes complete mutations; nested generation keeps the same async scope.</summary>
public static class FeeMutationScope
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly AsyncLocal<bool> IsOwner = new();

    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token = default)
    {
        if (IsOwner.Value) return await operation();
        await Gate.WaitAsync(token);
        try { IsOwner.Value = true; return await operation(); }
        finally { IsOwner.Value = false; Gate.Release(); }
    }
}

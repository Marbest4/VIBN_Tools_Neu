namespace VIBN_Tools.ContainerToFeeVisual;

/// <summary>Preserves the failing live-read phase and distinguishes SDK cancellation from a user cancellation.</summary>
internal static class Fee2ContainerReadPhase
{
    internal static async Task<T> ReadAsync<T>(string phase, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await read();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{phase}: {Describe(exception)}", exception);
        }
    }

    internal static string Describe(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            messages.Add($"{current.GetType().Name}: {current.Message}");
        var description = string.Join(" → ", messages);
        return exception is OperationCanceledException
            ? $"Die FEE-Anfrage wurde intern abgebrochen (Zeitüberschreitung oder Verbindungswechsel möglich). {description}"
            : description;
    }
}

using System.Reflection;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFeeVisual;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static partial class Program
{
    private static void VerifyFeeRootReadFailures()
    {
        var phase = typeof(Fee2ContainerService).Assembly.GetType("VIBN_Tools.ContainerToFeeVisual.Fee2ContainerReadPhase")!;
        var read = phase.GetMethod("ReadAsync", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(int));
        Task<int> Read(Func<Task<int>> action, CancellationToken token) =>
            (Task<int>)read.Invoke(null, ["Signal- und Hilfslogikrouten lesen", action, token])!;

        var timeout = new TaskCanceledException("SDK request exceeded 60 seconds", new TimeoutException("FEE did not respond"));
        Exception? failure = null;
        try { Read(() => Task.FromException<int>(timeout), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidOperationException exception) { failure = exception; }
        if (failure is null || !ReferenceEquals(failure.InnerException, timeout) ||
            !failure.Message.Contains("Signal- und Hilfslogikrouten") || !failure.Message.Contains("60 seconds") ||
            !failure.Message.Contains("FEE did not respond"))
            throw new InvalidOperationException("An SDK timeout was reported as a user cancellation or lost its failing phase/cause.");

        var vm = new Fee2ContainerPageVM();
        typeof(Fee2ContainerPageVM).GetMethod("ReportOperationFailure", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, ["FEE-Roots konnten nicht gelesen werden", failure]);
        if (!vm.StatusText.Contains("60 seconds") || !vm.IsIssuesExpanded ||
            !vm.Issues.Any(issue => issue.Contains(nameof(TimeoutException))))
            throw new InvalidOperationException("Root-read diagnostics are hidden or do not include the original SDK failure.");

        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        try
        {
            Read(() => { calls++; cancellation.Cancel(); return Task.FromCanceled<int>(cancellation.Token); }, cancellation.Token)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("A requested cancellation did not stop the phase.");
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token) { }
        try
        {
            Read(() => { calls++; return Task.FromResult(1); }, cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("A cancelled read still contacted the SDK.");
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token) { }
        if (calls != 1 || Read(() => Task.FromResult(7), CancellationToken.None).GetAwaiter().GetResult() != 7)
            throw new InvalidOperationException("Successful reads or cancellation boundaries changed.");
    }
}

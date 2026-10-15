using System.Diagnostics;

namespace VIBN_Tools.GlobalClasses;

public sealed record FeeReadProgress(string Phase, int Completed, int Total, string Unit, string Detail = "")
{
    public int Percent => Total > 0 ? (int)Math.Clamp((long)Completed * 100 / Total, 0, 100) : 0;
    public string Text => $"{Phase}: {Unit} {Completed}/{Total}" + (Detail.Length > 0 ? $" · {Detail}" : "");
}

/// <summary>Operation-local progress follows async SDK calls without global event subscriptions or competing UI updates.</summary>
public static class FeeReadProgressScope
{
    private static readonly AsyncLocal<ReadScope?> Current = new();
    public static IDisposable Begin(IProgress<FeeReadProgress> observer)
    {
        var scope = new ReadScope(observer, Current.Value); Current.Value = scope; return scope;
    }
    public static void Report(string phase, int completed, int total, string unit = "SimObjects", string detail = "") =>
        Current.Value?.Report(new(phase, Math.Clamp(completed, 0, Math.Max(0, total)), Math.Max(0, total), unit, detail));

    private sealed class ReadScope(IProgress<FeeReadProgress> observer, ReadScope? previous) : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _sync = new();
        private string _phase = "";
        public void Report(FeeReadProgress value)
        {
            lock (_sync)
            {
                if (value.Phase == _phase && value.Completed != value.Total && value.Completed != 0 && _clock.ElapsedMilliseconds < 100) return;
                _phase = value.Phase; _clock.Restart(); observer.Report(value);
            }
        }
        public void Dispose() => Current.Value = previous;
    }
}

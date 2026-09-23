using System.Text;
using System.IO;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.IbnRemote;

/// <summary>Small file logger with no dependency on the full desktop application.</summary>
public sealed class IbnRemoteFileLog : IApplicationLog
{
    private readonly object _gate = new();

    public static IbnRemoteFileLog Instance { get; } = new();

    private IbnRemoteFileLog()
    {
    }

    public string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GROB",
        "VIBN_Tools_IBN",
        "Logs");

    public void Information(string area, string message) => Write("INFO", area, message);

    public void Warning(string area, string message, string details = "") =>
        Write("WARN", area, message, details);

    public void Error(string area, string message, Exception exception) =>
        Write("ERROR", area, message, exception.ToString());

    private void Write(string level, string area, string message, string details = "")
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTimeOffset.Now:O}\t{level}\t{area}\t{message}";
            if (!string.IsNullOrWhiteSpace(details))
                line += $"\t{details.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}";
            lock (_gate)
            {
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"IBN-{DateTime.Today:yyyy-MM-dd}.log"),
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never prevent an RDP connection attempt.
        }
    }
}

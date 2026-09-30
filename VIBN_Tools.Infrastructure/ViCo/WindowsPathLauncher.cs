using System.Diagnostics;
using System.Text.RegularExpressions;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

public sealed class WindowsPathLauncher : IExternalPathLauncher
{
    private static readonly Regex SafeHostName = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Ein Pfad ist erforderlich.", nameof(path));

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenCommandPrompt() => StartCommandPrompt(null);

    public void OpenContinuousPing(string hostName)
    {
        var normalized = hostName?.Trim() ?? string.Empty;
        if (!SafeHostName.IsMatch(normalized))
            throw new ArgumentException("Der Rechnername enthält unzulässige Zeichen.", nameof(hostName));

        StartCommandPrompt($"ping {normalized} -t");
    }

    private static void StartCommandPrompt(string? command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (!string.IsNullOrWhiteSpace(command))
            startInfo.Arguments = $"/K {command}";
        Process.Start(startInfo);
    }
}

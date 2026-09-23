using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace VIBN_Tools.Application;

/// <summary>Reads version and build-file timestamp from the running binary.</summary>
public static class ApplicationBuildInformation
{
    public static string DisplayText { get; } = CreateDisplayText();

    private static string CreateDisplayText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ApplicationBuildInformation).Assembly;
        var location = assembly.Location;
        var fileVersion = string.IsNullOrWhiteSpace(location)
            ? null
            : FileVersionInfo.GetVersionInfo(location).FileVersion;
        var version = !string.IsNullOrWhiteSpace(fileVersion)
            ? fileVersion
            : assembly.GetName().Version?.ToString() ?? "unbekannt";
        var buildTime = !string.IsNullOrWhiteSpace(location) && File.Exists(location)
            ? File.GetLastWriteTime(location)
            : DateTime.MinValue;
        return buildTime == DateTime.MinValue
            ? $"Version {version}"
            : $"Version {version} · Build {buildTime:dd.MM.yyyy HH:mm}";
    }
}

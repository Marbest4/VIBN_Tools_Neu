using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace VIBN_Tools.Settings;

/// <summary>Immutable result of the local FEE version discovery.</summary>
public sealed record FeeVersionInfo(
    string UsedSdkVersion,
    string InstalledFeeVersion,
    bool HasVersionMismatch,
    string StatusMessage);

public interface IFeeVersionInfoProvider
{
    FeeVersionInfo Read();
}

/// <summary>
/// Reads the SDK selected during compilation and the newest locally installed
/// fe.screen-sim version. Discovery is intentionally best-effort: missing or
/// inaccessible registry keys must never prevent Project Settings from loading.
/// </summary>
public sealed class FeeVersionInfoProvider : IFeeVersionInfoProvider
{
    private const string UnknownVersion = "Nicht erkannt";
    private static readonly Regex NumericVersionPattern =
        new(@"(?<!\d)(\d+(?:\.\d+){1,3})(?!\d)", RegexOptions.Compiled);
    private readonly IReadOnlyList<string> _installationRoots;
    private readonly bool _includeRegistry;

    public FeeVersionInfoProvider()
        : this(GetDefaultInstallationRoots(), includeRegistry: true)
    {
    }

    /// <summary>
    /// Testable discovery entry point. Every candidate still has to contain
    /// <c>Bin\FS.SDK.dll</c>; a version-like folder name alone is never enough.
    /// </summary>
    public FeeVersionInfoProvider(IEnumerable<string> installationRoots, bool includeRegistry = false)
    {
        ArgumentNullException.ThrowIfNull(installationRoots);
        _installationRoots = installationRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _includeRegistry = includeRegistry;
    }

    public FeeVersionInfo Read()
    {
        var usedSdkVersion = DetectUsedSdkVersion();
        var installedFeeVersion = DetectInstalledFeeVersion();
        var mismatch = TryParseVersion(usedSdkVersion, out var used) &&
                       TryParseVersion(installedFeeVersion, out var installed) &&
                       used != installed;
        var status = mismatch
            ? $"Versionsabweichung: SDK {usedSdkVersion}, lokal installiert {installedFeeVersion}."
            : "SDK und lokale FEE-Version stimmen überein.";

        if (usedSdkVersion == UnknownVersion || installedFeeVersion == UnknownVersion)
            status = "Ein vollständiger Versionsvergleich ist nicht möglich.";

        return new FeeVersionInfo(usedSdkVersion, installedFeeVersion, mismatch, status);
    }

    private static string DetectUsedSdkVersion()
    {
        try
        {
            // Prefer the actual runtime binary. The build metadata remains a
            // fallback for diagnostic builds in which dependencies are not yet
            // copied next to the executable.
            var sdkPath = Path.Combine(AppContext.BaseDirectory, "FS.SDK.dll");
            if (File.Exists(sdkPath))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(sdkPath);
                if (TryParseVersion(versionInfo.ProductVersion, out var productVersion))
                    return Format(productVersion);
                if (TryParseVersion(versionInfo.FileVersion, out var fileVersion))
                    return Format(fileVersion);
            }

            var metadataVersion = typeof(FeeVersionInfoProvider).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute =>
                    string.Equals(attribute.Key, "FeeSdkVersion", StringComparison.Ordinal))
                ?.Value;
            if (TryParseVersion(metadataVersion, out var parsedMetadata))
                return Format(parsedMetadata);
        }
        catch
        {
            // Version display is diagnostic only and must never block startup.
        }

        return UnknownVersion;
    }

    private string DetectInstalledFeeVersion()
    {
        var versions = new List<Version>();
        AddDirectoryVersions(versions, _installationRoots);
        if (_includeRegistry)
            AddRegistryVersions(versions);
        return versions.Count == 0 ? UnknownVersion : Format(versions.Max()!);
    }

    private static IReadOnlyList<string> GetDefaultInstallationRoots()
    {
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => Path.Combine(path, "fe.screen-sim V5"))
        .ToArray();
    }

    private static void AddDirectoryVersions(
        ICollection<Version> versions,
        IEnumerable<string> installationRoots)
    {
        foreach (var installRoot in installationRoots)
        {
            try
            {
                if (!Directory.Exists(installRoot))
                    continue;

                var candidates = Directory.EnumerateDirectories(installRoot).Prepend(installRoot);
                foreach (var directory in candidates)
                {
                    var sdkMarker = GetSdkMarker(directory);
                    if (sdkMarker is null)
                        continue;

                    if (TryParseVersion(Path.GetFileName(directory), out var version) ||
                        TryReadFileVersion(sdkMarker, out version))
                        versions.Add(version);
                }
            }
            catch
            {
                // Continue with registry discovery when a folder is inaccessible.
            }
        }
    }

    private static void AddRegistryVersions(ICollection<Version> versions)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                        continue;

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var product = uninstall.OpenSubKey(subKeyName);
                        var displayName = product?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName) ||
                            !displayName.Contains("screen-sim", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var installLocation = product?.GetValue("InstallLocation") as string;
                        var sdkMarker = GetSdkMarker(installLocation);
                        if (sdkMarker is null)
                            continue;

                        if (TryParseVersion(product?.GetValue("DisplayVersion") as string, out var version) ||
                            TryReadFileVersion(sdkMarker, out version))
                            versions.Add(version);
                    }
                }
                catch
                {
                    // Registry access is optional and can be restricted by policy.
                }
            }
        }
    }

    private static string? GetSdkMarker(string? installationDirectory)
    {
        if (string.IsNullOrWhiteSpace(installationDirectory))
            return null;

        try
        {
            var marker = Path.Combine(Path.GetFullPath(installationDirectory), "Bin", "FS.SDK.dll");
            return File.Exists(marker) ? marker : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryReadFileVersion(string sdkPath, out Version version)
    {
        version = new Version();
        try
        {
            var info = FileVersionInfo.GetVersionInfo(sdkPath);
            return TryParseVersion(info.ProductVersion, out version) ||
                   TryParseVersion(info.FileVersion, out version);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = NumericVersionPattern.Match(value);
        return match.Success && Version.TryParse(match.Groups[1].Value, out version);
    }

    private static string Format(Version version) => $"V{version}";
}

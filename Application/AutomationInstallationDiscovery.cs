using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VIBN_Tools.Application;

public enum AutomationComponentKind
{
    TiaPortal,
    TiaOpenness,
    WinCc,
    SiemensExtension,
    TwinCat
}

public sealed record InstalledProductEvidence(
    string DisplayName,
    string DisplayVersion,
    string InstallLocation,
    string RegistryPath);

public sealed record InstalledAutomationComponent(
    AutomationComponentKind Kind,
    string Product,
    string Version,
    string InstallLocation,
    string Evidence)
{
    public string KindLabel => Kind switch
    {
        AutomationComponentKind.TiaPortal => "TIA Portal",
        AutomationComponentKind.TiaOpenness => "TIA Openness",
        AutomationComponentKind.WinCc => "WinCC",
        AutomationComponentKind.SiemensExtension => "Siemens-Erweiterung",
        AutomationComponentKind.TwinCat => "TwinCAT",
        _ => Kind.ToString()
    };
}

public sealed record AutomationInstallationInventory(
    IReadOnlyList<InstalledAutomationComponent> Components,
    IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<string> TiaVersions => Components
        .Where(component => component.Kind is
            AutomationComponentKind.TiaPortal or AutomationComponentKind.TiaOpenness)
        .Select(component => NormalizeTiaVersion(component.Version, component.Product))
        .Where(version => version is not null)
        .Select(version => version!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(ParseVersion)
        .ToArray();

    private static string? NormalizeTiaVersion(string version, string product)
    {
        var match = Regex.Match($"{version} {product}", @"(?<!\d)V?(\d{1,2})(?:\.\d+)?", RegexOptions.IgnoreCase);
        return match.Success ? $"V{match.Groups[1].Value}" : null;
    }

    private static int ParseVersion(string value) =>
        int.TryParse(value.TrimStart('V', 'v'), out var parsed) ? parsed : 0;
}

public interface IAutomationInstallationDiscovery
{
    AutomationInstallationInventory Discover();
}

/// <summary>
/// Read-only discovery based on actual folders, Openness assemblies and the
/// Windows uninstall catalog. No fixed list of TIA versions is maintained.
/// </summary>
public sealed class AutomationInstallationDiscovery : IAutomationInstallationDiscovery
{
    private readonly IReadOnlyList<string> _programRoots;
    private readonly Func<(IReadOnlyList<InstalledProductEvidence> Products, IReadOnlyList<string> Diagnostics)>
        _installedProductReader;

    public AutomationInstallationDiscovery(
        IEnumerable<string>? programRoots = null,
        Func<(IReadOnlyList<InstalledProductEvidence> Products, IReadOnlyList<string> Diagnostics)>?
            installedProductReader = null)
    {
        _programRoots = (programRoots ?? DefaultProgramRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _installedProductReader = installedProductReader ?? ReadInstalledProducts;
    }

    public AutomationInstallationInventory Discover()
    {
        var components = new List<InstalledAutomationComponent>();
        var diagnostics = new List<string>();

        foreach (var root in _programRoots)
        {
            DiscoverTiaFolders(root, components, diagnostics);
            DiscoverTwinCatFolders(root, components, diagnostics);
        }

        var installedProducts = _installedProductReader();
        diagnostics.AddRange(installedProducts.Diagnostics);
        foreach (var product in installedProducts.Products)
        {
            var kind = ClassifyInstalledProduct(product.DisplayName);
            if (kind is null)
                continue;
            components.Add(new InstalledAutomationComponent(
                kind.Value,
                product.DisplayName,
                string.IsNullOrWhiteSpace(product.DisplayVersion) ? "nicht angegeben" : product.DisplayVersion,
                product.InstallLocation,
                product.RegistryPath));
        }

        var distinct = components
            .GroupBy(BuildIdentityKey, StringComparer.OrdinalIgnoreCase)
            // The same uninstall entry is frequently registered in the 32- and
            // 64-bit views with an empty path in one view. Show the product once
            // and retain the evidence with the most useful installation path.
            .Select(group => group
                .OrderByDescending(component => !string.IsNullOrWhiteSpace(component.InstallLocation))
                .ThenByDescending(component =>
                    string.Equals(component.Evidence, "Installationsordner", StringComparison.OrdinalIgnoreCase))
                .ThenBy(component => component.Evidence, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(component => component.Kind)
            .ThenByDescending(component => component.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(component => component.Product, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length == 0)
            diagnostics.Add("Keine unterstützte lokale Automatisierungsinstallation wurde erkannt.");
        return new AutomationInstallationInventory(distinct, diagnostics.Distinct().ToArray());
    }

    /// <summary>
    /// Collapses the folder and uninstall-registry evidence for the same
    /// installation. Product labels differ between both sources (for example
    /// "Portal V20" and "SIMATIC STEP 7 Professional V20"), therefore the
    /// technical product family and detected major version form the identity.
    /// Siemens extensions keep their normalized product name so unrelated
    /// add-ons with the same version are never merged.
    /// </summary>
    public static string BuildIdentityKey(InstalledAutomationComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var version = NormalizeVersion(component.Version, component.Product);
        var product = component.Kind == AutomationComponentKind.SiemensExtension
            ? NormalizeProduct(component.Product)
            : component.Kind.ToString();
        return $"{component.Kind}|{product}|{version}";
    }

    private static string NormalizeVersion(string version, string product)
    {
        var source = $"{version} {product}";
        var tia = Regex.Match(source, @"(?<![A-Z0-9])V\s*(\d{1,2})(?:\.\d+)?", RegexOptions.IgnoreCase);
        if (tia.Success)
            return $"V{tia.Groups[1].Value}";

        var numeric = Regex.Match(source, @"(?<!\d)(\d{1,3}(?:\.\d+){0,3})(?!\d)");
        if (!numeric.Success)
            return "UNVERSIONED";
        var parts = numeric.Groups[1].Value.Split('.').ToList();
        while (parts.Count > 1 && parts[^1] == "0")
            parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }

    private static string NormalizeProduct(string value) => new(
        (value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    public static AutomationComponentKind? ClassifyInstalledProduct(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        if (displayName.Contains("TwinCAT", StringComparison.OrdinalIgnoreCase))
            return AutomationComponentKind.TwinCat;
        if (displayName.Contains("WinCC", StringComparison.OrdinalIgnoreCase))
            return AutomationComponentKind.WinCc;
        if (displayName.Contains("Openness", StringComparison.OrdinalIgnoreCase))
            return AutomationComponentKind.TiaOpenness;
        if (displayName.Contains("TIA Portal", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("STEP 7", StringComparison.OrdinalIgnoreCase))
            return AutomationComponentKind.TiaPortal;
        if (displayName.Contains("Siemens", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("SIMATIC", StringComparison.OrdinalIgnoreCase))
            return AutomationComponentKind.SiemensExtension;
        return null;
    }

    private static IEnumerable<string> DefaultProgramRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemDrive))
            yield return Path.Combine(systemDrive, "TwinCAT");
    }

    private static void DiscoverTiaFolders(
        string root,
        ICollection<InstalledAutomationComponent> components,
        ICollection<string> diagnostics)
    {
        var automationRoot = Path.Combine(root, "Siemens", "Automation");
        if (!Directory.Exists(automationRoot))
            return;
        try
        {
            foreach (var portalPath in Directory.EnumerateDirectories(automationRoot, "Portal V*"))
            {
                var folder = Path.GetFileName(portalPath);
                var version = Regex.Match(folder, @"V\d+(?:\.\d+)?", RegexOptions.IgnoreCase).Value;
                components.Add(new InstalledAutomationComponent(
                    AutomationComponentKind.TiaPortal,
                    folder,
                    string.IsNullOrWhiteSpace(version) ? "nicht erkannt" : version.ToUpperInvariant(),
                    portalPath,
                    "Installationsordner"));

                var publicApi = Path.Combine(portalPath, "PublicAPI");
                if (!Directory.Exists(publicApi))
                    continue;
                foreach (var apiPath in Directory.EnumerateDirectories(publicApi))
                {
                    var assemblyPath = Path.Combine(apiPath, "Siemens.Engineering.dll");
                    if (!File.Exists(assemblyPath))
                        continue;
                    components.Add(new InstalledAutomationComponent(
                        AutomationComponentKind.TiaOpenness,
                        "Siemens TIA Portal Openness",
                        Path.GetFileName(apiPath),
                        assemblyPath,
                        "Siemens.Engineering.dll"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"TIA-Installationsordner '{automationRoot}' konnte nicht vollständig gelesen werden: {exception.Message}");
        }
    }

    private static void DiscoverTwinCatFolders(
        string root,
        ICollection<InstalledAutomationComponent> components,
        ICollection<string> diagnostics)
    {
        var candidates = new[]
        {
            Path.Combine(root, "Beckhoff", "TwinCAT", "3.1"),
            root.EndsWith("TwinCAT", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, "3.1")
                : Path.Combine(root, "TwinCAT", "3.1")
        };
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(candidate))
                    continue;
                components.Add(new InstalledAutomationComponent(
                    AutomationComponentKind.TwinCat,
                    "Beckhoff TwinCAT",
                    Path.GetFileName(candidate),
                    candidate,
                    "Installationsordner"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"TwinCAT-Installationsordner '{candidate}' konnte nicht gelesen werden: {exception.Message}");
            }
        }
    }

    private static (
        IReadOnlyList<InstalledProductEvidence> Products,
        IReadOnlyList<string> Diagnostics) ReadInstalledProducts()
    {
        var products = new List<InstalledProductEvidence>();
        var diagnostics = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = baseKey.OpenSubKey(uninstallPath);
                if (uninstall is null)
                    continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(subKeyName);
                    var name = product?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    products.Add(new InstalledProductEvidence(
                        name,
                        product?.GetValue("DisplayVersion") as string ?? string.Empty,
                        product?.GetValue("InstallLocation") as string ?? string.Empty,
                        $"HKLM ({view})\\{uninstallPath}\\{subKeyName}"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                diagnostics.Add($"Installationskatalog {view} konnte nicht gelesen werden: {exception.Message}");
            }
        }
        return (products, diagnostics);
    }
}

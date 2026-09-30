using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VIBN_Tools.Quality;

public sealed class ProjectProfilePolicyValidator
{
    private static readonly Regex RangePattern = new(
        @"^\s*%?(?<kind>[EAIQ])\s*(?<from>\d+)(?:\.\d+)?\s*-\s*%?(?:[EAIQ])?\s*(?<to>\d+)(?:\.\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AddressPattern = new(
        @"^\s*%?(?<kind>[EAIQ])\s*(?<byte>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IReadOnlyList<QualityFinding> Validate(ProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var findings = ValidateNamingExpressions(profile).ToList();
        if (!File.Exists(profile.ContainerPath))
            return findings;

        var document = Load(profile.ContainerPath);
        var containers = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("Container", StringComparison.OrdinalIgnoreCase))
            .Select(element => new
            {
                Name = Read(element, "Component", "Name", "ID", "Id"),
                Type = Read(element, "Type", "ContainerType"),
            })
            .ToArray();
        var allowedTypes = profile.AllowedContainerTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedTypes.Count > 0)
        {
            foreach (var container in containers.Where(item => !allowedTypes.Contains(item.Type)))
            {
                findings.Add(new QualityFinding(
                    "Projektprofil", "CONTAINER_TYPE_NOT_ALLOWED", QualityStatus.Failed,
                    $"Container '{container.Name}' verwendet den im Profil nicht erlaubten Typ '{container.Type}'."));
            }
        }

        ApplyNamingRule(profile, "Container", containers.Select(item => item.Name), findings);
        var signals = new ContainerSignalObservationReader().Read(profile.ContainerPath);
        ApplyNamingRule(profile, "Signal", signals.Select(item => item.SymbolicName), findings);
        if (profile.SignalAddressRanges.Count > 0)
        {
            foreach (var signal in signals.Where(signal => !string.IsNullOrWhiteSpace(signal.Address)))
            {
                if (!profile.SignalAddressRanges.Any(range => IsAddressAllowed(signal.Address, range)))
                {
                    findings.Add(new QualityFinding(
                        "Projektprofil", "SIGNAL_ADDRESS_OUTSIDE_PROFILE", QualityStatus.Failed,
                        $"Signal '{signal.SymbolicName}' mit Adresse '{signal.Address}' liegt außerhalb der Profilbereiche " +
                        $"{string.Join(", ", profile.SignalAddressRanges)}."));
                }
            }
        }

        if (findings.All(item => item.Status != QualityStatus.Failed))
        {
            findings.Add(new QualityFinding(
                "Projektprofil", "PROFILE_POLICY_PASSED", QualityStatus.Passed,
                $"{containers.Length} Container und {signals.Count} Signale entsprechen den konfigurierten Projektregeln."));
        }
        return findings;
    }

    private static IReadOnlyList<QualityFinding> ValidateNamingExpressions(ProjectProfile profile)
    {
        var findings = new List<QualityFinding>();
        foreach (var pair in profile.NamingRules)
        {
            try
            {
                _ = new Regex(pair.Value, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                findings.Add(new QualityFinding(
                    "Projektprofil", "NAMING_REGEX_INVALID", QualityStatus.Failed,
                    $"Namensregel '{pair.Key}' ist ungültig: {exception.Message}"));
            }
        }
        return findings;
    }

    private static void ApplyNamingRule(
        ProjectProfile profile,
        string key,
        IEnumerable<string> values,
        ICollection<QualityFinding> findings)
    {
        if (!profile.NamingRules.TryGetValue(key, out var expression) || string.IsNullOrWhiteSpace(expression))
            return;
        Regex regex;
        try
        {
            regex = new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return;
        }
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value) && !regex.IsMatch(value)))
        {
            findings.Add(new QualityFinding(
                "Projektprofil", $"{key.ToUpperInvariant()}_NAME_INVALID", QualityStatus.Failed,
                $"{key}-Name '{value}' verletzt die Profilregel '{expression}'."));
        }
    }

    private static bool IsAddressAllowed(string address, string range)
    {
        var rangeMatch = RangePattern.Match(range);
        var addressMatch = AddressPattern.Match(address);
        if (!rangeMatch.Success || !addressMatch.Success)
            return address.StartsWith(range.Trim().TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        if (NormalizeKind(rangeMatch.Groups["kind"].Value) != NormalizeKind(addressMatch.Groups["kind"].Value))
            return false;
        var value = int.Parse(addressMatch.Groups["byte"].Value, CultureInfo.InvariantCulture);
        var from = int.Parse(rangeMatch.Groups["from"].Value, CultureInfo.InvariantCulture);
        var to = int.Parse(rangeMatch.Groups["to"].Value, CultureInfo.InvariantCulture);
        return value >= Math.Min(from, to) && value <= Math.Max(from, to);
    }

    private static char NormalizeKind(string value) => char.ToUpperInvariant(value[0]) switch
    {
        'E' => 'I',
        'A' => 'Q',
        var kind => kind,
    };

    private static XDocument Load(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 50L * 1024 * 1024,
        };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    private static string Read(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var child = element.Elements().FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (child is not null && !string.IsNullOrWhiteSpace(child.Value))
                return child.Value.Trim();
            var attribute = element.Attributes().FirstOrDefault(candidate =>
                candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
        }
        return string.Empty;
    }
}

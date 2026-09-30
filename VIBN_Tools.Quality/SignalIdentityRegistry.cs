using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VIBN_Tools.Quality;

public sealed record SignalObservation(
    string SignalId,
    string SymbolicName,
    string Address,
    string DataType,
    string InterfaceGuid,
    string Container,
    string Slot,
    string FeeVariableGuid,
    string SourceKey);

public sealed record SignalIdentity(
    string StableId,
    string SignalId,
    string SymbolicName,
    string Address,
    string DataType,
    string InterfaceGuid,
    string Container,
    string Slot,
    string FeeVariableGuid,
    string SourceKey,
    IReadOnlyList<string> PreviousNames,
    IReadOnlyList<string> PreviousAddresses,
    DateTimeOffset LastConfirmedUtc)
{
    public string PreviousNamesText => string.Join(", ", PreviousNames);

    public string PreviousAddressesText => string.Join(", ", PreviousAddresses);
}

public sealed record SignalReconciliationResult(
    IReadOnlyList<SignalIdentity> Identities,
    IReadOnlyList<QualityFinding> Findings,
    int Added,
    int Updated,
    int Unchanged,
    int Conflicts);

public sealed class SignalIdentityRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SignalIdentityRegistry(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VIBN_Tools",
            "quality",
            "signal-identities.json");
    }

    public IReadOnlyList<SignalIdentity> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<SignalIdentity>>(File.ReadAllText(_path), JsonOptions) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public SignalReconciliationResult Reconcile(IEnumerable<SignalObservation> observations, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var identities = Load().ToList();
        var findings = new List<QualityFinding>();
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var conflicts = 0;

        foreach (var observation in observations)
        {
            var candidates = FindCandidates(identities, observation).DistinctBy(item => item.StableId).ToArray();
            if (candidates.Length > 1)
            {
                conflicts++;
                findings.Add(new QualityFinding(
                    "Signalregister",
                    "SIGNAL_IDENTITY_AMBIGUOUS",
                    QualityStatus.Failed,
                    $"Signal '{observation.SymbolicName}' ({observation.Address}) passt zu {candidates.Length} Identitäten.",
                    "Signal-ID, Interface oder FEE-GUID eindeutig korrigieren."));
                continue;
            }

            if (candidates.Length == 0)
            {
                var identity = CreateIdentity(observation);
                identities.Add(identity);
                added++;
                findings.Add(new QualityFinding("Signalregister", "SIGNAL_NEW", QualityStatus.Warning, $"Neues Signal registriert: {observation.SymbolicName} ({observation.Address})."));
                continue;
            }

            var current = candidates[0];
            if (HasHardConflict(current, observation, out var conflictMessage))
            {
                conflicts++;
                findings.Add(new QualityFinding("Signalregister", "SIGNAL_IDENTITY_CONFLICT", QualityStatus.Failed, conflictMessage, "Zuordnung manuell prüfen; bestehende Identität wurde nicht überschrieben."));
                continue;
            }

            var replacement = Update(current, observation);
            if (replacement == current)
            {
                unchanged++;
                continue;
            }
            identities[identities.IndexOf(current)] = replacement;
            updated++;
            findings.Add(new QualityFinding(
                "Signalregister",
                "SIGNAL_CHANGED",
                QualityStatus.Warning,
                DescribeChanges(current, replacement)));
        }

        foreach (var duplicate in identities.Where(item => !string.IsNullOrWhiteSpace(item.FeeVariableGuid))
                     .GroupBy(item => item.FeeVariableGuid, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            conflicts++;
            findings.Add(new QualityFinding("Signalregister", "SIGNAL_FEE_GUID_DUPLICATE", QualityStatus.Failed, $"FEE-Variablen-GUID '{duplicate.Key}' ist mehreren Identitäten zugeordnet."));
        }

        var ordered = identities.OrderBy(item => item.SymbolicName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        if (persist && conflicts == 0)
            AtomicJsonFile.Write(_path, ordered, JsonOptions);
        return new SignalReconciliationResult(ordered, findings, added, updated, unchanged, conflicts);
    }

    private static IEnumerable<SignalIdentity> FindCandidates(IEnumerable<SignalIdentity> identities, SignalObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.SignalId))
        {
            var byId = identities.Where(item => string.Equals(item.SignalId, observation.SignalId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length > 0)
                return byId;
        }
        if (!string.IsNullOrWhiteSpace(observation.FeeVariableGuid))
        {
            var byFee = identities.Where(item => string.Equals(item.FeeVariableGuid, observation.FeeVariableGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byFee.Length > 0)
                return byFee;
        }
        var byInterfaceAndName = identities.Where(item =>
            !string.IsNullOrWhiteSpace(observation.InterfaceGuid) &&
            string.Equals(item.InterfaceGuid, observation.InterfaceGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.SymbolicName, observation.SymbolicName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byInterfaceAndName.Length > 0)
            return byInterfaceAndName;
        return identities.Where(item =>
            string.Equals(item.SymbolicName, observation.SymbolicName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Address, observation.Address, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHardConflict(SignalIdentity current, SignalObservation observation, out string message)
    {
        if (!string.IsNullOrWhiteSpace(current.SignalId) && !string.IsNullOrWhiteSpace(observation.SignalId) &&
            !string.Equals(current.SignalId, observation.SignalId, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Signal '{observation.SymbolicName}' kollidiert mit bestehender Signal-ID '{current.SignalId}'.";
            return true;
        }
        if (!string.IsNullOrWhiteSpace(current.FeeVariableGuid) && !string.IsNullOrWhiteSpace(observation.FeeVariableGuid) &&
            !string.Equals(current.FeeVariableGuid, observation.FeeVariableGuid, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Signal '{observation.SymbolicName}' besitzt eine andere FEE-GUID als die registrierte Identität.";
            return true;
        }
        if (!string.IsNullOrWhiteSpace(current.DataType) && !string.IsNullOrWhiteSpace(observation.DataType) &&
            !string.Equals(current.DataType, observation.DataType, StringComparison.OrdinalIgnoreCase))
        {
            message = $"Datentypkonflikt für '{observation.SymbolicName}': registriert '{current.DataType}', neu '{observation.DataType}'.";
            return true;
        }
        message = string.Empty;
        return false;
    }

    private static SignalIdentity CreateIdentity(SignalObservation value) => new(
        StableHash(value.SignalId, value.InterfaceGuid, value.FeeVariableGuid, value.SymbolicName, value.Address, value.SourceKey),
        value.SignalId.Trim(), value.SymbolicName.Trim(), value.Address.Trim(), value.DataType.Trim(), value.InterfaceGuid.Trim(),
        value.Container.Trim(), value.Slot.Trim(), value.FeeVariableGuid.Trim(), value.SourceKey.Trim(), [], [], DateTimeOffset.UtcNow);

    private static SignalIdentity Update(SignalIdentity current, SignalObservation value)
    {
        var names = current.PreviousNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addresses = current.PreviousAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(current.SymbolicName, value.SymbolicName, StringComparison.OrdinalIgnoreCase) && current.SymbolicName.Length > 0)
            names.Add(current.SymbolicName);
        if (!string.Equals(current.Address, value.Address, StringComparison.OrdinalIgnoreCase) && current.Address.Length > 0)
            addresses.Add(current.Address);
        var replacement = current with
        {
            SignalId = Prefer(value.SignalId, current.SignalId),
            SymbolicName = Prefer(value.SymbolicName, current.SymbolicName),
            Address = Prefer(value.Address, current.Address),
            DataType = Prefer(value.DataType, current.DataType),
            InterfaceGuid = Prefer(value.InterfaceGuid, current.InterfaceGuid),
            Container = Prefer(value.Container, current.Container),
            Slot = Prefer(value.Slot, current.Slot),
            FeeVariableGuid = Prefer(value.FeeVariableGuid, current.FeeVariableGuid),
            SourceKey = Prefer(value.SourceKey, current.SourceKey),
            PreviousNames = names.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            PreviousAddresses = addresses.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            LastConfirmedUtc = DateTimeOffset.UtcNow,
        };
        return EquivalentBusinessData(current, replacement) ? current : replacement;
    }

    private static bool EquivalentBusinessData(SignalIdentity left, SignalIdentity right) =>
        string.Equals(left.SignalId, right.SignalId, StringComparison.Ordinal) &&
        string.Equals(left.SymbolicName, right.SymbolicName, StringComparison.Ordinal) &&
        string.Equals(left.Address, right.Address, StringComparison.Ordinal) &&
        string.Equals(left.DataType, right.DataType, StringComparison.Ordinal) &&
        string.Equals(left.InterfaceGuid, right.InterfaceGuid, StringComparison.Ordinal) &&
        string.Equals(left.Container, right.Container, StringComparison.Ordinal) &&
        string.Equals(left.Slot, right.Slot, StringComparison.Ordinal) &&
        string.Equals(left.FeeVariableGuid, right.FeeVariableGuid, StringComparison.Ordinal) &&
        string.Equals(left.SourceKey, right.SourceKey, StringComparison.Ordinal) &&
        left.PreviousNames.SequenceEqual(right.PreviousNames, StringComparer.Ordinal) &&
        left.PreviousAddresses.SequenceEqual(right.PreviousAddresses, StringComparer.Ordinal);

    private static string DescribeChanges(SignalIdentity oldValue, SignalIdentity newValue)
    {
        var changes = new List<string>();
        if (!string.Equals(oldValue.SymbolicName, newValue.SymbolicName, StringComparison.Ordinal))
            changes.Add($"Name '{oldValue.SymbolicName}' → '{newValue.SymbolicName}'");
        if (!string.Equals(oldValue.Address, newValue.Address, StringComparison.Ordinal))
            changes.Add($"Adresse '{oldValue.Address}' → '{newValue.Address}'");
        if (!string.Equals(oldValue.Container, newValue.Container, StringComparison.Ordinal) || !string.Equals(oldValue.Slot, newValue.Slot, StringComparison.Ordinal))
            changes.Add($"Zuordnung '{oldValue.Container}/{oldValue.Slot}' → '{newValue.Container}/{newValue.Slot}'");
        return $"Signal '{newValue.StableId}' aktualisiert: {string.Join(", ", changes)}.";
    }

    private static string Prefer(string candidate, string fallback) => string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    private static string StableHash(params string[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", values.Select(value => value?.Trim() ?? string.Empty))))).ToLowerInvariant();
}

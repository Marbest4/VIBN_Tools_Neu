using System.Text.Json;
using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Aufruf: VIBN_Tools.TiaLiveRead <Bridge.exe> [TIA-Version, Standard V20] [Ausgabe.json]");
    return 2;
}

var bridgePath = Path.GetFullPath(args[0]);
var version = args.Length >= 2 ? args[1] : "V20";
var outputPath = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
if (!File.Exists(bridgePath))
{
    Console.Error.WriteLine($"TIA Bridge nicht gefunden: {bridgePath}");
    return 2;
}

var pipeName = $"VIBN_TIA_LIVE_{Environment.ProcessId}_{Guid.NewGuid():N}";
await using var client = new NamedPipeTiaBridgeClient(new TiaBridgeClientOptions(
    pipeName,
    ConnectTimeout: TimeSpan.FromSeconds(15),
    RequestTimeout: TimeSpan.FromMinutes(3),
    BridgeExecutablePath: bridgePath));

try
{
    await client.ConnectAsync();
    if (!await client.PingAsync())
        throw new InvalidOperationException("Die gestartete TIA Bridge antwortet nicht.");

    await client.SelectVersionAsync(version);
    await client.AttachAsync();
    var plcs = await client.ListPlcsAsync();
    if (plcs.Count == 0)
        throw new InvalidOperationException("Im geöffneten TIA-Projekt wurde keine PLC gefunden.");

    var result = new List<LivePlcHardware>();
    foreach (var plc in plcs.OrderBy(plc => plc.Index))
    {
        await client.SelectPlcAsync(plc.Index);
        var modules = (await client.ListHardwareAsync())
            .OrderBy(module => module.DeviceIndex)
            .ThenBy(module => module.TraversalIndex)
            .ThenBy(module => module.AddressSetIndex)
            .ToArray();
        Validate(plc, modules);
        result.Add(new LivePlcHardware(plc, modules));
    }

    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    if (outputPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"JSON: {outputPath}");
    }

    foreach (var plc in result)
    {
        Console.WriteLine($"PLC [{plc.Plc.Index}] {plc.Plc.Name} ({plc.Plc.TypeIdentifier}), {plc.Modules.Count} adressführende Modulzeile(n)");
        Console.WriteLine("Gerät | Modul | Slot/Subslot | Eingang | Ausgang | IP | PROFINET");
        foreach (var module in plc.Modules)
        {
            Console.WriteLine(string.Join(" | ",
                EmptyAsDash(module.DeviceName),
                EmptyAsDash(module.ModuleName),
                $"{NumberOrDash(module.Slot)}/{NumberOrDash(module.Subslot)}",
                FormatRange("E", module.InputAddressRange, module.InputLengthBits),
                FormatRange("A", module.OutputAddressRange, module.OutputLengthBits),
                EmptyAsDash(module.IpAddress),
                EmptyAsDash(module.ProfinetName)));
        }
    }

    Console.WriteLine($"LIVE-ABNAHME OK: {result.Count} PLC(s), {result.Sum(item => item.Modules.Count)} Modulzeile(n); ausschließlich gelesen, nicht gespeichert.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"LIVE-ABNAHME FEHLGESCHLAGEN: {exception.Message}");
    Console.Error.WriteLine(exception);
    return 1;
}

static void Validate(TiaPlcInfo plc, IReadOnlyList<TiaHardwareModuleInfo> modules)
{
    var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var module in modules)
    {
        ValidateAddress(plc, module, "Eingang", module.InputStartByte, module.InputLengthBits, module.InputLength, module.InputEndByte);
        ValidateAddress(plc, module, "Ausgang", module.OutputStartByte, module.OutputLengthBits, module.OutputLength, module.OutputEndByte);

        var identity = string.Join("|",
            module.DeviceName,
            module.ModuleName,
            module.ModuleType,
            module.TypeIdentifier,
            module.InputStartByte,
            module.InputLengthBits,
            module.OutputStartByte,
            module.OutputLengthBits);
        if (!identities.Add(identity))
            throw new InvalidDataException($"PLC '{plc.Name}' enthält eine doppelte semantische Hardwarezeile: {identity}");
    }
}

static void ValidateAddress(
    TiaPlcInfo plc,
    TiaHardwareModuleInfo module,
    string kind,
    int start,
    int lengthBits,
    int lengthBytes,
    int end)
{
    if (start < 0)
    {
        if (lengthBits != 0 || lengthBytes != 0 || end != -1)
            throw InvalidAddress(plc, module, kind, start, lengthBits, lengthBytes, end);
        return;
    }

    var expectedBytes = (lengthBits + 7) / 8;
    var expectedEnd = start + expectedBytes - 1;
    if (lengthBits <= 0 || lengthBytes != expectedBytes || end != expectedEnd)
        throw InvalidAddress(plc, module, kind, start, lengthBits, lengthBytes, end);
}

static InvalidDataException InvalidAddress(
    TiaPlcInfo plc,
    TiaHardwareModuleInfo module,
    string kind,
    int start,
    int lengthBits,
    int lengthBytes,
    int end) => new(
    $"Ungültiger {kind}sbereich in PLC '{plc.Name}', Modul '{module.ModulePath}': " +
    $"Start={start}, Bits={lengthBits}, Bytes={lengthBytes}, Ende={end}.");

static string EmptyAsDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

static string NumberOrDash(int value) => value < 0 ? "—" : value.ToString();

static string FormatRange(string prefix, string range, int bits) =>
    range == "—" ? "—" : $"{prefix} {range} ({bits} Bit)";

internal sealed record LivePlcHardware(TiaPlcInfo Plc, IReadOnlyList<TiaHardwareModuleInfo> Modules);

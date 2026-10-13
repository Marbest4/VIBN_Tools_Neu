using System.Xml.Linq;
using VIBN_Tools.Core.Containers;
using VIBN_Tools.Core.Interfaces;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Require(SiemensSignalAddress.TryParse("A701", "Int", out var bare, out var warning) && warning is null &&
    bare is { Area: 'Q', StartByte: 701, Length: 2, Bit: null }, "A701 did not use its declared scalar width.");
Require(!SiemensSignalAddress.TryParse("A701", "Bool", out _, out warning) && warning is not null,
    "An ambiguous bit address was guessed rather than reported without throwing.");
foreach (var address in new[] { "AW701", "PAW701", "%QW701", " %qw701 " })
{
    Require(SiemensSignalAddress.TryParse(address, "Int", out var parsed, out warning) && warning is null,
        $"Supported address was rejected: {address}");
    Require(parsed == bare, $"German, international and peripheral area aliases disagree: {address}");
}
var hasBit0 = SiemensSignalAddress.TryParse("A701.0", "Bool", out var bit0, out _);
var hasBit1 = SiemensSignalAddress.TryParse("Q701.1", "Bool", out var bit1, out _);
Require(hasBit0 && hasBit1 && !bit0!.Overlaps(bit1!),
    "Different bits in the same byte conflict.");
Require(bare!.Overlaps(bit0!) && bare!.Overlaps(bit1!), "A bit was not checked against the enclosing word.");
Require(SiemensSignalAddress.TryParse("%IX701.0", "Bool", out var input, out _) && !bare!.Overlaps(input!),
    "Input and output areas were confused.");
Require(SiemensSignalAddress.TryParse("E701.0", "Bool", out var germanInput, out _) && input == germanInput,
    "E/I bit aliases do not agree.");
foreach (var address in new string?[] { null, "", "A", "AB", "AWoops", "AD2147483648", "A1.10", "A1.1.2", "A-1", "AX1", "Axis1", "IN[1]", "OUT[1]", "DB1.Flag" })
    Require(!SiemensSignalAddress.TryParse(address, "Bool", out _, out _), $"Invalid or symbolic address was parsed as a PLC range: {address}");
Require(SiemensSignalAddress.TryParse("AD2147483647", "DInt", out var last, out _) &&
    SiemensSignalAddress.TryParse("A2147483647.7", "Bool", out var lastBit, out _) && last!.Overlaps(lastBit!),
    "Address-range arithmetic overflowed near Int32.MaxValue.");

var document = XDocument.Parse("""
    <AutoCreate><Components>
      <Component name="Door" type="FeedSafetyDoor" minSignals="1" maxSignals="4"><Slots><Slot name="PLC_IN_InHomePos"/></Slots></Component>
      <Component name="Switch" type="Switch"><Slots><Slot name="PLC_IN_NO1"/></Slots></Component>
      <Component name="Sensor first" type="CustomSensor" minSignals="1" maxSignals="2"><Slots><Slot name="PLC_IN_A"/></Slots></Component>
      <Component name="Sensor second" type="CustomSensor" minSignals="2" maxSignals="3"><Slots><Slot name="PLC_IN_B"/></Slots></Component>
    </Components></AutoCreate>
    """);
var catalog = new ContainerRequirementsCatalog(document);
Require(catalog.Types.Count == 3 && catalog.ConfiguredName("feedsafetydoor") == "FeedSafetyDoor",
    "Req types were duplicated or case-sensitive.");
Require(catalog.ConfiguredName("CabinetSwitch") == "Switch", "A legacy Req type alias was lost.");
Require(catalog.Slots("CustomSensor").Count == 2 && catalog.SignalLimits("CustomSensor") == (1, 3),
    "Multiple Req component variants of one type lost slots or limits.");
Require(catalog.TypesMatchingSlots(["PLC_IN_InHomePos"]).Single() == "FeedSafetyDoor" &&
    catalog.TypesMatchingSlots([]).Count == 0 && catalog.TypesMatchingSlots(["Undefined"]).Count == 0,
    "Empty or incompatible live slots were treated as positive type evidence.");
document.Descendants("Slot").First().SetAttributeValue("name", "ChangedLater");
Require(catalog.Slots("FeedSafetyDoor").Single() == "PLC_IN_InHomePos", "A running scan observed mutable Req document changes.");
Console.WriteLine("PLC address and optional Req-XML regression tests passed.");

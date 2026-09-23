[CmdletBinding()]
param(
    [string]$BridgePath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BridgePath)) {
    $BridgePath = Join-Path $PSScriptRoot "..\VIBN_Tools.TiaBridge\bin\$Configuration\net48\VIBN_Tools.TiaBridge.exe"
}
$source = @'
using System.Collections.Generic;

public sealed class FakeTiaProject
{
    public FakeTiaProject()
    {
        Devices = new List<object>();
        DeviceGroups = new List<object>();
        UngroupedDevicesGroup = new FakeTiaGroup();
    }

    public List<object> Devices { get; private set; }
    public List<object> DeviceGroups { get; private set; }
    public FakeTiaGroup UngroupedDevicesGroup { get; private set; }
}

public sealed class FakeTiaPortal
{
    public FakeTiaPortal()
    {
        Projects = new List<object>();
        LocalSessions = new List<object>();
    }

    public List<object> Projects { get; private set; }
    public List<object> LocalSessions { get; private set; }
}

public sealed class FakeLocalSession
{
    public FakeLocalSession(object project)
    {
        Name = "LargeProjectLocalSession";
        Project = project;
    }

    public string Name { get; private set; }
    public object Project { get; private set; }
}

public sealed class FakeTiaGroup
{
    public FakeTiaGroup()
    {
        Devices = new List<object>();
        Groups = new List<object>();
    }

    public List<object> Devices { get; private set; }
    public List<object> Groups { get; private set; }
}

public sealed class FakeTiaDevice
{
    public FakeTiaDevice()
    {
        Name = string.Empty;
        TypeName = "Device";
        TypeIdentifier = string.Empty;
        IpAddress = string.Empty;
        PnDeviceName = string.Empty;
        FirmwareVersion = string.Empty;
        HardwareIdentifier = string.Empty;
        DeviceItems = new List<object>();
        Items = new List<object>();
    }

    public string Name { get; set; }
    public string TypeName { get; set; }
    public string TypeIdentifier { get; set; }
    public string IpAddress { get; set; }
    public string PnDeviceName { get; set; }
    public string FirmwareVersion { get; set; }
    public string HardwareIdentifier { get; set; }
    public List<object> DeviceItems { get; private set; }
    public List<object> Items { get; private set; }
}

public sealed class FakeTiaItem
{
    public FakeTiaItem()
    {
        Name = "Module";
        TypeName = "ModuleType";
        TypeIdentifier = string.Empty;
        IpAddress = string.Empty;
        PnDeviceName = string.Empty;
        FirmwareVersion = string.Empty;
        HardwareIdentifier = string.Empty;
        DeviceItems = new List<object>();
        Addresses = new List<object>();
    }

    public string Name { get; set; }
    public string TypeName { get; set; }
    public string TypeIdentifier { get; set; }
    public string IpAddress { get; set; }
    public string PnDeviceName { get; set; }
    public string FirmwareVersion { get; set; }
    public string HardwareIdentifier { get; set; }
    public int PositionNumber { get; set; }
    public List<object> DeviceItems { get; private set; }
    public List<object> Addresses { get; private set; }
}

public sealed class FakeTiaAddress
{
    public FakeTiaAddress()
    {
        IoType = "Input";
    }

    public string IoType { get; set; }
    public int StartAddress { get; set; }
    public int Length { get; set; }
}
'@

Add-Type -TypeDefinition $source -Language CSharp

function New-FakeDevice([string]$Name, [int]$InputAddress) {
    $device = [FakeTiaDevice]::new()
    $device.Name = $Name
    $item = [FakeTiaItem]::new()
    $item.Name = "$Name-Module"
    $item.PositionNumber = 1
    $address = [FakeTiaAddress]::new()
    $address.StartAddress = $InputAddress
    # Siemens Openness Address.Length is measured in bits.
    $address.Length = 32
    $item.Addresses.Add($address)
    $device.DeviceItems.Add($item)
    return $device
}

function New-FallbackDevice([string]$Name, [int]$OutputAddress) {
    $device = [FakeTiaDevice]::new()
    $device.Name = $Name
    $item = [FakeTiaItem]::new()
    $item.Name = "$Name-OutputModule"
    $item.PositionNumber = 2
    $address = [FakeTiaAddress]::new()
    $address.IoType = 'Output'
    $address.StartAddress = $OutputAddress
    $address.Length = 48
    $item.Addresses.Add($address)
    $device.Items.Add($item)
    return $device
}

function New-PnPnDevice() {
    $device = [FakeTiaDevice]::new()
    # Some TIA projects expose the root station only as a generic rack name.
    # The reader must still promote the actual device-head name below it.
    # Keep the generated C# fixture ASCII-compatible so the test behaves the
    # same under Windows PowerShell 5.1 and PowerShell 7.
    $device.Name = 'Baugruppentraeger'
    $device.TypeName = 'GSD device'
    $device.TypeIdentifier = 'GSDML-V2.35-SIEMENS-PNPNIOC-20200924.XML'

    # Real GSD stations can expose the device head/network interface in a
    # sibling branch next to the rack containing the addressed modules.
    $rack = [FakeTiaItem]::new()
    $rack.Name = 'Baugruppentraeger'
    $rack.TypeName = 'Rack'
    $rack.PositionNumber = 0

    $head = [FakeTiaItem]::new()
    $head.Name = 'PN-PN-Coupler_1'
    $head.TypeName = 'PN/PN Coupler X2'
    $head.TypeIdentifier = 'GSD:TEST/HM_DAP_X2_V3_0'
    $head.FirmwareVersion = 'V3.0'
    $head.PositionNumber = 0

    $interface = [FakeTiaItem]::new()
    $interface.Name = 'PN/PN Coupler Interface'
    $interface.TypeName = 'Interface'
    $interface.IpAddress = '192.168.0.3'
    $interface.PnDeviceName = 'pn-pn-coupler-x2'
    $interface.PositionNumber = 0

    $slotOne = [FakeTiaItem]::new()
    $slotOne.Name = 'PROFIsafe IN/OUT 12 Byte / 6 Byte_1'
    $slotOne.PositionNumber = 1

    $safeTwelveSix = [FakeTiaItem]::new()
    $safeTwelveSix.Name = 'PROFIsafe IN/OUT 12 Byte / 6 Byte'
    $safeTwelveSix.TypeName = 'PROFIsafe IN/OUT 12 Byte / 6 Byte'
    $safeTwelveSix.PositionNumber = 0
    $safeTwelveSix.HardwareIdentifier = 'HW-6201'
    $inputOne = [FakeTiaAddress]::new()
    $inputOne.IoType = 'Input'
    $inputOne.StartAddress = 62
    $inputOne.Length = 96
    $outputOne = [FakeTiaAddress]::new()
    $outputOne.IoType = 'Output'
    $outputOne.StartAddress = 62
    $outputOne.Length = 48
    $safeTwelveSix.Addresses.Add($inputOne)
    $safeTwelveSix.Addresses.Add($outputOne)

    $slotTwo = [FakeTiaItem]::new()
    $slotTwo.Name = 'PROFIsafe IN/OUT 6 Byte / 12 Byte_1'
    $slotTwo.PositionNumber = 2

    $safeSixTwelve = [FakeTiaItem]::new()
    $safeSixTwelve.Name = 'PROFIsafe IN/OUT 6 Byte / 12 Byte'
    $safeSixTwelve.TypeName = 'PROFIsafe IN/OUT 6 Byte / 12 Byte'
    $safeSixTwelve.PositionNumber = 0
    $safeSixTwelve.HardwareIdentifier = 'HW-7402'
    $inputTwo = [FakeTiaAddress]::new()
    $inputTwo.IoType = 'Input'
    $inputTwo.StartAddress = 74
    $inputTwo.Length = 48
    $outputTwo = [FakeTiaAddress]::new()
    $outputTwo.IoType = 'Output'
    $outputTwo.StartAddress = 68
    $outputTwo.Length = 96
    $safeSixTwelve.Addresses.Add($inputTwo)
    $safeSixTwelve.Addresses.Add($outputTwo)

    $slotOne.DeviceItems.Add($safeTwelveSix)
    $slotTwo.DeviceItems.Add($safeSixTwelve)
    # A second proxy/path with identical semantic data must not create a row.
    $duplicate = [FakeTiaItem]::new()
    $duplicate.Name = $safeTwelveSix.Name
    $duplicate.TypeName = $safeTwelveSix.TypeName
    $duplicate.PositionNumber = $safeTwelveSix.PositionNumber
    $duplicateInput = [FakeTiaAddress]::new()
    $duplicateInput.IoType = 'Input'
    $duplicateInput.StartAddress = 62
    $duplicateInput.Length = 96
    $duplicateOutput = [FakeTiaAddress]::new()
    $duplicateOutput.IoType = 'Output'
    $duplicateOutput.StartAddress = 62
    $duplicateOutput.Length = 48
    $duplicate.Addresses.Add($duplicateInput)
    $duplicate.Addresses.Add($duplicateOutput)
    # The same physical area is also reachable below the logical interface,
    # but without the rack slot metadata. The reader must keep only the richer
    # slot-aware representation.
    $interface.DeviceItems.Add($duplicate)

    $rack.DeviceItems.Add($slotOne)
    $rack.DeviceItems.Add($slotTwo)
    $head.DeviceItems.Add($interface)
    $device.DeviceItems.Add($rack)
    $device.DeviceItems.Add($head)
    return $device
}

$project = [FakeTiaProject]::new()
$project.Devices.Add((New-FakeDevice 'RootPLC' 0))
$group = [FakeTiaGroup]::new()
$group.Devices.Add((New-FakeDevice 'GroupedDevice' 10))
$subgroup = [FakeTiaGroup]::new()
$subgroup.Devices.Add((New-FakeDevice 'NestedDevice' 20))
$group.Groups.Add($subgroup)
$project.DeviceGroups.Add($group)
$project.UngroupedDevicesGroup.Devices.Add((New-FakeDevice 'UngroupedDevice' 30))
$project.UngroupedDevicesGroup.Devices.Add((New-FallbackDevice 'FallbackDevice' 40))
$pnPnDevice = New-PnPnDevice
$project.UngroupedDevicesGroup.Devices.Add($pnPnDevice)
# Same logical device through another Openness proxy collection.
$project.DeviceGroups[0].Devices.Add((New-PnPnDevice))

$resolvedBridge = (Resolve-Path -LiteralPath $BridgePath).Path
$bridge = [Reflection.Assembly]::LoadFrom($resolvedBridge)
$readerType = $bridge.GetType('VIBN_Tools.TiaBridge.Openness.TiaHardwareReader', $true)
$reader = [Activator]::CreateInstance($readerType, [object[]]@([FakeTiaProject].Assembly))
$rows = @($readerType.GetMethod('Read').Invoke($reader, [object[]]@($project, 0)))

if ($rows.Count -ne 7) {
    throw "Sieben adressführende Hardwarezeilen erwartet, aber $($rows.Count) erhalten."
}

$names = @($rows | ForEach-Object DeviceName)
foreach ($expected in @('RootPLC', 'GroupedDevice', 'NestedDevice', 'UngroupedDevice', 'FallbackDevice')) {
    if ($expected -notin $names) {
        throw "Gerät '$expected' fehlt in der Traversierung."
    }
}

$inputStarts = @($rows | Where-Object InputStartByte -ge 0 | ForEach-Object InputStartByte | Sort-Object)
if (($inputStarts -join ',') -ne '0,10,20,30,62,74') {
    throw "Unerwartete Eingangsadressen: $($inputStarts -join ',')"
}

$fallbackRow = $rows | Where-Object DeviceName -eq 'FallbackDevice' | Select-Object -First 1
if ($null -eq $fallbackRow -or $fallbackRow.OutputStartByte -ne 40 -or $fallbackRow.OutputLength -ne 6) {
    throw 'Items-Fallback oder Ausgangsadressen wurden nicht korrekt gelesen.'
}

$pnPnRows = @($rows | Where-Object DeviceName -eq 'PN-PN-Coupler_1' | Sort-Object Slot)
if ($pnPnRows.Count -ne 2) {
    $actualDevices = @($rows | ForEach-Object { "[$($_.DeviceIndex)] $($_.DeviceName) / $($_.ModuleName)" }) -join '; '
    throw "Exakt zwei semantisch eindeutige PN/PN-Zeilen erwartet, aber $($pnPnRows.Count) erhalten. Gelesen: $actualDevices"
}

$firstSafe = $pnPnRows[0]
if ($firstSafe.DeviceType -ne 'PN/PN Coupler X2' -or
    $firstSafe.TraversalIndex -lt 1 -or $firstSafe.HierarchyDepth -ne 2 -or
    $firstSafe.ParentName -ne 'PROFIsafe IN/OUT 12 Byte / 6 Byte_1' -or
    $firstSafe.ObjectClass -notmatch 'FakeTiaItem' -or
    $firstSafe.HardwareIdentifier -ne 'HW-6201' -or
    $firstSafe.ModulePath -ne 'Baugruppentraeger/PROFIsafe IN/OUT 12 Byte / 6 Byte_1/PROFIsafe IN/OUT 12 Byte / 6 Byte' -or
    $firstSafe.IpAddress -ne '192.168.0.3' -or
    $firstSafe.ProfinetName -ne 'pn-pn-coupler-x2' -or
    $firstSafe.FirmwareVersion -ne 'V3.0' -or
    $firstSafe.Slot -ne 1 -or $firstSafe.Subslot -ne 0 -or
    $firstSafe.InputStartByte -ne 62 -or $firstSafe.InputLengthBits -ne 96 -or
    $firstSafe.InputLength -ne 12 -or $firstSafe.InputEndByte -ne 73 -or
    $firstSafe.OutputStartByte -ne 62 -or $firstSafe.OutputLengthBits -ne 48 -or
    $firstSafe.OutputLength -ne 6 -or $firstSafe.OutputEndByte -ne 67) {
    throw "Erster PROFIsafe-Bereich falsch: Gerät='$($firstSafe.DeviceName)', Typ='$($firstSafe.DeviceType)', IP='$($firstSafe.IpAddress)', PN='$($firstSafe.ProfinetName)', FW='$($firstSafe.FirmwareVersion)', Slot=$($firstSafe.Slot)/$($firstSafe.Subslot), Index=$($firstSafe.TraversalIndex), Tiefe=$($firstSafe.HierarchyDepth), Parent='$($firstSafe.ParentName)', Klasse='$($firstSafe.ObjectClass)', HW='$($firstSafe.HardwareIdentifier)', Pfad='$($firstSafe.ModulePath)', E=$($firstSafe.InputStartByte)/$($firstSafe.InputLengthBits)/$($firstSafe.InputLength)/$($firstSafe.InputEndByte), A=$($firstSafe.OutputStartByte)/$($firstSafe.OutputLengthBits)/$($firstSafe.OutputLength)/$($firstSafe.OutputEndByte)."
}

$secondSafe = $pnPnRows[1]
if ($secondSafe.Slot -ne 2 -or $secondSafe.Subslot -ne 0 -or
    $secondSafe.InputStartByte -ne 74 -or $secondSafe.InputLengthBits -ne 48 -or
    $secondSafe.InputLength -ne 6 -or $secondSafe.InputEndByte -ne 79 -or
    $secondSafe.OutputStartByte -ne 68 -or $secondSafe.OutputLengthBits -ne 96 -or
    $secondSafe.OutputLength -ne 12 -or $secondSafe.OutputEndByte -ne 79) {
    throw 'Zweiter PROFIsafe-Bereich wurde nicht als E 74–79 / A 68–79 ausgewertet.'
}

$portal = [FakeTiaPortal]::new()
$portal.LocalSessions.Add([FakeLocalSession]::new($project))
$sessionType = $bridge.GetType('VIBN_Tools.TiaBridge.Openness.TiaOpennessSession', $true)
$probeMethod = $sessionType.GetMethod('ProbeOpenProject', [Reflection.BindingFlags]'Static,NonPublic')
$probe = $probeMethod.Invoke($null, [object[]]@($portal))
$probeType = $probe.GetType()
$probeProject = $probeType.GetProperty('Project').GetValue($probe)
$probeDiagnostics = [string]$probeType.GetProperty('Diagnostics').GetValue($probe)
if ($null -eq $probeProject -or $probeDiagnostics -notmatch 'LocalSessions=1' -or $probeDiagnostics -notmatch 'LargeProjectLocalSession') {
    throw "Multiuser-Local-Session oder Attach-Diagnose unvollständig: $probeDiagnostics"
}

Write-Host "TIA-Traversierung erfolgreich: $($names -join ', '); PN/PN E62-73/A62-67 und E74-79/A68-79"

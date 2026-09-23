using System.Text.RegularExpressions;

namespace VIBN_Tools.Tia.Contracts;

public sealed class EmptyPayload
{
    public static EmptyPayload Instance { get; } = new();

    private EmptyPayload()
    {
    }
}

public sealed class TiaVersionPayload
{
    public string Version { get; set; } = string.Empty;
}

public sealed class TiaPlcSelectionPayload
{
    public int PlcIndex { get; set; }
}

public sealed class TiaFolderPayload
{
    public string ParentPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class TiaTransferPayload
{
    public string FolderPath { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
}

public sealed class TiaPlcInfo
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public string TypeIdentifier { get; set; } = string.Empty;
}

public sealed class TiaProjectTree
{
    public List<TiaFolderInfo> Folders { get; set; } = new();

    public List<TiaProgramItemInfo> Items { get; set; } = new();
}

public sealed class TiaFolderInfo
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

public sealed class TiaProgramItemInfo
{
    public string Name { get; set; } = string.Empty;

    public string FolderPath { get; set; } = string.Empty;
}

public sealed class TiaAxisInfo
{
    /// <summary>Stable identity composed from the technology-group path and object name.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TechnologyType { get; set; } = string.Empty;

    public string GroupPath { get; set; } = string.Empty;

    /// <summary>Only populated by the configuration command.</summary>
    public List<TiaAxisParameterResult> ParameterResults { get; set; } = new();
}

public sealed class TiaAxisConfigurationPayload
{
    public List<string> AxisIds { get; set; } = new();
}

public sealed class TiaAxisParameterResult
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Single source of truth for the parameters changed by the explicit axis
/// configuration command. All values are integer Openness parameter values.
/// </summary>
public static class TiaAxisConfigurationPolicy
{
    public static IReadOnlyDictionary<string, int> CreateParameterValues(string axisName)
    {
        var motionType = IsLinearAxis(axisName) ? 0 : 1;
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["_Properties.MotionType"] = motionType,
            ["Modulo.Enable"] = 0,
            ["Actor.DataAdaption"] = 0,
            ["Sensor[1].DataAdaption"] = 0,
            ["Sensor[1].MountingMode"] = motionType,
            ["Simulation.Mode"] = 1,
            ["Sensor[1].Type"] = 2,
            ["TorqueLimiting.PositionBasedMonitorings"] = 0,
            ["FollowingError.EnableMonitoring"] = 0,
            ["PositionControl.EnableDSC"] = 0
        };
    }

    public static bool IsLinearAxis(string axisName)
    {
        var value = (axisName ?? string.Empty).Trim();
        return Regex.IsMatch(
                   value,
                   @"(?:^|[_\-\s])(?:X|Y|Z)(?:$|[_\-\s])",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   value,
                   @"(?:AXIS|ACHSE)[_\-\s]*(?:X|Y|Z)\d*$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// Read-only hardware/module information discovered through TIA Openness.
/// Start addresses are byte offsets. Siemens Openness reports
/// <c>Address.Length</c> in bits, therefore the DTO keeps both the unmodified
/// bit length and the rounded-up byte length used by the UI and Special Device
/// generation. A negative start value means that the module has no address of
/// that IO type.
/// </summary>
public sealed class TiaHardwareModuleInfo
{
    public int TraversalIndex { get; set; }

    public int DeviceIndex { get; set; }

    public int HierarchyDepth { get; set; }

    public string ParentName { get; set; } = string.Empty;

    public string ObjectClass { get; set; } = string.Empty;

    public string HardwareIdentifier { get; set; } = string.Empty;

    public int Slot { get; set; } = -1;

    public string DeviceName { get; set; } = string.Empty;

    public string DeviceType { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string OrderNumber { get; set; } = string.Empty;

    public string GsdName { get; set; } = string.Empty;

    public string GsdType { get; set; } = string.Empty;

    public string ProfinetName { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public string NetworkRole { get; set; } = string.Empty;

    public string ModuleName { get; set; } = string.Empty;

    public string ModulePath { get; set; } = string.Empty;

    public string ModuleType { get; set; } = string.Empty;

    public string TypeIdentifier { get; set; } = string.Empty;

    public string FirmwareVersion { get; set; } = string.Empty;

    public int Subslot { get; set; } = -1;

    /// <summary>
    /// Zero-based ordinal of the input/output range pair within one DeviceItem.
    /// It distinguishes multiple, separate address areas without merging them.
    /// </summary>
    public int AddressSetIndex { get; set; }

    public int InputStartByte { get; set; } = -1;

    /// <summary>Raw <c>Address.Length</c> value reported by Openness, in bits.</summary>
    public int InputLengthBits { get; set; }

    /// <summary>Input size rounded up to complete bytes.</summary>
    public int InputLength { get; set; }

    public int InputEndByte => InputStartByte >= 0 && InputLength > 0
        ? InputStartByte + InputLength - 1
        : -1;

    public string InputAddressRange => FormatAddressRange(InputStartByte, InputEndByte);

    public int OutputStartByte { get; set; } = -1;

    /// <summary>Raw <c>Address.Length</c> value reported by Openness, in bits.</summary>
    public int OutputLengthBits { get; set; }

    /// <summary>Output size rounded up to complete bytes.</summary>
    public int OutputLength { get; set; }

    public int OutputEndByte => OutputStartByte >= 0 && OutputLength > 0
        ? OutputStartByte + OutputLength - 1
        : -1;

    public string OutputAddressRange => FormatAddressRange(OutputStartByte, OutputEndByte);

    private static string FormatAddressRange(int start, int end) => start < 0
        ? "—"
        : end > start ? $"{start}–{end}" : start.ToString();
}

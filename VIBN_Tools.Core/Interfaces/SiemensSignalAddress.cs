using System.Globalization;

namespace VIBN_Tools.Core.Interfaces;

/// <summary>A bounded PLC byte/bit range. A/E and Q/I refer to the same respective areas.</summary>
public sealed record SiemensSignalAddress(char Area, int StartByte, int Length, int? Bit = null)
{
    public bool Overlaps(SiemensSignalAddress other)
    {
        if (Area != other.Area) return false;
        if (Bit.HasValue && other.Bit.HasValue) return StartByte == other.StartByte && Bit == other.Bit;
        return StartByte <= (long)other.StartByte + other.Length - 1 && other.StartByte <= (long)StartByte + Length - 1;
    }

    public static bool TryParse(string? address, string? dataType, out SiemensSignalAddress? parsed, out string? diagnostic)
    {
        parsed = null; diagnostic = null;
        var value = address?.Trim().ToUpperInvariant() ?? "";
        if (value.StartsWith('%')) value = value[1..];
        if (value.StartsWith('P') && value.Length > 1 && "AEIQ".Contains(value[1])) value = value[1..];
        if (value.Length < 2 || !"AEIQM".Contains(value[0]) || value.StartsWith("AXIS", StringComparison.Ordinal)) return false;
        var area = value[0] switch { 'A' => 'Q', 'E' => 'I', var key => key };
        var remainder = value[1..];
        var width = remainder[0] switch { 'B' => 1, 'W' => 2, 'D' => 4, 'L' => 8, _ => 0 };
        var explicitBit = remainder.StartsWith('X');
        if (width > 0 || explicitBit) remainder = remainder[1..];
        else if (!char.IsAsciiDigit(remainder[0])) return false; // Robot, axis and symbolic addresses are not PLC byte ranges.
        var parts = remainder.Split('.');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return Invalid(address, "ungültiger Byte-Offset", out diagnostic);
        if (parts.Length == 2)
        {
            if (width > 0 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bit) || bit > 7)
                return Invalid(address, "Bitadresse muss einen Bit-Offset von 0 bis 7 besitzen", out diagnostic);
            parsed = new(area, start, 1, bit); return true;
        }
        if (explicitBit) return Invalid(address, "Bit-Offset fehlt", out diagnostic);
        if (width == 0)
        {
            width = dataType?.Trim().ToUpperInvariant() switch
            {
                "BYTE" or "SINT" or "USINT" => 1,
                "WORD" or "INT" or "UINT" => 2,
                "DWORD" or "DINT" or "UDINT" or "REAL" or "SINGLE" => 4,
                "LWORD" or "LINT" or "ULINT" or "LREAL" or "DOUBLE" => 8,
                _ => 0,
            };
            if (width == 0) return Invalid(address, "Breite ist ohne B/W/D/L oder Bit-Offset nicht eindeutig; Datentyp und Adresse prüfen", out diagnostic);
        }
        parsed = new(area, start, width); return true;
    }

    private static bool Invalid(string? address, string reason, out string? diagnostic)
    {
        diagnostic = $"Adressformat '{address}' kann nicht auf Überschneidungen geprüft werden: {reason}.";
        return false;
    }
}

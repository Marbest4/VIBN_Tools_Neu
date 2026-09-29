using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace VIBN_Tools.Rockwell;

public sealed record AllenBradleyInterfaceRow(
    string Tag,
    string Comment,
    string Usage);

public sealed record AllenBradleyInterfaceExportResult(
    string FilePath,
    int ExportedSignalCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reimplements the Allen-Bradley interface export from InterfaceCreator_Rockwell
/// without Office COM automation. The source L5X remains read-only and the
/// generated workbook keeps the legacy InterfaceSimExport column contract.
/// </summary>
public static partial class AllenBradleyInterfaceExcelExporter
{
    private static readonly string[] DefaultMemberNames = ["D", "PtStatus", "VS"];
    private static readonly string[] Headers =
    [
        "Is Valid", "Tag", "Type", "Comment", "Usage", "Cycle", "Value", "References"
    ];

    private static readonly HashSet<string> SupportedLogixTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL"
    };

    public static AllenBradleyInterfaceExportResult Export(
        string l5xPath,
        string targetPath,
        IEnumerable<string>? memberNames = null)
    {
        var rows = ReadRows(l5xPath, memberNames, out var warnings);
        if (rows.Count == 0)
        {
            throw new InvalidDataException(
                "Die L5X-Datei enthält keine passenden Allen-Bradley-Schnittstellensignale " +
                "für die Member D*, PtStatus* oder VS*.");
        }

        var fullTargetPath = NormalizeTargetPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Der Zielpfad für die Excel-Schnittstelle ist ungültig.");
        Directory.CreateDirectory(directory);

        using var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("InterfaceSimExport");
        WriteHeader(workbook, sheet);
        for (var index = 0; index < rows.Count; index++)
            WriteRow(sheet.CreateRow(index + 1), rows[index]);
        sheet.CreateFreezePane(0, 1);
        sheet.AutoSizeColumn(0);
        sheet.SetColumnWidth(1, Math.Min(255 * 256, Math.Max(30 * 256, sheet.GetColumnWidth(1))));
        sheet.SetColumnWidth(3, Math.Min(255 * 256, Math.Max(40 * 256, sheet.GetColumnWidth(3))));

        var temporaryPath = fullTargetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                workbook.Write(stream, leaveOpen: false);
            File.Move(temporaryPath, fullTargetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new AllenBradleyInterfaceExportResult(fullTargetPath, rows.Count, warnings);
    }

    public static IReadOnlyList<AllenBradleyInterfaceRow> ReadRows(
        string l5xPath,
        IEnumerable<string>? memberNames,
        out IReadOnlyList<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(l5xPath))
            throw new ArgumentException("Eine Allen-Bradley-L5X-Datei muss ausgewählt werden.", nameof(l5xPath));
        if (!File.Exists(l5xPath))
            throw new FileNotFoundException("Die ausgewählte Allen-Bradley-L5X-Datei wurde nicht gefunden.", l5xPath);
        if (!string.Equals(Path.GetExtension(l5xPath), ".l5x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Allen-Bradley-Quelldatei muss die Erweiterung .L5X besitzen.");

        XDocument document;
        try
        {
            document = XDocument.Load(l5xPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Die L5X-Datei ist kein gültiges XML (Zeile {exception.LineNumber}, Position {exception.LinePosition}).",
                exception);
        }

        var controller = document.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Controller", StringComparison.Ordinal));
        if (controller is null)
            throw new InvalidDataException("Die L5X-Datei enthält keinen Controller-Knoten.");

        var requestedMembers = (memberNames ?? DefaultMemberNames)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedMembers.Count == 0)
            throw new ArgumentException("Mindestens ein zu suchender Allen-Bradley-Membername ist erforderlich.", nameof(memberNames));

        var diagnostics = new List<string>();
        var programTags = DirectChild(controller, "Programs")?
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Program", StringComparison.Ordinal))
            .SelectMany(program => DirectChild(program, "Tags")?.Elements() ?? Enumerable.Empty<XElement>())
            .Where(element => string.Equals(element.Name.LocalName, "Tag", StringComparison.Ordinal))
            .ToArray() ?? [];
        var controllerTags = DirectChild(controller, "Tags")?
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Tag", StringComparison.Ordinal))
            .Where(IsSourceTag)
            .OrderBy(tag => GroupOrder(Attribute(tag, "Name")))
            .ThenBy(tag => Attribute(tag, "Name"), StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var result = new List<AllenBradleyInterfaceRow>();
        foreach (var tag in controllerTags)
            ReadTag(tag, requestedMembers, programTags, result, diagnostics);

        var deduplicated = result
            .GroupBy(row => row.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var preferred = group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Comment)) ?? group.First();
                if (group.Count() > 1)
                    diagnostics.Add($"Doppeltes L5X-Signal '{group.Key}' wurde nur einmal exportiert.");
                return preferred;
            })
            .ToArray();
        warnings = diagnostics.Distinct(StringComparer.Ordinal).ToArray();
        return deduplicated;
    }

    private static void ReadTag(
        XElement root,
        IReadOnlySet<string> requestedMembers,
        IReadOnlyList<XElement> programTags,
        ICollection<AllenBradleyInterfaceRow> result,
        ICollection<string> warnings) =>
        Traverse(root, [], requestedMembers, programTags, result, warnings);

    private static void Traverse(
        XElement element,
        IReadOnlyList<string> path,
        IReadOnlySet<string> requestedMembers,
        IReadOnlyList<XElement> programTags,
        ICollection<AllenBradleyInterfaceRow> result,
        ICollection<string> warnings)
    {
        var name = Attribute(element, "Name");
        var currentPath = string.IsNullOrWhiteSpace(name) ? path : [.. path, name];
        var children = element.Elements().ToArray();
        if (children.Length > 0)
        {
            foreach (var child in children)
                Traverse(child, currentPath, requestedMembers, programTags, result, warnings);
            return;
        }

        if (element.Parent is null || string.IsNullOrWhiteSpace(name))
            return;
        var parentName = Attribute(element.Parent, "Name");
        var ioGroup = RemoveDigits(parentName);
        if (!string.Equals(ioGroup, "I", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ioGroup, "O", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedMember = RemoveDigits(name);
        if (string.Equals(normalizedMember, name, StringComparison.Ordinal) ||
            !requestedMembers.Contains(normalizedMember))
        {
            return;
        }

        var dataType = Attribute(element, "DataType");
        if (!SupportedLogixTypes.Contains(dataType))
        {
            warnings.Add($"Nicht unterstützter Logix-Datentyp '{dataType}' bei '{string.Join('.', currentPath)}' wurde übersprungen.");
            return;
        }

        var tagPath = string.Join('.', currentPath);
        result.Add(new AllenBradleyInterfaceRow(
            tagPath,
            ResolveComment(tagPath, programTags),
            string.Equals(ioGroup, "I", StringComparison.OrdinalIgnoreCase) ? "Write" : "Read"));
    }

    private static string ResolveComment(string alias, IReadOnlyList<XElement> programTags)
    {
        var separator = alias.IndexOf('.');
        if (separator <= 0)
            return string.Empty;
        var firstName = alias[..separator];
        var operand = alias[separator..];
        var comment = FindComment(programTags, firstName, operand);
        if (!string.IsNullOrWhiteSpace(comment) || !operand.Contains("Safety", StringComparison.Ordinal))
            return comment;

        var safetyIndex = operand.IndexOf(".Safety", StringComparison.Ordinal);
        var safetyOperand = safetyIndex >= 0
            ? ".SAFETY" + operand[(safetyIndex + ".Safety".Length)..]
            : operand.Replace("Safety", "SAFETY", StringComparison.Ordinal);
        return FindComment(programTags, firstName, safetyOperand);
    }

    private static string FindComment(
        IEnumerable<XElement> programTags,
        string firstName,
        string operand)
    {
        foreach (var programTag in programTags.Where(tag =>
                     string.Equals(Attribute(tag, "AliasFor"), firstName, StringComparison.Ordinal)))
        {
            var match = programTag.DescendantsAndSelf().FirstOrDefault(element =>
                string.Equals(Attribute(element, "Operand"), operand, StringComparison.Ordinal));
            var value = match?.Value.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "SPARE", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return string.Empty;
    }

    private static void WriteHeader(IWorkbook workbook, ISheet sheet)
    {
        var style = workbook.CreateCellStyle();
        var font = workbook.CreateFont();
        font.IsBold = true;
        style.SetFont(font);
        var row = sheet.CreateRow(0);
        for (var index = 0; index < Headers.Length; index++)
        {
            var cell = row.CreateCell(index);
            cell.SetCellValue(Headers[index]);
            cell.CellStyle = style;
        }
    }

    private static void WriteRow(IRow target, AllenBradleyInterfaceRow source)
    {
        var values = new[]
        {
            "WAHR", source.Tag, "BOOL", source.Comment, source.Usage, "Continous", "FALSCH", "0"
        };
        for (var index = 0; index < values.Length; index++)
            target.CreateCell(index).SetCellValue(values[index]);
    }

    private static bool IsSourceTag(XElement tag)
    {
        var name = Attribute(tag, "Name");
        if (string.Equals(name, "Debug", StringComparison.Ordinal) ||
            name.Contains("_Standard", StringComparison.Ordinal))
        {
            return false;
        }
        var description = tag.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Description", StringComparison.Ordinal));
        return !string.Equals(description?.Value.Trim(), "Simulation", StringComparison.Ordinal);
    }

    private static int GroupOrder(string name)
    {
        if (name.Contains("SBK", StringComparison.Ordinal))
            return 1;
        if (name.Contains("BK", StringComparison.Ordinal))
            return 0;
        if (name.Contains("PM", StringComparison.Ordinal))
            return 2;
        return 3;
    }

    private static string NormalizeTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Eine Zieldatei für die Allen-Bradley-Excel-Schnittstelle ist erforderlich.", nameof(path));
        var withExtension = string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".xlsx";
        return Path.GetFullPath(withExtension);
    }

    private static XElement? DirectChild(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal));

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))?.Value ?? string.Empty;

    private static string RemoveDigits(string value) => DigitsRegex().Replace(value, string.Empty);

    [GeneratedRegex("[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsRegex();
}

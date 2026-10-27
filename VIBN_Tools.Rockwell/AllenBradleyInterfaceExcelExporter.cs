using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace VIBN_Tools.Rockwell;

public sealed record AllenBradleyInterfaceRow(
    string Tag,
    string Comment,
    string Usage,
    string DataType = "BOOL");

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
        IEnumerable<string>? memberNames = null,
        bool includeSafetySignals = false)
    {
        var rows = ReadRows(l5xPath, memberNames, out var warnings, includeSafetySignals);
        if (rows.Count == 0)
        {
            var message = "Die L5X-Datei enthält keine passenden Allen-Bradley-Schnittstellensignale " +
                (includeSafetySignals
                    ? "für die gewählten Member oder Safety-Ein-/Ausgänge."
                    : "für die gewählten Member. Für Safety-Ein-/Ausgänge 'Safety-Signale einschließen' aktivieren.");
            if (warnings.Count > 0)
                message += Environment.NewLine + string.Join(Environment.NewLine, warnings.Take(5));
            throw new InvalidDataException(message);
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
        out IReadOnlyList<string> warnings,
        bool includeSafetySignals = false)
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
        var allControllerTags = DirectChild(controller, "Tags")?
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Tag", StringComparison.Ordinal))
            .ToArray() ?? [];
        var comments = BuildCommentLookup(allControllerTags.Concat(programTags));
        var controllerTags = allControllerTags
            .Where(IsSourceTag)
            .OrderBy(tag => GroupOrder(Attribute(tag, "Name")))
            .ThenBy(tag => Attribute(tag, "Name"), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new List<AllenBradleyInterfaceRow>();
        foreach (var tag in controllerTags)
            Traverse(tag, [], requestedMembers, comments, result, diagnostics, includeSafetySignals);
        if (includeSafetySignals)
            ReadSafetyModuleRows(controller, requestedMembers, comments, result, diagnostics);

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

    private static void Traverse(
        XElement element,
        IReadOnlyList<string> path,
        IReadOnlySet<string> requestedMembers,
        IReadOnlyDictionary<string, string> comments,
        ICollection<AllenBradleyInterfaceRow> result,
        ICollection<string> warnings,
        bool includeSafetySignals,
        bool isSafety = false,
        string? usage = null)
    {
        var name = Attribute(element, "Name");
        var currentPath = string.IsNullOrWhiteSpace(name) ? path : [.. path, name];
        var index = Attribute(element, "Index");
        if (element.Name.LocalName == "Element" && index.Length > 0 && currentPath.Count > 0)
            currentPath = [.. currentPath.Take(currentPath.Count - 1), currentPath[^1] + index];
        isSafety |= IsSafetyElement(element);
        var ioGroup = RemoveDigits(name);
        var safetyIoGroup = ioGroup.ToUpperInvariant() switch
        {
            "SI" or "SAFEINPUT" or "SAFEINPUTS" or "SAFETYINPUT" or "SAFETYINPUTS" => "I",
            "SO" or "SAFEOUTPUT" or "SAFEOUTPUTS" or "SAFETYOUTPUT" or "SAFETYOUTPUTS" => "O",
            _ => string.Empty
        };
        if (safetyIoGroup.Length > 0)
        {
            isSafety = true;
            ioGroup = safetyIoGroup;
        }
        if (isSafety && !includeSafetySignals)
            return;

        // Once the direction is known, carry it through nested structures.
        // A Safety subtree may use Input(s)/Output(s) instead of I1/O1.
        if (string.Equals(ioGroup, "I", StringComparison.OrdinalIgnoreCase) ||
            (isSafety && (string.Equals(ioGroup, "Input", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(ioGroup, "Inputs", StringComparison.OrdinalIgnoreCase))))
            usage = "Write";
        else if (string.Equals(ioGroup, "O", StringComparison.OrdinalIgnoreCase) ||
                 (isSafety && (string.Equals(ioGroup, "Output", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ioGroup, "Outputs", StringComparison.OrdinalIgnoreCase))))
            usage = "Read";

        var children = element.Elements().ToArray();
        if (children.Length > 0)
        {
            foreach (var child in children)
                Traverse(child, currentPath, requestedMembers, comments, result, warnings,
                    includeSafetySignals, isSafety, usage);
            return;
        }

        if (element.Name.LocalName is not ("DataValueMember" or "DataValue" or "Element"))
            return;
        var memberName = name.Length > 0 ? name : element.Ancestors()
            .Select(parent => Attribute(parent, "Name")).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        if (!isSafety && !MatchesMember(memberName, requestedMembers))
            return;

        var tagPath = string.Join('.', currentPath);
        if (usage is null)
        {
            if (isSafety)
                warnings.Add($"Safety-Signal '{tagPath}' wurde übersprungen: keine eindeutige Ein-/Ausgangsstruktur (I/O, SI/SO, Inputs/Outputs) vorhanden.");
            return;
        }

        var dataType = Attribute(element, "DataType");
        if (dataType.Length == 0 && element.Name.LocalName == "Element" && element.Parent is not null)
            dataType = Attribute(element.Parent, "DataType");
        if (!SupportedLogixTypes.Contains(dataType))
        {
            warnings.Add($"Nicht unterstützter Logix-Datentyp '{dataType}' bei '{string.Join('.', currentPath)}' wurde übersprungen.");
            return;
        }

        result.Add(new AllenBradleyInterfaceRow(
            tagPath,
            comments.GetValueOrDefault(tagPath, string.Empty),
            usage,
            dataType.ToUpperInvariant()));
    }

    private static bool IsSafetyElement(XElement element)
    {
        if (string.Equals(Attribute(element, "Class"), "Safety", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Attribute(element, "Name");
        return string.Equals(name, "Safety", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Safety_", StringComparison.OrdinalIgnoreCase) ||
               (element.Name.LocalName == "Tag" && name.StartsWith("s_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesMember(string name, IReadOnlySet<string> requestedMembers) =>
        requestedMembers.Any(prefix => name.Length > prefix.Length &&
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[prefix.Length]));

    private static IReadOnlyDictionary<string, string> BuildCommentLookup(IEnumerable<XElement> commentTags)
    {
        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in commentTags)
        {
            var target = Attribute(tag, "AliasFor");
            if (string.IsNullOrWhiteSpace(target))
                target = Attribute(tag, "Name");
            if (string.IsNullOrWhiteSpace(target))
                continue;
            foreach (var element in tag.Descendants())
            {
                var operand = Attribute(element, "Operand");
                if (operand.Length == 0)
                    continue;
                var value = element.Value.Trim();
                if (value.Length > 0 && !string.Equals(value, "SPARE", StringComparison.OrdinalIgnoreCase))
                    comments.TryAdd(target + operand, value);
            }
        }
        return comments;
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
            "WAHR", source.Tag, source.DataType, source.Comment, source.Usage, "Continous",
            string.Equals(source.DataType, "BOOL", StringComparison.OrdinalIgnoreCase) ? "FALSCH" : "0", "0"
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

using System.IO;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.SharedWpf.Commands;

namespace VIBN_Tools.Application.VM;

/// <summary>A detached edit session: grouping, XML edits and cancellation do not mutate the root.</summary>
public sealed class Fee2ContainerEditVM : NotifyBase
{
    private readonly Fee2ContainerRoot _source;
    private Fee2ContainerRootEditor _editor;
    private string _xml;
    private bool _xmlDirty;
    private string _status = "Container, Signale und FEE-Objekte bearbeiten. Änderungen werden erst mit Übernehmen gespeichert.";
    public Fee2ContainerEditVM(Fee2ContainerRootSelectionVM root)
    {
        _source = root.CreateEditedRoot();
        var document = root.Editor.CreateSnapshot(includeExcluded: true).ContainerDocument;
        _editor = Fee2ContainerRootEditor.FromDocument(_source, document);
        _xml = document.ToString();
        GroupCommand = new RelayCommand(Group);
        ReadXmlCommand = new RelayCommand(() => ReadXml());
        WriteXmlCommand = new RelayCommand(WriteXml);
    }
    public Fee2ContainerRootEditor Editor => _editor;
    public IReadOnlyList<string> SupportedContainerTypes => FeeContainerLiveReconstructor.SupportedContainerTypes;
    public ContainerGenerationSettings Settings { get; } = new() { GroupByComponent = true, GroupByType = true };
    public string Xml { get => _xml; set { if (SetPropertyChange(ref _xml, value)) _xmlDirty = true; } }
    public string Status { get => _status; private set => SetPropertyChange(ref _status, value); }
    public ICommand GroupCommand { get; }
    public ICommand ReadXmlCommand { get; }
    public ICommand WriteXmlCommand { get; }

    public void WriteXml()
    {
        _xml = Editor.CreateSnapshot(includeExcluded: true).ContainerDocument.ToString();
        _xmlDirty = false;
        OnPropertyChanged(nameof(Xml));
    }
    public bool ApplyXmlIfNeeded() => !_xmlDirty || ReadXml();
    private bool ReadXml()
    {
        try
        {
            using var text = new StringReader(Xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != "CAAMergeResult" || document.Root.Element("ContainerList") is null)
                throw new InvalidDataException("CAAMergeResult mit ContainerList erwartet.");
            var invalidGuid = document.Descendants("SimObject").Any(item => !Guid.TryParse(item.Element("Guid")?.Value, out var guid) || guid == Guid.Empty) ||
                document.Descendants("Entry").Any(item => item.Attribute("feeGuid") is { } attribute && (!Guid.TryParse(attribute.Value, out var guid) || guid == Guid.Empty));
            if (invalidGuid) throw new InvalidDataException("Ungültige FEE-GUID. GUIDs vor dem Übernehmen korrigieren.");
            // An explicit rename is a manual edit, including its physical associations.
            foreach (var container in document.Descendants("Container"))
                foreach (var item in ContainerFileXml.Objects(container))
                    if (!ContainerFileXml.CanRetainObjectAssociation(item, item.Element("Name")?.Value ?? "", container.Element("Component")?.Value ?? ""))
                        item.SetAttributeValue("assignment", "Manual");
            ReplaceEditor(document);
            _xmlDirty = false;
            Status = "XML übernommen. Container, Slots, GUIDs und Exportauswahl vor dem Speichern prüfen.";
            return true;
        }
        catch (Exception exception) { Status = "XML konnte nicht übernommen werden: " + exception.Message; return false; }
    }
    private void Group()
    {
        try
        {
            if (!ApplyXmlIfNeeded()) return;
            // Excluded containers/signals stay intact and are never pulled into an exported group.
            var all = Editor.CreateSnapshot(includeExcluded: true).ContainerDocument;
            var active = Editor.CreateSnapshot().ContainerDocument;
            var grouped = FeeContainerFileGrouping.Group(active, Settings);
            var excluded = all.Descendants("Container").Where(container => container.Attribute("export")?.Value == "false").Select(item => new XElement(item)).ToArray();
            foreach (var container in all.Descendants("Container").Where(container => container.Attribute("export")?.Value != "false"))
            {
                var excludedEntries = container.Element("DataList")?.Elements("Entry").Where(entry => entry.Attribute("export")?.Value == "false").ToArray() ?? [];
                if (excludedEntries.Length == 0) continue;
                var retained = new XElement(container); retained.SetAttributeValue("id", "excluded:" + container.Attribute("id")?.Value);
                retained.SetAttributeValue("export", "false"); retained.Element("DataList")!.ReplaceNodes(excludedEntries.Select(entry => new XElement(entry)));
                retained.Element("SimObjects")?.Remove(); grouped.Root!.Element("ContainerList")!.Add(retained);
            }
            grouped.Root!.Element("ContainerList")!.Add(excluded);
            ReplaceEditor(grouped);
            WriteXml();
            Status = "Grouping-Vorschau erstellt. Bei aufgeteilten Containern bleiben FEE-Objekte zur manuellen Zuordnung in einem eigenen, vom Export ausgeschlossenen Eintrag erhalten. Bitte im Hauptfenster zuordnen. Abbrechen verwirft alle Änderungen.";
        }
        catch (Exception exception) { Status = "Grouping konnte nicht ausgeführt werden: " + exception.Message; }
    }
    private void ReplaceEditor(XDocument document)
    {
        _editor = Fee2ContainerRootEditor.FromDocument(_source, document);
        OnPropertyChanged(nameof(Editor));
    }
    public Fee2ContainerRootEditor CreateResult() => Editor;
}

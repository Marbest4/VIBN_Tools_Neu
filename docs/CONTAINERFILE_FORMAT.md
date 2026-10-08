# Gemeinsames ContainerFile-Format

Das eingebettete `ContainerGeneration/XmlSchemas/CAAResult.xsd` ist für ContainerGeneration, Container2FEE Visual und FEE2Container gemeinsam erweitert. Bestehende Dateien ohne FEE-Objekte bleiben gültig. `DataList` enthält ausschließlich Containersignale; die optionalen `SimObjects` enthalten die zugehörigen FEE-Objekte. Leere Container- und Signallisten sind zulässig, damit auch vollständige Löschstände verglichen werden können.

```xml
<CAAMergeResult version="1.0.0.0" createdAt="2026-10-07T12:00:00Z" autoCreateFile="" zuli="">
  <ContainerList>
    <Container id="axis-1">
      <Component>Axis_1</Component>
      <Type>Cylinder</Type>
      <DataList>
        <Entry feeGuid="11111111-1111-1111-1111-111111111111">
          <ID>Signal-ID</ID>
          <Address>%Q0.0</Address>
          <DataType>Bool</DataType>
          <Signal>Axis_1_ToWork</Signal>
          <Slot>PLC_OUT_ToWorkPos</Slot>
          <Note />
        </Entry>
      </DataList>
      <SimObjects>
        <SimObject>
          <Guid>22222222-2222-2222-2222-222222222222</Guid>
          <Name>Axis_1</Name>
          <FeeType>MotionJoint</FeeType>
          <Role>SimObject</Role>
          <Target>MotionJoints</Target>
          <ClrType>VIBN_Tools.GlobalClasses.FeeObjects.FeeJoint</ClrType>
          <Slots />
        </SimObject>
      </SimObjects>
    </Container>
  </ContainerList>
</CAAMergeResult>
```

`FeeType` ist der tatsächliche SDK-Typ, etwa `Surface`, `SafetySensor`, `Button` oder `MotionJoint`. `Role` unterscheidet `Primary`, `SimObject`, `TechnicalHelper` und Inventareinträge mit `Available`. `Target` benennt ein kompatibles Generatorziel; ein leeres Ziel kann beim Import nur bei eindeutig passendem Typ automatisch aufgelöst werden. Nicht zu einem Generatorziel passende, explizite Zugehörigkeiten bleiben im XML erhalten. Optional speichert `Slots/Slot` den Slotnamen und dessen `assignedGuid`. Diese Bestandsdaten ersetzen keine rekonstruierten SDK-Endpunkte.

`feeGuid` am Signaleintrag hält eine konkrete Variablenzuordnung fest. Ohne GUID wird das Signal anhand seiner fachlichen Identität aufgelöst beziehungsweise vom Generator erzeugt. Optional enthält `FeeInventory/SimObjects` die verfügbaren FEE-Objekte und `FeeInventory/Signals` deren Variablenbestand mit Interface, Tag, Adresse, Pfad und Datentyp. Inventardaten allein werden nicht auf FEE angewendet. Die Export- und Importpfade behalten gleiche Namen bei unterschiedlichen GUIDs getrennt.

Prüfung mit installiertem .NET 8:

```powershell
dotnet run --project Tests/ContainerFileFormatTests/VIBN_Tools.ContainerFileFormat.Tests.csproj
dotnet run --project Tests/UiStartupSmokeTests/VIBN_Tools.UiStartup.SmokeTests.csproj
dotnet run --project Tests/ContainerGenerationSmokeTests/VIBN_Tools.ContainerGeneration.SmokeTests.csproj
```

Der erste Test läuft ohne WPF, FEE-SDK oder zusätzliche NuGet-Pakete. Er prüft Schema, Containerklassifizierung einschließlich Typersatz und vollständigem Löschstand, Signal-GUIDs, gleichnamige Objektidentitäten, ausgerichtete Differenzzeilen, sicheren XML-Import und die gemeinsame Sperre für verschachtelte FEE-Schreiboperationen. Die weiteren Tests benötigen Windows, WPF und die unveränderten Repository-SDK-Dateien; sie prüfen auch manuelle Mehrfachzuordnungen mit anderen Namen, Offline-Export/Import, Undo, unbekannte SDK-Typen und abgeschaltete Scroll-Synchronisierung.

In der Linux-Arbeitsumgebung ohne .NET-SDK wurden C#-Syntax und XML/XSD-Struktur geprüft. Die .NET-Testprogramme, der WPF-Build und die Anwendung auf ein echtes FEE-Projekt konnten dort nicht ausgeführt werden. Ein erfolgreicher Livetest muss insbesondere die SDK-Unterstützung für entfallende Variablenzuordnungen und den Vorher-/Nachher-Stand bestätigen.


### Manuelle und automatische Zuordnung

`SimObject` unterstützt das optionale Attribut `assignment` mit den Werten `Automatic` und `Manual`. Ohne Attribut gilt die automatische Namensprüfung. Ein ausdrücklich manuell zugeordnetes Objekt mit anderem Namen wird als `<SimObject assignment="Manual">…</SimObject>` exportiert. Das Attribut wird im gemeinsamen Objektmodell und bei dessen Kopien erhalten.

### Regressionen für Anzeige und Root-Wechsel

Die Windows-Prüfprogramme ergänzen schnelle Wechsel zwischen Roots mit stark unterschiedlichen Zeilenzahlen, erhaltene alte Tabellenansichten, überbreite Baumobjekte, Sync-Aus, Quellenunterscheidung gleichnamiger Signale, wiederhergestellte Validierungsbreiten, Monitorbegrenzung und die manuelle Korrektur doppelter Vergleichseinträge. Die portablen Formatprüfungen ergänzen fehlerhafte beziehungsweise doppelte Container und persistierte manuelle Zuordnungen.

Ausführung erfordert ein .NET-SDK; die WPF-Prüfungen zusätzlich Windows und die FEE-SDK-Assemblies. Eine Syntax-/XML-Prüfung ersetzt diese Ausführung nicht. Für den Live-Test: zwei unterschiedlich große FEE-Roots bei nach unten gescrollten Listen mehrfach wechseln, die Anzeige auf einen anders skalierten Monitor verschieben und danach Vergleich und Zuordnung im realen FEE-Projekt prüfen.

### Ergänzte Regressionen vom 08.10.2026

`Tests/UiStartupSmokeTests/OctoberWorkflowRegressionTests.cs` prüft den Spaltenreset mit umgeordneten und entfernten Spalten, sichere Drag-and-drop-Eltern für `Run`, Assemblies-Parent und Duplikatwarnung, kombinierte Text-/Farbfilter, 120 falsch etikettierte physische Objektzuordnungen, isolierte Grouping-Bearbeitung, erhaltene GUIDs/Slotrouten, Export-Checkboxen und einen gemeinsamen Export mehrerer Roots sowie den Erhalt von Baumknoten beim Löschen. Die vorhandenen Auswahltests erwarten bei einer Objekt-/Signalzeile den einzelnen betroffenen Bereich.

Die portablen ContainerFile-Tests prüfen zusätzlich Rollen-/Live-Typ-Namensprüfung und die Zuordnung von FEE-Validierungsfehlern zu Vergleichszeilen. `Tests/CoreSmokeTests` prüft 60-/5-Minuten-Standards, individuelle Intervalle, ältere Einstellungsdateien und gemeinsame Deadline-Farben. Diese C#-Regressionen sind für die Ausführung mit .NET beziehungsweise Windows vorgesehen. In der Bearbeitungsumgebung wurden C#-Syntax, XML/XAML, das erweiterte XML-Beispiel gegen `CAAResult.xsd` und Diff-Whitespace geprüft; ein .NET-Build und ein Live-FEE-Lauf waren mangels .NET-SDK/Windows/FEE nicht möglich.

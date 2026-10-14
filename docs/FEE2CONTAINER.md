# FEE2Container

## Aktuellen FEE-Stand einlesen

**FEE-Roots einlesen** rekonstruiert alle Roots aus einem frischen FEE-Snapshot: aktuelle Objekte, Namen, Definitionen, Hierarchie, Interfacevariablen und deren tatsächliche Slotzuordnungen. Dies gilt auch für Projekte mit Container2FEE-Provenienz. Eine gespeicherte Generierungs-XML liefert keine Signal- oder Objektzuordnung mehr. Entfernte Variablen, umbenannte Container und geänderte Logiken werden anhand des heutigen Projektstands behandelt.

Es wird kein früherer ModelValidation-Bestand, Cache oder Temp-Verzeichnis durchsucht. Der schlanke Szenenbestand wird einmal gelesen; Variablenzuordnungen werden projektweit einmal ausgewertet und anschließend nach Root aufgeteilt. MoveBit-Routen werden je Hilfsobjekt innerhalb dieses Lesevorgangs wiederverwendet. Nur bei mehrdeutigen Typen werden zusätzliche Objekt-Tags gelesen. Diese Daten werden beim nächsten Einlesen neu aufgebaut.

Auswählbar sind die höchsten BasicFrames; verschachtelte BasicFrames gehören zu ihrem jeweiligen Root. Objekte ohne BasicFrame erscheinen unter **Projektobjekte ohne BasicFrame**. Decoration wird ausgeschlossen. Automatische physische Objektzuordnungen erfordern denselben Namen wie der Container. Mehrdeutige Namen werden zusätzlich über aktuelle Eltern und Objektverbindungen geprüft; ungeklärte Objekte bleiben für die manuelle Zuordnung verfügbar.

## Bedienung und Anzeige

1. FEE in Project Settings verbinden und **ModelValidation → Update Objects** ausführen. Nach jedem Disconnect/Reconnect ist dies erneut erforderlich.
2. **FEE-Roots einlesen** wählen. Alte Roots, Container, Signale und Objektlisten werden sofort aus der Darstellung entfernt. Auch ein abgebrochener oder fehlgeschlagener neuer Lesevorgang zeigt keinen alten Projektstand als neues Ergebnis.
3. Einen Root für die Details auswählen und gewünschte Roots über **Export** markieren. Die Tabellen zeigen aktuelle Container, Signalzuordnungen und nicht zugeordnete Objekte. Ein Container hebt seine Signale hervor und zentriert die erste passende Signalzeile; eine Signalzeile hebt ihren Container hervor und zentriert dessen Zeile. Filter auf der Zielseite werden bei Bedarf geöffnet.
4. Den Stand bearbeiten und exportieren. Nur markierte Roots sowie darin zum Export markierte Container und Signale werden übernommen.

Die erkannten Container starten eingeklappt. Der Pfeil links an einer Containerzeile öffnet oder schließt ihre Signal- und FEE-Objektdetails. Eingeklappt bleibt eine kompakte Containerübersicht mit Komponente, Typ, Exportauswahl und Prüfstatus. Die Auswahl einer Zeile allein ändert den Klappzustand nicht. Signalbearbeitung, Mehrfach-Drag-and-drop und Zuordnungsentfernung bleiben in den aufgeklappten Details verfügbar.

Beim Root-Wechsel werden neue Tabellenansichten veröffentlicht; alte virtuelle Ansichten bleiben unverändert, während WPF ausstehende Zeilenanforderungen beendet. Revisionsnummern verwerfen veraltete Auswahl- und Zentrierungsanforderungen. Die Spalten und Listenbereiche lassen sich manuell über Spaltenränder und Trennbalken anpassen.

Ein aktuelles Objekt ohne rücklesbare Signalzuordnung erhält einen **PRÜFEN**-Hinweis und einen leeren, schema-konformen FEE-UNASSIGNED-Eintrag. Es wird kein Signal erfunden. Leseprobleme erscheinen unter **Fehler und Rekonstruktionshinweise**. Der Export schreibt atomar ein ContainerFile, das ContainerGeneration und der Containervergleich einlesen können.

## Mehrfachauswahl und Objektzuordnungen

In **FEE-Objekte ohne eindeutige Containerzuordnung** lassen sich mit Strg/Shift mehrere Zeilen auswählen. Über den Ziehgriff wird die gesamte Auswahl auf einen vorhandenen Container verschoben. Für automatische physische SimObject-Zuordnungen muss der Objektname eindeutig zur Containerkomponente passen. Technische Hilfsobjekte werden zusätzlich über aktuelle Verknüpfungen und Cabinets über ihre Hierarchie zugeordnet. Die ausdrücklich manuelle Zuordnung per Drag-and-drop erlaubt auch unterschiedliche Namen, einschließlich gemischter Mehrfachselektionen. Mehrere gleichnamige Objekte werden anhand ihrer GUID getrennt geführt, auch wenn Name und FEE-Typ identisch sind.

Die Spalte **Zugeordnete FEE-Objekte** zeigt jeden Eintrag einzeln. Per Rechtsklick → **Zuordnung entfernen** wird genau dieses Objekt aus der Zuordnung entfernt und steht wieder in der Liste nicht zugeordneter Objekte zur Verfügung. Ein zugeordnetes Objekt kann auch direkt auf einen anderen Container gezogen werden, auch bei abweichendem Namen; der alte Eintrag wird dabei entfernt. Diese Aktionen bearbeiten den FEE2Container-Arbeitsstand und löschen keine Szenenobjekte in FEE.


## FEE-Objekte im ContainerFile

Der Szenen-Snapshot erhält auch SDK-Typen ohne spezielles ModelValidation-Modell. Ausgenommen sind die unten beschriebenen Hilfs-/CAD-Typen und Roboterachsen. Objekte außerhalb eines BasicFrames erscheinen im Bereich **Projektobjekte ohne BasicFrame**. Unbekannte Typen und mehrdeutige Namen bleiben sichtbar und manuell zuordenbar; ein unbekannter FEE-Typ allein begründet keinen geratenen Container-Typ.

Die Spalte **FEE-Objekte** nennt die Anzahl der zugeordneten Objekte. **Zugeordnete FEE-Objekte** zeigt Name, tatsächlichen FEE-Typ, Rolle und GUID als Tooltip. Das primäre Containerobjekt, weitere SimObjects und technische Hilfsobjekte werden getrennt gespeichert. GUIDs unterscheiden Objekte mit identischem Namen und Typ.

Beim Export werden Objektzuordnungen und Signal-GUIDs erhalten. Das gemeinsame, abwärtskompatibel erweiterte `CAAResult.xsd` akzeptiert `SimObjects` pro Container und optionalen verfügbaren Bestand unter `FeeInventory`. ContainerGeneration übernimmt Objektidentitäten beim ContainerFile-Import, Export, Speichern des Arbeitsstands und Undo. Eine Objektzuordnung ohne kompatibles Generatorziel bleibt eine dokumentierte Zugehörigkeit; dadurch wird keine unbekannte Slotverknüpfung erfunden.

Zur Prüfung des echten Projektbestands: nach **Update Objects** den Root einlesen, **Erkannte Container**, zugeordnete Objekte und nicht zugeordnete Objekte kontrollieren, dann exportieren und in ContainerGeneration erneut laden. Eine vollständige Erkennung eines konkreten FEE-Projekts muss mit dem dort verwendeten SDK und Modell geprüft werden.


## Namensprüfung und Root-Wechsel

Automatische FEE-Objektzuordnungen verlangen denselben Objekt- und Containernamen (ohne Unterscheidung der Groß-/Kleinschreibung, mit Entfernung äußerer Leerzeichen). Die Prüfung gilt auch für gespeicherte GUID-Zuordnungen, Primär- und technische Hilfsobjekte. Beim Live-Abgleich ist der tatsächlich gelesene FEE-Name maßgeblich; gespeicherte Namen, Rollen, Typen oder Provenienz allein erlauben keine abweichende automatische Zuordnung. Ein abweichend benanntes Objekt bleibt zur manuellen Zuordnung verfügbar. Die Signalrücklese folgt dem primären Objekt und aktuell über Slotverbindungen erreichbaren technischen Hilfsobjekten. Provenienz allein bestätigt keine Route; die Signalroute ist unabhängig von der Namensprüfung für physische Objektzuordnungen.

ContainerGeneration schreibt häufig leere `id`-Attribute. Beim Provenienzabgleich und im Editor erhält deshalb jeder Container eine eindeutige Arbeitskennung. Fehlende, leere und wiederholte IDs werden ersetzt; vorhandene eindeutige IDs bleiben erhalten. Die ursprünglichen stabilen Container2FEE-Identitäten werden vor dieser Normalisierung ermittelt, sodass Provenienz auch gleichnamige Container weiterhin unterscheiden kann. Objekte und Signale werden über die eindeutige Kennung genau ihrer Containerzeile zugeordnet und exportiert. Widersprüchliche gespeicherte Objekt-GUID-Zuordnungen bleiben zur manuellen Prüfung verfügbar. Der Editor arbeitet auf einer Kopie und verändert die gespeicherte FEE-Provenienz nicht.

Manuelle Zuordnungen dürfen abweichende Namen haben. Der XML-Eintrag erhält hierfür `assignment="Manual"`; automatische Zuordnungen werden mit `assignment="Automatic"` gespeichert. Dieser Unterschied bleibt beim Export und beim Einlesen in ContainerGeneration erhalten. Ein erneutes FEE-Root-Einlesen beginnt eine neue Rekonstruktion aus dem aktuellen FEE-Projekt; manuelle Editoränderungen vorher exportieren. Ältere Dateien ohne Kennzeichnung werden für SimObjects wie automatische Zuordnungen behandelt; eine abweichende manuelle Zuordnung muss einmal ausdrücklich neu vorgenommen werden.

Beim Root-Wechsel werden neue Detailkollektionen und Ansichten veröffentlicht. Die Daten hinter der vorherigen virtuellen Tabellenansicht werden nicht geleert, solange WPF noch verzögerte Zeilenanforderungen bearbeiten kann. Suchfilter bleiben erhalten; Auswahl und ausstehende Synchronisationsanforderungen werden zurückgesetzt.

## Bearbeiten, Grouping und mehrere Roots

Der Typ in **Erkannte Container** und in der tabellarischen Bearbeitung wird ausschließlich über ein nicht editierbares Dropdown gewählt. Die Auswahl enthält bekannte Containertypen des gemeinsamen Metadatenkatalogs und Typen aus der optional geladenen Req-XML. Freie Typtexte sind in diesen Tabellen nicht möglich; die separate XML-Ansicht bleibt ein ausdrücklich manueller Bearbeitungsweg.

**Bearbeiten / Grouping** öffnet einen getrennten Arbeitsstand des aktiven Roots. Containername, Typ, Signale, Slots, IDs, Adressen und Datentypen lassen sich in Tabellen bearbeiten. Das XML enthält zusätzlich die vollständigen FEE-Objektzuordnungen mit GUID und Slotrouten. **Tabellen → XML** aktualisiert die XML-Ansicht; **XML → Tabellen** übernimmt Änderungen aus dem Text. Noch nicht übernommene XML-Änderungen werden bei **Übernehmen** ebenfalls geprüft. Ungültiges XML oder ungültige GUIDs werden nicht stillschweigend durch einen älteren Tabellenstand ersetzt. **Abbrechen** verwirft die Bearbeitung.

Die Grouping-Vorschau nutzt dieselben `ContainerGenerationSettings.GenerateGroupingRules` und `ContainerGenerator.GroupItems` wie ContainerGeneration: Component, Typ, ID und Adresse; für ID/Adresse gelten Capture-Gruppen und die dort übliche Trennung mehrerer Muster durch Komma und Leerzeichen. Unterschiedliche Containertypen werden nicht vermischt. Signal-GUIDs, FEE-Objekte und Slotrouten bleiben erhalten. Ergibt eine Gruppierung für einen ursprünglichen Container mehrere Zielgruppen, bleiben seine FEE-Objekte in einem eigenen, vom Export ausgeschlossenen Container mit Prüfhinweis, bis sie im Hauptfenster ausdrücklich zugeordnet werden. Die Export-Checkboxen bleiben erhalten. Ein bewusst durch Bearbeitung oder Grouping umbenannter Container erhält für abweichend benannte physische Objekte eine manuelle Zuordnung.

Für einen gemeinsamen ContainerFile-Export mehrere Zeilen in der Root-Liste unter **Export** markieren. Die Auswahl der gerade angezeigten Root-Zeile schränkt den Export nicht ein. Übernommen werden nur Container und Signale mit aktivem Export-Haken innerhalb markierter Roots. Der Export erhält rootbezogen eindeutige Container-IDs, Signalbindungen und verfügbaren Bestand. Offene Tabellenbearbeitungen werden vor Bearbeiten/Export abgeschlossen.

## Grundlage der Containererkennung und Requirements.xml

Die Erkennung nutzt den gemeinsamen Containerkatalog, aktuelle Logikdefinitionen, Cabinet-Definitionen, Namen, Hierarchie und Variablen-/Slotrouten. Eine Req-XML ist **optional**: bekannte FEE-Typen können ohne sie rekonstruiert werden. **Req-XML laden …** liest und validiert eine Datei direkt im Reiter. Eine in ContainerGeneration bereits geladene Datei wird beim nächsten Einlesen übernommen. **Req-XML verwenden** lässt sich ausschalten. Der Text neben den Schaltflächen zeigt die tatsächlich verwendete Quelle; geänderte Definitionen gelten beim nächsten **FEE-Roots einlesen**, bestehende manuelle Bearbeitungen bleiben bis dahin erhalten.

Die Req-XML ergänzt Typen, Slotnamen und Min/Max-Grenzen für Signale. Für kompatible Typvarianten mit derselben FEE-Logik werden auch die bestehenden Required-/Exclude-Keyregeln aus ContainerGeneration auf aktuelle Signalnamen angewendet. Nicht in der Datei enthaltene bekannte FEE-Typen bleiben mit Prüfhinweis erkennbar. Unbekannte kundenspezifische Logiken lassen sich anhand eindeutiger aktueller Slots einem Req-Typ zuordnen; das Ergebnis erhält einen Prüfhinweis. Die Datei ersetzt keine aktuellen Objekt-GUIDs oder FEE-Verknüpfungen.

Signalrouten werden einschließlich BoolNot, BoolAnd, BoolOr und MoveBit in beiden Richtungen durch die aktuelle Struktur verfolgt. Nachweislich zu einem primären Container gehörende Hilfsobjekte werden als technische Hilfsobjekte behandelt; interne Hilfsports erzeugen keine zusätzlichen Signalzeilen, wenn dieselbe Variable bereits am primären Objekt erkannt wurde. Physische automatische Zuordnungen verlangen weiterhin passende Namen; abweichende Namen bleiben manuell zuordenbar. Nicht mehr unterstützte Live-Slots werden mit Originalname und Signal-GUID sowie Prüfhinweis erhalten. Der verfügbare Signalbestand im Export wird nach der aktuellen Root-Zuordnung begrenzt.

Die FEE-Definition allein unterscheidet nicht jede fachliche Variante: Cylinder und FeedSafetyDoor verwenden dieselbe Logik; ReturnCircuit und SafeArea dasselbe BoolNot. Eine passende Objekt-Provenienz darf diese Typfrage auflösen. Widerspricht sie der aktuellen Definition, gewinnt die aktuelle Definition. Ohne eindeutigen Hinweis wird ein kompatibler Typ mit Prüfmeldung verwendet. SensorX besitzt kein Szenenobjekt; aus einer Struktur ohne Objektbezug lässt sich dessen frühere Gruppierung nicht sicher rekonstruieren. Solche Signale bleiben im verfügbaren Signalbestand und können manuell eingeordnet werden. Historische Root-XML stellt keine entfernten Container oder alten Signalzuordnungen wieder her.


## Live-Details und gemeinsame Containeransicht

FEE2Container zeigt Component, Type, ID, Address, DataType, Signal und Slot wie ContainerGeneration. Die Einträge in der Containerzeile und unter „Erkannte Signalzuordnungen“ bearbeiten dieselben Daten. Die Typauswahl bleibt ein Dropdown; „Bearbeiten / Grouping“ und die Grouping-Leiste verwenden dieselben Gruppierungsregeln. Export-Checkboxen und manuelle FEE-Objektzuordnungen bleiben erhalten.

„Verknüpfung“ beschreibt aktuelle Objekt-/Slot-Endpunkte sowie Signalnamen, auch für SimpleLogicObjects und CabinetElemente. „Parent / Cabinet“ zeigt bei CabinetElementen das übergeordnete Cabinet; bei anderen Objekten den Parent. LogicObjects zeigen die geladene Logikdefinition. SceneObjectCompounder, CADAssembly und Decoration werden nicht als Container oder unzugeordnete Objekte angezeigt. MotionJoints werden nur bei exakt j1 bis j9 (ohne Beachtung der Groß-/Kleinschreibung) ausgeblendet; beispielsweise j10 oder Station.j1 bleiben sichtbar.

Bekannte SpecialDevice-Logikdefinitionen stammen aus DeviceFactory. Ihre aktuellen Signalzuordnungen erscheinen in der normalen Containerliste und zusätzlich in einer Geräteübersicht. Der Button überträgt die bereits eingelesenen Geräte des ausgewählten Roots an FEE2SpecialDevices. Der Typfilter betrifft die Objekte ohne eindeutige Containerzuordnung.

Die Übersicht „Verknüpfte Signale ohne Zuordnung“ enthält Live-Variablen, die im aktuellen Root an Objekte angeschlossen sind, aber keinen vollständigen aktiven Signal-/Slot-Eintrag im Containerarbeitsstand besitzen. Das kann insbesondere bei unbekannten Hilfslogiken oder manuell deaktivierten Signaleinträgen auftreten. Gezählt werden eindeutige Variablen-GUIDs, keine mehrfachen Routen desselben Signals. Die Schaltfläche in einer Containerzeile begrenzt die Liste auf dessen zugeordnete FEE-Objekte; die obere Schaltfläche zeigt den ganzen Root. Alle Listen verwenden Pixel-Scrolling und ein kurzes, unterbrechbares Mausrad-Scrolling; Virtualisierung bleibt aktiv.

## Container2FEEVisual: Snapshot und Logikzuordnung

„FEE aktualisieren“ und „Automatisch zuordnen“ lesen einen neuen FEE-Snapshot mit Verknüpfungen aller gefundenen Variablen und Roots. Bei der ersten Aktualisierung pro Verbindung werden alle gefundenen Interfaces ausgewählt. Danach bleibt die manuelle Auswahl erhalten. Root-/Interface-Checkboxen ändern nur die Projektion des Snapshots und führen keine SDK-Abfragen aus. Disconnect löscht den Snapshot; eigene Verknüpfungs-/Löschaktionen aktualisieren ihre bestätigten Ergebnisse im gespeicherten Stand.

Ein verfügbares SimObject wird erst grün, wenn es dem Plan zugeordnet ist und die für diesen Container erforderlichen Verknüpfungen bestätigt sind. Ein beliebig verbundener Slot genügt nicht. Für Containertypen mit vorgesehener Logik müssen Logic Component und Slots vorhanden sein, bevor Signale und SimObjects zugeordnet werden. Direkte SimObject-Typen behalten ihre bisherigen Verknüpfungsregeln. LogicObject-Instanzen können auf einen Container oder seinen Logikeintrag gezogen werden; ihre Szenen-GUID wird verwendet, nicht die GUID der Logikdefinition. Diese Zuordnung bleibt in der Plandatei erhalten und wird bei „Nur verknüpfen“ verwendet.


## Aktuelle SpecialDevice-Signale und Signalbearbeitung

SpecialDevices werden je LogicObject-GUID erkannt, auch wenn mehrere Geräte denselben Namen oder einen gemeinsamen BasicFrame besitzen. FEE2Container und FEE2SpecialDevices verwenden dieselbe Live-Rekonstruktion. Die beim Generieren gespeicherten Variablen-GUIDs, Basisadressen und Signalnamen ersetzen den aktuellen FEE-Stand nicht mehr. Die Gerätelogik erscheint zusätzlich als Typ in der normalen Containerliste; ihre aktuell verknüpften Variablen erhalten reguläre Signal-/Slot-Einträge und zählen daher nicht pauschal als unzugeordnet.

FEE2SpecialDevices zeigt unterhalb der Geräte die aktuellen Adressen, Pfade, Datentypen, Nutzungen und verknüpften Slots. E- und A-Byte sind die kleinsten **numerischen** E/I- beziehungsweise A/Q-Byteadressen der verknüpften Variablen; bei reinen symbolischen Adressen steht — in der Ansicht. „Aktuell verbunden“ zählt eindeutige Variablen-GUIDs, „Fehlend“ unbelegte deklarierte PLC-Slots. „Signale“ ist die Summe dieser beiden Werte. Die separaten PLC_IN/PLC_OUT-Spalten zählen Slots. Die Übernahme aus FEE2Container überträgt die gefundenen Gerätesnapshots direkt, statt ausschließlich den Reiter zu öffnen.

Verknüpfte, nicht zugeordnete Signale sowie vorhandene Signalzeilen lassen sich auf Container ziehen. Verschieben und Entfernen ändern den ContainerFile-Arbeitsstand; die aktuelle Verbindung im FEE-Projekt wird dadurch nicht umgeschrieben oder gelöscht. Entfernte aktive Zuordnungen erscheinen wieder unter „Verknüpfte Signale ohne Zuordnung“. Entf innerhalb eines Texteingabefelds bearbeitet ausschließlich den Text. Req-XML-Fundstellen in ContainerGeneration zeigen die auslösenden Komponenten-/Slot-/Key-Regeln samt vorhandener Zeileninformation; „No matching components“ bleibt ohne ergänzende Fundstelle.


## Entfernen, Ausschlusssuche und noch nicht definierte Typen

**Entfernen** löscht den Container einschließlich seiner Signalzuordnungen aus dem FEE2Container-Arbeitsstand und aus dem nächsten ContainerFile-Export. Die zugehörigen FEE-Objekte werden zur erneuten Zuordnung freigegeben; tatsächlich verknüpfte Signale erscheinen wieder ohne Zuordnung. FEE selbst wird dabei nicht verändert. Ein noch von anderen Containern verwendetes Cabinet oder Hilfsobjekt bleibt diesen zugeordnet. Ein Objekt kann erneut als Container ergänzt werden; seine aktuellen Signalverbindungen werden übernommen.

Die Suchfelder für Container, Signale und unzugeordnete Objekte unterstützen wie die Rechnerübersicht mit Komma getrennte Suchbegriffe und Ausschlüsse: `Scanner,!Motor` zeigt Scanner-Treffer ohne Motor. Groß-/Kleinschreibung wird ignoriert.

`N/A` ist ein auswählbarer und unverändert exportierter Typ für noch nicht definierte Container. Unbekannte LogicObjects wie `Sick_Scanner_MicroScan3` werden mit ihren heutigen Signal-/Slotzuordnungen und ihrer GUID als N/A erkannt. Die tatsächliche Logikdefinition wird im optionalen XML-Eintrag `LogicDefinitionName` am SimObject gespeichert und beim Bearbeiten/Export erhalten. N/A bleibt ein gelber Prüfhinweis, bis eine passende Containerdefinition festgelegt ist.

CabinetElemente werden über ihre aktuelle Definition erkannt, beispielsweise als CabinetSwitch oder CabinetFuse. Ihre GUID bleibt auch dann erhalten, wenn die angezeigte Beschriftung vom internen Objektnamen abweicht. Cabinets selbst werden nur als `TechnicalHelper` gespeichert und erzeugen keinen eigenen Container.

BoolAnd, BoolAlwaysTrue, BoolAnd10Inputs, ConvertBoolToByte, ConvertByteToBool, BoolCopy5Outputs, BoolOr und BoolPositiveEdge werden nicht als eigene Container erkannt. Aktuelle Verbindungen ordnen sie den beteiligten Containern als technische Hilfsobjekte zu, einschließlich zusammenhängender Hilfslogikketten. Bei gemeinsam verwendeten Hilfsobjekten sind mehrere Containerbezüge zulässig. Fehlt jede auflösbare Verbindung, bleibt das Objekt zur manuellen Prüfung unzugeordnet; alte Provenienz ersetzt diesen Nachweis nicht. Solche nachgewiesenen Strukturzuordnungen werden als `assignment="Linked"` gespeichert und bei einer erneuten FEE-Rücklesung erneut am aktuellen Stand geprüft.

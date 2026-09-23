# FEE2SpecialDevices

`FEE2SpecialDevices` ist bewusst ein eigener Hauptreiter. Die Root-Auswahl und der FEE-Lesezugriff entsprechen dem Sicherheitsprinzip von `FEE2Container`; das Zieldokument ist jedoch ein gerätespezifisches JSON statt eines ContainerFiles. Dadurch werden Container- und Hardwaredomäne nicht vermischt.

## Unterstützter Umfang

Nach einer vollständig erfolgreichen Erzeugung durch `SpecialDevices2FEE` schreibt die Anwendung eine versionierte Provenienz in die `TagComponent.TagEntries` des erzeugten `BasicFrame`. Sie enthält:

- Präfix, Hersteller und Gerätetyp;
- optionalen Robotertyp;
- Eingangs- und Ausgangs-Startbyte;
- Signal-GUID, Tag, Adresse, Richtung, Datentyp und Kommentar.

Ein fehlgeschlagener oder nur teilweise ausgeführter Schreibvorgang erhält keine gültige Provenienz. Nach der vollständigen Geräteerzeugung wird der Tag-Datensatz geschrieben, der vorhandene `BasicFrame` erneut an FEE gesendet und in einem begrenzten Zeitfenster zurückgelesen sowie per Prüfsumme validiert. Ist nur dieser Rückleseschritt wegen einer verzögerten FEE-Sichtbarkeit nicht möglich, bleibt die fachlich erfolgreiche Geräteerzeugung erfolgreich: Das Gerät wird aus der Warteschlange entfernt und ein Provenienzhinweis ins Anwendungsprotokoll geschrieben. Beim Rücklesen werden die Signale über ihre persistierte Variablen-GUID mit den aktuellen FEE-Werten überlagert. Fehlende Variablen werden gezählt und als Hinweis angezeigt; der gespeicherte Generierungsstand bleibt für die Diagnose erhalten.

## Ablauf

1. Mit FEE verbinden.
2. **FEE-Geräte einlesen** wählen.
3. Einen eindeutig erkannten Root auswählen.
4. Hersteller, Gerätetyp, Adressen sowie die Anzahl aktueller/fehlender Signale prüfen.
5. PLC_IN/PLC_OUT jeweils als *vorhanden* und *verbunden* sowie die konkrete Liste aller fehlenden `PLC_*`-Slots prüfen. E-/A-Byte sind die kleinsten gefundenen und verknüpften Byteadressen.
6. **Gerät als JSON exportieren** oder **Alle als JSON exportieren** wählen. **Auswahl entfernen** und **Liste leeren** verändern nur die geladene Ansicht, nie das FEE-Projekt.

Der Export erfolgt atomar als `*.specialdevice.json`. Das JSON ist eine versionierte, maschinenlesbare Momentaufnahme für Vergleich und Archivierung. Unter **SpecialDevices2FEE → FEE2-JSON laden** kann die Datei geprüft und über den bestehenden `DeviceFactory`-Katalog in die Warteschlange übernommen werden. Unbekannte Hersteller, Gerätetypen, Versionen oder fehlende Pflichtangaben werden abgewiesen. Ein bereits vorhandenes Präfix desselben Herstellers wird nicht doppelt eingereiht.

Der Gerätekatalog bleibt bei einer erneuten Erzeugung die autoritative Quelle für Signale. Weichen die aus FEE exportierten Tags, Adressen, Datentypen oder Richtungen von dieser Definition ab, wird das Gerät zwar zur bewussten Prüfung eingereiht, die Oberfläche warnt aber ausdrücklich: Die manuell veränderten Signalwerte werden nicht stillschweigend als neue Generierungsregel verwendet. Der JSON-Snapshot bleibt der Soll-Ist-Nachweis.

## Rekonstruktion älterer Projekte und Grenzen

- Durchsucht werden alle BasicFrames der Hierarchie, damit auch unter Projekt- oder Gruppenknoten abgelegte Geräte gefunden werden. Doppelte Treffer desselben Geräts werden auf den spezifischsten beziehungsweise provenance-markierten Geräteframe reduziert. Ein bei alten/manuellen Frames vollständig fehlender `TagComponent` wird wie fehlende Provenienz behandelt und verhindert die Struktursuche nicht. Ein Gerät wird nur dann rekonstruiert, wenn genau eine Logikdefinition eindeutig einem Eintrag des bestehenden `DeviceFactory`-Katalogs entspricht. Präfix, aktuelle Variablenzuweisungen, Richtungen und Startadressen werden aus FEE gelesen. Für Atlas-Copco-Geräte muss zusätzlich ABB/Fanuc/Kuka eindeutig aus den Adressformaten folgen; andernfalls wird kein Snapshot geraten.
- Die Provenienz bleibt der garantierte Round-Trip. Eine Rekonstruktion wird als `FEE-Struktur (Rekonstruktion)` gekennzeichnet und muss fachlich mit der katalogisierten Gerätedefinition verglichen werden.
- Der kontrollierte Reimport stellt Hersteller, Gerätetyp, Präfix, Robotertyp und Startadressen wieder her. Manuell abweichende Signale werden diagnostiziert, nicht ungeprüft in den Gerätekatalog geschrieben. Nach dem Einlesen der TIA-Hardware kann **JSON mit TIA vergleichen** Präfix sowie E-/A-Startbyte gegenüberstellen; ein möglicher statt exakter Treffer bleibt ausdrücklich prüfpflichtig.
- Die Codec- und Exportlogik ist automatisiert getestet. Lesen nach echtem FEE-Save/Reload bleibt eine Live-Abnahme mit der installierten FEE-Laufzeit.

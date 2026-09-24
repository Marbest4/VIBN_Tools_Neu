# Abgleich MyHmiAvalonia mit VIBN Tools

## Bewertete Quelle

Verglichen wurde `Marbest4/MyHmiAvalonia` mit dem Branch `feature/ui-fee-consistency` von VIBN Tools. Der Referenzcode ist eine Avalonia-Anwendung mit großen ViewModels und zeilenbasierter L5X-Verarbeitung. Er wurde als fachliche Verhaltensreferenz verwendet, nicht als direkt zu kopierende Architektur.

## Projektsuche und Rechnerübersicht

Die aktuelle Rechnerübersicht deckt die Referenzfunktionen bereits ab und geht darüber hinaus: strukturierte Kanbanize-Karten, getrennte Planung/In Arbeit/Abgeschlossen-Ansichten, Mehrfach-/Negativsuche, Projektpfade, Onlineprüfung, RDP, Rollen und aktualisierbarer Cache. Deshalb wurde keine zweite ProjectSearch-/PCSearch-Implementierung übernommen.

RDP-Sitzung und letzte Anmeldung werden nicht aus dem Windows-Ereignisprotokoll gelesen. `WindowsRemoteSessionService` führt lesend `quser.exe /server:<PC>` aus. „RDP-Sitzung“ ist der aktive Terminaldienst-Benutzer; „Letzte Anmeldung“ ist der jüngste in der aktuellen `quser`-Ausgabe enthaltene Anmeldezeitpunkt. Das ist keine vollständige historische Anmeldechronik. Fehlende Remote-Berechtigung, Firewall-/Dienstprobleme und Timeouts werden als „Nicht abrufbar“ ausgewiesen.

## TIA-Funktionsabgleich

| MyHmiAvalonia-Aktion | VIBN-Tools-Entsprechung | Ergebnis |
| --- | --- | --- |
| Axis TO Basic Config | Achsen laden, einzeln auswählen und mit typisierten Parametern konfigurieren | vorhanden |
| Axis DB+FC/FB Integration | optionale Achskonfiguration plus Erzeugung/Import von `AxisDB.xml` und `AxisFC.xml` im Bibliotheksimport | vorhanden |
| Import VicoBib | rekursiver Import von `_Programm` und `_Datatype` | vorhanden |
| Export VicoBib | rekursiver Export der gewählten Bibliothek nach `_Programm` und `_Datatype` | vorhanden |
| PLC-Auswahl/Projekt speichern | typisierte Bridge-Befehle mit erklärender UI | vorhanden |
| Hardware lesen | typisierte, rekursive Hardwareabfrage für SpecialDevices | zusätzlich vorhanden |
| AxisDB → ExcelInterface | Referenz sendet nur den Stringbefehl an eine nicht enthaltene externe Bridge | nicht verifizierbar |
| TO-Config Export/Import | Referenz sendet nur `ExportTO`/`ImportTO`; serverseitige Implementierung fehlt im Repository | nicht verifizierbar |

Die TIA-Bridge bleibt absichtlich eine automatisch gestartete `net48`-EXE. Siemens Openness und der synchrone Hersteller-API-Aufruf werden dadurch vom `net8.0-windows`-Hauptprozess isoliert. Ein hängender Siemens-Aufruf kann so beendet werden, ohne VIBN Tools, FEE oder Rechnerübersicht mit zu beenden.

## Rockwell

Der neue Reiter bildet die im Referenzrepository vorhandenen GCCS-Schritte ab:

- L5X laden und strukturell analysieren;
- `SIMULATION_MODES`, die `SimulationMode`-AOI und Controller-Tags idempotent ergänzen;
- `A001_Simulation` aus `B001_MapInputs` erzeugen und `A000_Main` umschalten;
- dasselbe für erkannte Safety-Programme als `s_A001_Simulation`;
- ausschließlich eine neue `_Generated.L5X` schreiben und diese optional öffnen.

Die Umsetzung verwendet `XDocument`, stabile Namen und atomisches Speichern. Vorhandene Elemente werden nicht dupliziert. Anders als der Referenzcode überschreibt sie nie die Quelldatei und zerlegt XML nicht anhand von Zeilenpositionen.

Ohne reales Studio-5000-Projekt konnte nur die XML-Transformation mit synthetischen Standard-/Safety-Programmen automatisch geprüft werden. Direkte Safety-Moduladressen werden derzeit mit einem sichtbaren Prüfhinweis auf `NOP()` gesetzt; die Referenz versucht hierfür gerätespezifische Safety-Tags aus Modul-XML abzuleiten. Eine belastbare automatische Abbildung benötigt mindestens einen repräsentativen L5X-Export pro verwendeter GuardLogix-/Modulfamilie und einen Importtest in der eingesetzten Studio-5000-Version.

## FEE-Änderungen dieses Abgleichs

`FeeSignalLinkDiscovery` behandelt `null` aus `GetAssignedSceneObjectsAsync`, `GetSlotSlotAssignmentAsync` und `GetPropertiesAsync` als leeres/unverknüpftes Ergebnis. Dadurch führt ein vorhandenes Signal ohne Assignment nicht mehr zu einer `NullReferenceException`.

FEE2Container besitzt je eine Suche für Container, Signalzuordnungen und nicht containerrelevante Objekte. Die gegenseitige Auswahl markiert weiterhin alle zugehörigen Einträge und bringt den ersten verknüpften Eintrag auch bei virtualisierten, langen Listen in den sichtbaren Bereich. Tooltips erklären jede Spalte der FEE-Root-Tabelle.

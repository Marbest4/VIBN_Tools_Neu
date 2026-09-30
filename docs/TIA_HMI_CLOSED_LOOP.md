# TIA HMI/FEE Closed-Loop-Test

## Aussage und technische Grenze

Der Bereich **TIA Portal → HMI/FEE Closed Loop** prüft nach ausdrücklichem Start den Weg:

```text
HMI-Runtime-Tag -> PLC-Ausgang -> FEE-Modell -> PLC-Rückmeldeeingang
```

TIA Openness kann Projektstruktur und Compiler bedienen, aber keinen laufenden HMI-Taster betätigen. Deshalb spricht VIBN Tools den HMI-Laufzeitteil über einen kleinen projektspezifischen Runtime-Adapter an. Ohne erreichbaren Adapter, FEE-Verbindung und ausgewählte Signale gibt es keinen positiven Nachweis. Der Ablauf wurde mit simulierten Adaptern getestet; eine reale WinCC-/PLCSIM-/FEE-Abnahme ist weiterhin erforderlich.

## Voraussetzungen

1. WinCC Runtime und PLC beziehungsweise PLCSIM Advanced laufen und kommunizieren.
2. FEE ist verbunden; Ausgang und/oder Rückmeldeeingang sind als Interfacevariablen vorhanden.
3. Der Signalweg zwischen PLC und FEE ist im Projekt fertig konfiguriert.
4. Das angegebene HMI-Runtime-Tag ist les- und schreibbar. Es ist ein Runtime-Tag, kein Bildobjektname.
5. Ein WinCC-spezifischer Adapter stellt die Named Pipe bereit. Standardname ist `VIBN_Tools.WinCC.Runtime`.
6. Vor dem ersten Produktivlauf sind Benutzerberechtigung, sichere Teststellung, Timeout und fachlich erwartete Werte zu prüfen.

## Adaptervertrag

VIBN Tools öffnet pro Operation eine lokale Named-Pipe-Verbindung. Anfrage und Antwort sind jeweils eine UTF-8-JSON-Zeile.

```json
{"Operation":"probe","Tag":null,"Value":null}
{"Operation":"read","Tag":"HMI.Start","Value":null}
{"Operation":"write","Tag":"HMI.Start","Value":"1"}
```

Antwort:

```json
{"Success":true,"Value":"0","Message":"Runtime verbunden"}
```

Der Adapter muss `probe`, `read` und `write` implementieren. Die konkrete Anbindung an WinCC Unified, WinCC Professional oder eine andere Runtime gehört in diesen Adapter, weil deren APIs und Lizenzen nicht austauschbar sind.

## Ablauf und Rücksetzung

VIBN Tools prüft den Adapter, liest den ursprünglichen HMI-Wert und die anfänglichen FEE-Werte, schreibt den Trigger und fragt die gewählten FEE-Signale im 200-ms-Raster bis zum Timeout ab. Ein leerer erwarteter Wert verlangt eine Änderung gegenüber dem Anfangswert; ein gesetzter Erwartungswert muss textuell übereinstimmen. Ausgang und Rückmeldung sind unabhängig wählbar, mindestens eines von beiden ist Pflicht.

Der ursprüngliche HMI-Wert wird in einem `finally`-Pfad auch bei Fehler, Timeout oder Abbruch zurückgeschrieben und erneut gelesen. Eine nicht verifizierbare Rücksetzung lässt den Test fehlschlagen. Ergebniszeilen erscheinen direkt im TIA-Reiter und werden als **TIA HMI Closed Loop** unter **Project Quality → Adapter/Nachweise** gespeichert.

## Noch live abzunehmen

- Herstelleradapter gegen die tatsächlich eingesetzte WinCC-Runtime implementieren und signieren.
- Lesen/Schreiben eines ungefährlichen Testtags nachweisen.
- PLC-/PLCSIM- und FEE-Signalrichtung anhand eines freigegebenen Testprojekts prüfen.
- Timeout, Bedienerabbruch, Runtime-Abbruch und Rücksetzung praktisch testen.
- Erst danach produktive Bewegungs- oder Sicherheitsfunktionen freigeben.

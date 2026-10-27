# Mini-Tools: FEE-Auswahl positionieren

## Zweck

Der Reiter **Mini-Tools** positioniert die aktuell in FEE ausgewählten
Szenenobjekte. Bei ausgewählten Objekten vom Typ `Surface` kann im selben Lauf
zusätzlich `Transform.LocalScale` gesetzt werden. Andere Objekttypen behalten
ihre Skalierung.

## Voraussetzung

- In **Project Settings** besteht eine bestätigte FEE-Verbindung.
- Ein FEE-Projekt ist geladen.
- Mindestens ein Objekt ist direkt in FEE ausgewählt.
- Der angemeldete Benutzer besitzt mindestens Level7.

## Bedienung

1. Gewünschte Objekte im FEE-Projektbaum oder in der 3D-Ansicht markieren.
2. Im Reiter **Mini-Tools** die Werte für X und Y eintragen.
3. Die drei wiederverwendbaren Z-Werte für Bandhöhe 1 bis 3 eintragen.
4. Im Dropdown auswählen, welche Bandhöhe dieser Lauf verwendet.
5. Länge, Breite und Höhe für die Surface-Skalierung eintragen.
6. **FEE-Auswahl positionieren** drücken.

Die Ergebnisliste zeigt pro Objekt, ob Position und gegebenenfalls
Surface-Skalierung von FEE bestätigt wurden. Status, Fortschritt und Fehler
werden zusätzlich im zentralen Diagnoseprotokoll unter **Mini-Tools** erfasst.

## Technische Zuordnung

| Eingabe | FEE-Eigenschaft |
| --- | --- |
| X / Y / ausgewählte Bandhöhe | `Transform.Position` |
| Länge / Breite / Höhe | `Transform.LocalScale` – nur für `Surface` |

Die Eingaben verwenden FEE-Einheiten. Positive und negative Positionswerte
sind erlaubt; Skalierungswerte müssen größer als null sein. Punkt und das im
Windows-Profil konfigurierte Dezimaltrennzeichen werden akzeptiert.

Die Änderungen werden unmittelbar an das geladene FEE-Projekt gesendet. Das
Speichern des Projekts wird nicht automatisch ausgelöst. Die FEE-Aufrufe werden
absichtlich seriell ausgeführt, da die verwendete SDK-Verbindung
zustandsbehaftet ist. Ein Fehler an einem Objekt wird dokumentiert, ohne die
Ergebnisse bereits verarbeiteter Objekte zu verbergen.

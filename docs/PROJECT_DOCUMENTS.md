# Rechnerübersicht: 00_Documents

Die Spalte **00_Documents** listet ausschließlich die Dateien direkt unter `<Simulation-Projektpfad>/00_Documents`. Unterordner und deren Inhalte werden nicht durchsucht oder angezeigt. Enthält der Ordner nur Unterordner, erscheint **Ordner leer**. Der Projektpfad wird mit derselben Auflösung wie beim **Simulation**-Button ermittelt. Für die ausgewählte Rechnerzeile gilt das aktuell ausgewählte Projekt; andere Zeilen verwenden wie der Button ihr erstes aktives Projekt.

Der Serverordner wird im Hintergrund gelesen, unabhängig davon, ob der Rechner online ist. Gleiche Projektpfade werden gemeinsam abgefragt. Ergebnisse werden fünf Minuten zwischengespeichert; ein allgemeines Aktualisieren der Rechnerdaten verwirft den Zwischenspeicher. Ein Projektwechsel verwirft ausstehende Anzeigeergebnisse des alten Projekts. Es werden keine Dateien geschrieben.

Die Spalte unterscheidet leere, fehlende und nicht erreichbare Ordner sowie fehlende Projektpfade. Nach acht Sekunden endet das Warten der Anzeige mit einer Zeitüberschreitung; ein blockierender Dateisystemzugriff hält dadurch die Oberfläche nicht auf. Der Tooltip zeigt Projekt, vollständigen Ordnerpfad und gegebenenfalls den Zugriffsfehler. Lange Dateilisten lassen sich innerhalb der Zelle scrollen.

Die Spalte startet auch bei älteren gespeicherten Ansichten sichtbar. Danach wird ihre Sichtbarkeit gemeinsam mit den übrigen Spalten pro Benutzer gespeichert. Sie lässt sich über die vorhandene Spaltenauswahl ein- und ausblenden.

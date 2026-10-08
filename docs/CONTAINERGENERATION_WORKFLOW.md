# ContainerGeneration: Änderungen prüfen und bestätigen

**Neue Details einblenden** steuert die Spalte **Reimport-Änderung** in der Containerliste sowie die entsprechende Spalte der offenen Signallisten. Die Details starten ausgeblendet. Das Ausblenden verändert weder Vergleichsentscheidungen noch Änderungsmarkierungen.

Nach **Auswahl anwenden** bleiben neue/geänderte Container gelb. **Änderungen bestätigen** bestätigt die Änderungsmarkierungen im gesamten Arbeitsstand. Ohne Fehler oder Warnungen werden die betroffenen Container und Signalzeilen wieder weiß. Fehler haben Vorrang und bleiben rot; Zuordnungswarnungen und Einträge mit fachlichem Prüfbedarf bleiben gelb/orange. Die Bestätigung ersetzt keine Validierung. Weitere Bearbeitungen öffnen die Änderungsmarkierung erneut.

Die Bestätigung erhält Review-Zustand, Kommentare und manuelle Herkunft. Sie ist rückgängig machbar und bleibt beim Speichern/Laden des Arbeitsstands erhalten. Diese Metadaten werden nicht in das produktive ContainerFile geschrieben. Die vorhandene Checkbox für manuell geprüfte Container bleibt unabhängig davon erhalten.

## Fehlerbehandlung

Synchrone und asynchrone Befehle besitzen eine gemeinsame Fehlergrenze. Erwartete verwaltete Fehler werden protokolliert und in der Statuszeile angezeigt. Import/Generierung sperren während ihres Laufs andere Befehle und die bearbeitbaren Listen. Filtertimer behandeln Fehler ebenfalls. Normale Bearbeitungsaktionen und die Reimport-Übernahme erstellen vor dem Eingriff einen unabhängigen Arbeitsstand; ein Fehler stellt diesen wieder her und verwirft das Aktionsprotokoll der abgebrochenen Aktion. Undo/Redo verschiebt seine Historieneinträge erst nach erfolgreicher Wiederherstellung.

Nicht beschreibbare Aktionslog-Ordner verhindern keine Bearbeitung oder den Seitenstart. Die fehlgeschlagene Protokollierung wird gemeldet. Wiederholtes Laden der Seite registriert den Logempfänger nicht mehrfach; beim Verlassen werden Logempfänger und UI-Timer angehalten. Meldungen aus Hintergrundthreads werden über den UI-Dispatcher übernommen. Text-Inlines wie `Run` werden über ihren Inhalts-/logischen Elternknoten aufgelöst.

Die Dispatcher-Fehlerbehandlung erhält die Anwendung auch bei bekannten negativen WPF-Größen, veralteten virtuellen Indizes und abfangbaren Fehlern mit ContainerGeneration-Stack. Schwere Prozessfehler wie Speichermangel, Stacküberlauf oder Speicherzugriffsverletzungen werden nicht als erfolgreich reparierbare Vorgänge behandelt. Eine Garantie gegen jeden denkbaren Prozessabsturz wäre dadurch nicht gegeben.

## Drag-and-drop und große Bestände

Eine Mehrfachverschiebung durchsucht Container und offene Listen einmal anhand stabiler Signal-IDs; ältere Einträge nutzen den vorhandenen Quellschlüssel. Unterschiedliche IDs bleiben auch bei gleichem Signalnamen getrennt. Container validieren und berechnen ihre Review-Details am Ende der Aktion. Undo-Kopien nutzen diese verzögerte Aktualisierung ebenfalls. Die Arbeitsaktion schreibt ihre JSONL-Ereignisse gemeinsam; ein Remove/Add-Paar bleibt als Move erkennbar, auch wenn eine große Aktion länger als 500 ms dauert.

Der Drag beginnt erst nach der normalen Windows-Ziehschwelle. Die Identität wird beim Mausklick aufgenommen, damit ein wiederverwendetes UI-Element kein anderes Signal liefert. Die Tabellen bleiben virtualisiert; die Hauptliste verwendet Standard-Virtualisierung statt Recycling. Reimport-Vorschauen ersetzen ihre Vergleichsliste in einem Schritt.

`VirtualizingStackPanel` ist eine Microsoft-WPF-Frameworkklasse, die unter anderem variable Zeilenhöhen, Hierarchien, Virtualisierung und Scrollen verwaltet. Ihre Länge ist allein kein Qualitätsmaß für dieses Repository; hier wurde kein Frameworkquelltext kopiert oder verändert.

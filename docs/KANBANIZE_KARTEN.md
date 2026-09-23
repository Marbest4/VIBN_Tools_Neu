# Kanbanize Karten

## Zwei getrennte Funktionen

Der Hauptreiter **Kanbanize Karten** enthält bewusst zwei voneinander unabhängige Abläufe:

1. **VIBN → Arbeitsplätze** liest virtuelle Inbetriebnahme-Karten und erstellt bzw. aktualisiert ihre eindeutig verknüpfte Arbeitsplatzkarte.
2. **Eigene Karte** erstellt auf ausdrücklichen Benutzerwunsch eine normale Kanbanize-Karte.

Beide Abläufe enthalten keine Lizenzanfrage- oder Lizenzdatenlogik.

## VIBN → Arbeitsplätze

### Voraussetzungen

- API-Zugriff kann Boards, Lanes, Spalten und Karten lesen.
- Das Quellboard „Virtuelle Inbetriebnahme“ enthält den Workflow **Team-Aufgaben** und liefert dessen `workflow_id` über die API.
- Im Zielboard darf der API-Zugriff Karten erstellen und die beiden geplanten Terminwerte bestehender generierter Karten ändern.
- Quellboard, Zielboard, Ziel-Lane und Zielspalte müssen gewählt sein.

### Bedienablauf

1. **Boards aktualisieren** drücken. Alternativ öffnet **Planansicht anzeigen** die feste Arbeitsplätze-Planansicht `1541` im Standardbrowser.
2. Quellboard „Virtuelle Inbetriebnahme“ sowie Zielboard „Arbeitsplätze“ und die gewünschte Zielposition auswählen.
3. **Prüfen** drücken und jede Zeile der Vorschau lesen. Neu anzulegende Karten sind in **Sync** standardmäßig markiert; Aktualisierungen bestehender Karten nicht.
4. Ausschließlich die gewünschten Zeilen in der Spalte **Sync** markieren. **Alle selektieren** und **Alle deselektieren** wirken nur auf schreibbare Vorschauzeilen.
5. Nur wenn Vorschau und Zielposition fachlich korrekt sind, **Synchronisieren** drücken. Nicht markierte Zeilen werden garantiert nicht geschrieben.

### Auswahlregel

Eine Quellkarte ist nur zulässig, wenn sie im Quellboard „Virtuelle Inbetriebnahme“ über ihre `workflow_id` eindeutig zum Workflow **Team-Aufgaben** gehört, ihr Titel `Grundinbetriebnahme` oder `Nachpflege` enthält, sie nicht `Vorlage` heißt und nicht in der Archivspalte liegt. Karten aller anderen Workflows bleiben auch dann ausgeschlossen, wenn ihr Titel passen würde. Kann **Team-Aufgaben** in der Boardstruktur nicht aufgelöst werden, wird die Vorschau mit einer Diagnose beendet; es gibt keinen unsicheren Fallback auf sämtliche Karten. Eine zusätzliche Vorlagenkarte ist für die Synchronisierung nicht erforderlich.

### Terminregel

| Zielwert | Berechnung |
| --- | --- |
| Start | Deadline der konkreten Quellkarte minus 14 Tage |
| Ende/Deadline | Deadline derselben VIBN-Quellkarte plus 56 Tage |

Der Start wird im bestehenden Startdatums-Custom-Field des Arbeitsplätze-Boards (Feld-ID `508`) gespeichert. Das Ende wird als reguläre Kanbanize-Deadline gespeichert. `actual_end_time` wird ausdrücklich nicht geschrieben, weil es einen tatsächlichen Abschluss statt eines Plantermins beschreibt.

Fehlt die Deadline einer Quellkarte, zeigt die Vorschau für genau diese Karte einen Konflikt. Andere gültige, markierte Karten können weiterhin synchronisiert werden.

Beim Prüfen werden Start und Deadline ausschließlich nach dem lokalen Kalendertag verglichen. Unterschiedliche Uhrzeiten am selben Tag erzeugen deshalb keinen unnötigen Updatevorschlag.

Die Vorschau zeigt grundsätzlich nur `dd.MM.yyyy`; Uhrzeiten werden in dieser Ansicht nicht dargestellt.

### Duplikat- und Änderungsregel

Die Zielkarte speichert die Quellkarten-ID als `custom_id` und Parent-Link. Dadurch erkennt ein zweiter Lauf zuverlässig dieselbe Karte.

| Situation | Verhalten |
| --- | --- |
| keine Zielkarte mit Quell-ID oder strukturierter Titelidentität | neue verknüpfte Zielkarte an ausgewählter Zielposition erstellen; der Name endet mit `*[Gen]*` |
| genau eine Zielkarte mit abweichendem Zeitplan | nur Startdatumsfeld und Deadline patchen |
| genau eine Zielkarte mit gleichem Zeitplan | unverändert |
| mehrere Karten mit gleicher Quell-ID und unterschiedlichen Rollen | dunkelgrün als zusammengehörige Kartenfamilie; CLIENT, CORE und weitere Rollen werden gezählt |
| Hauptkarte `*[Gen]*` plus kopierte Rollenkarte, aber noch ohne CORE | auswählbare Vorschau benennt ausschließlich die Hauptkarte in `*[Gen]* CORE` um |
| gleiche Quell-ID, gleiche Lane, exakt gleicher Titel, aber unterschiedliche Start-/Endtage | Konflikt mit konkretem Terminhinweis |
| exakt gleicher Titel mit unterschiedlichen Quellkarten-IDs | Konflikt |
| CORE innerhalb einer Quellkartenfamilie mehrfach vorhanden | Konflikt |
| fehlende Quell-Deadline | Konflikt für diese Quellkarte, keinerlei Änderung |

Die Titelanalyse liest vom Ende: `*[Gen]*`, `*[Gen]* CORE` und Zusatzkarten wie `… - CLIENT` werden auf eine gemeinsame Basisidentität und eine erweiterbare Rollenbezeichnung abgebildet. Historische Karten mit führendem `*[Gen]*` bleiben lesbar. Die Automatik verschiebt, löscht, beschreibt oder priorisiert keine vorhandene Karte. Die einzige Umbenennung ist die in der Vorschau einzeln wählbare CORE-Kennzeichnung der generierten Hauptkarte; der HTTP-Adapter sendet dafür ausschließlich das Feld `title`.

## Eigene Karte

Im Reiter **Eigene Karte** kann der Benutzer Board, Lane, Spalte, Titel, Beschreibung, Priorität, externe ID und Deadline wählen. Das Arbeitsplätze-Board, die Lane **Angelegt** und die Spalte **Backlog** werden – sofern über den API-Benutzer erreichbar – als Standard vorausgewählt. Der Entwurf wird vor dem HTTP-Aufruf validiert. Die manuelle Erstellung verwendet keine VIBN-ID und beeinflusst die Synchronisierung nicht.

## Tests und Codewegweiser

| Bereich | Datei |
| --- | --- |
| Modelle und HTTP-Vertrag | `VIBN_Tools.Core/Kanbanize/CardCreation.cs` |
| Auswahl-, Termin- und Duplikatregel | `VIBN_Tools.Core/Kanbanize/VibnWorkplaceSynchronization.cs` |
| Kanbanize-v2-Adapter | `VIBN_Tools.Infrastructure/Kanbanize/KanbanizeCardApiService.cs` |
| WPF-Vorschau und Status | `Application/VM/VibnWorkplaceSynchronizationVM.cs` |
| Kartenreiter | `Application/View/KanbanizeCardPage.xaml` |
| Fach- und Payloadtests | `Tests/CoreSmokeTests/Program.cs` – `VerifyVibnWorkplaceSynchronizationAsync` und `VerifyKanbanizeHttpWriteScopeAsync` |

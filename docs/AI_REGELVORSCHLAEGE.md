# AI-Test – Regelvorschläge

## Datenbasis

ContainerGeneration protokolliert direkte Änderungen als JSONL im Ordner `vibn_ai_data/actions`. Schema 2 enthält mindestens Zeitstempel, Aktionstyp, Eigenschaft, Vorher-/Nachherwert, stabile Signal-ID sowie einen SHA-256-basierten Quellschlüssel. Der Quellschlüssel enthält keine Klartextpfade. Alte Schema-1-Zeilen bleiben lesbar.

Erfasst werden Slotwechsel, Verschieben/Hinzufügen und direkte Änderungen an Signal, ID, Adresse, Datentyp, Notiz sowie Containername/-typ. Das sichtbare Arbeitsbereichsprotokoll bleibt davon getrennt: JSONL ist die strukturierte Auswertungsquelle.

## Vorschlagslogik

Die erste Stufe ist absichtlich deterministisch und nicht generativ. Sie gruppiert tatsächliche Slotkorrekturen nach:

- Komponententyp,
- exaktem Signaltext,
- bisherigem Slot,
- neuem Slot.

`Häufigkeit` ist die Anzahl beobachteter Änderungen. `Fälle` zählt unterschiedliche relevante Kombinationen aus Quelle und Signal-ID für denselben Typ/Signal/alten Slot. Die Konfidenz ist:

`unterstützende unterschiedliche Fälle / alle unterschiedlichen relevanten Fälle`

Damit führt ein mehrfaches Klicken im selben Fall nicht künstlich zu hoher Sicherheit. Gegensätzliche Zielslots senken die Konfidenz sichtbar. Die exakte Signalregel ist konservativ; Regex-Verallgemeinerungen werden erst dann sinnvoll, wenn genügend fachlich freigegebene Fälle und eine messbare Evaluierung vorliegen.

## Prüfung

Im Unterreiter **Regelvorschläge** können Vorschläge aktualisiert, angenommen oder abgelehnt werden. Der Status wird atomar in `rule_suggestion_reviews.json` gespeichert. **Annehmen verändert die Requirements-XML noch nicht.** Das verhindert, dass eine statistische Beobachtung ungeprüft produktive Regeln verändert.

Mit **XML-Vorschau** wird anschließend eine AutoCreate-Datei ausgewählt. Der Writer:

- übernimmt ausschließlich angenommene Slotvorschläge,
- blockiert widersprüchliche Zielslots für dieselbe Typ-/Signal-Kombination,
- prüft Ausgangsdatei und Vorschau gegen das eingebettete XSD,
- verwendet `match="exact"`, damit ein kurzer Signaltext keine anderen Signale als Teiltreffer erfasst,
- zeigt Typ, Signal sowie alten und neuen Slot vor dem Schreiben,
- prüft unmittelbar vor dem Schreiben den SHA-256-Stand der Quelldatei erneut.

**XML übernehmen** verlangt eine zweite explizite Bestätigung. Danach ersetzt `File.Replace` die unveränderte Quelldatei atomar und legt im selben Ordner eine eindeutig benannte `.vibn-backup`-Datei an. Ein zwischen Vorschau und Übernahme extern geändertes XML wird nicht überschrieben.

Die exakte Regel wird als isolierte Override-Komponente geschrieben. Bestehende Definitionen des Komponententyps erhalten für genau dieses vollständige Signal eine Exclusion; die Override-Komponente ordnet es genau einem Zielslot zu. Dadurch erzeugt die bisherige Eindeutigkeitsprüfung keinen Mehrfachtreffer. Eine spätere Änderung desselben Vorschlags ersetzt den alten generierten Override statt eine konkurrierende Regel zu hinterlassen.

## Vorschläge für neue Container

Der Unterreiter **Container-Vorschläge** wertet wiederkehrende `Add`-, `Move`- und `ContainerAndSlot`-Aktionen aus. Er schlägt einen neuen Containertyp nur vor, wenn mindestens zwei unterschiedliche Quell-/Zielcontainer-Fälle vorliegen. Ein Slot gilt als wiederkehrend, wenn er in mindestens 60 Prozent dieser Fälle vorkommt. Konfidenz, Fallzahl, Aktionszahl, Beispielcontainer und vorgeschlagene Slots bleiben in der Oberfläche sichtbar.

Auch ein angenommener Vorschlag erzeugt oder ändert keine Requirements-XML und startet keine FEE-Generierung. Annahme und Ablehnung werden getrennt in `container_suggestion_reviews.json` gespeichert. Erst eine fachliche Freigabe und eine explizite Implementierung im gemeinsamen Containerkatalog dürfen einen neuen Typ produktiv aktivieren. Generalisierte Regex-Regeln bleiben aus demselben Grund bewusst offen.

## Automatische Abdeckungsmatrix

Der Unterreiter **Abdeckungsmatrix** vergleicht eine ausgewählte Requirements-XML mit dem gemeinsamen Metadatenkatalog des Vorwärtsgenerators und dem Typkatalog der FEE2Container-Rekonstruktion. Angezeigt werden unter anderem Requirements-Komponente, Laufzeitklasse, deklarierte/unterstützte Slots, unbekannte beziehungsweise fehlende Slots und die Unterstützung beider Richtungen. Die Matrix findet strukturelle Lücken früh, ersetzt aber keinen Live-Test der FEE-SDK-Verknüpfungen.

## Messbarer Performance-Modus

Der Performance-Modus ist standardmäßig ausgeschaltet. Nach Aktivierung misst er die Gesamtdauer und den Erfolg der instrumentierten Abläufe ContainerGeneration, Container2FEE Visual, TIA und Rockwell. Messungen werden als JSONL unter `%LOCALAPPDATA%\VIBN_Tools\diagnostics\performance` gespeichert; die Oberfläche zeigt Anzahl, Mittelwert, Maximum, letzte Dauer und Fehlerzahl pro Vorgang. **Sitzung leeren** entfernt nur die im Arbeitsspeicher angezeigte Zusammenfassung; bereits geschriebene JSONL-Messdateien bleiben für Vergleiche erhalten.

Die Messung ist bewusst opt-in und enthält Vorgangsname, Dauer, Erfolg und einen knappen Status, aber keine API-Schlüssel oder Passwörter. Sie liefert eine reproduzierbare Ausgangsbasis für Optimierungen; feste Grenzwerte müssen erst anhand repräsentativer Kundenprojekte festgelegt werden.

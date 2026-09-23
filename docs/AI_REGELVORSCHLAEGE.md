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

Weiterhin offen sind fachlich generalisierte Regex-Regeln und Vorschläge für vollständig neue Komponententypen. Dafür reichen einzelne Bedienaktionen nicht als belastbare Datenbasis; solche Regeln dürfen erst nach separater Evaluation und fachlicher Freigabe entstehen.

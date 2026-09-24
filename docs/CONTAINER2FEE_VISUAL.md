# Container2FEE Visual

## Zweck und Abgrenzung

Der Reiter **Container2FEE Visual** ist ein zusätzlicher, levelgeschützter Arbeitsbereich. Der bestehende Reiter **Container2Fee** und dessen Ablauf bleiben unverändert. Beide Wege verwenden am Ende dieselben Containerklassen und denselben `ContainerToFeeService`; dadurch entsteht kein zweiter Generator mit abweichendem Verhalten.

Die visuelle Seite kann eine Container-XML bereits ohne FEE-Verbindung lesen und als Plan darstellen. Erst das Laden vorhandener SimObjects und **Start Generation** benötigen die in Project Settings bestätigte FEE-Verbindung.

![Visueller Container2FEE-Plan mit synthetischen Testdaten](screenshots/container2fee-visual.png)

## Bedienablauf

1. **XML öffnen** wählen. Die Quelldatei wird nur gelesen und nicht verändert.
2. Links Container prüfen. Unter jedem Container stehen **Logic**, **Signals** und **SimObjects** als klar getrennte Ebenen; Signal- und SimObject-Knoten nennen die tatsächlich gefundene oder manuell zugeordnete FEE-Entität.
3. Nach erfolgreicher FEE-Verbindung **FEE aktualisieren** drücken. Noch freie Ziele werden wie im bisherigen Ablauf anhand von identischem Komponentennamen und kompatiblem Typ automatisch zugeordnet. Zusätzlich werden vorhandene Signale und rücklesbare Containerbestände verglichen.
4. Ein FEE-SimObject von rechts auf ein kompatibles Ziel in der Mitte oder direkt auf den zugehörigen SimObject-Knoten im Baum ziehen. Ein Einzelziel wird ersetzt, ein Mehrfachziel ergänzt. Zugeordnete SimObjects erscheinen als Unterknoten des Ziels. Mit `Strg`/`Umschalt` können mehrere Objekte ausgewählt und gemeinsam auf ein Mehrfachziel gezogen werden. Der Drop auf die Gruppe **SimObjects** ist zulässig, wenn für die gesamte Auswahl genau ein kompatibles Ziel existiert. Dasselbe FEE-Objekt kann nie gleichzeitig mehreren Containern gehören. Mehrere ausgewählte FEE-Signale können auf einen vorhandenen Signal-Knoten verteilt oder auf die Gruppe **Signals** gezogen und damit als zusätzliche Planeinträge ergänzt werden. Ergänzte Signale bleiben bis zur expliziten Auswahl eines erwarteten Slots rot markiert. Nach dem Drop bleiben Auswahl, Aufklappzustand und Scrollposition der Struktur erhalten.
5. In der linken Struktur pro vollständigem Container festlegen, ob er verarbeitet wird. **Alle selektieren** und **Alle deselektieren** ändern diese Auswahl gemeinsam. Kindobjekte erben die Containerentscheidung, weil Logik, Signale und technische Hilfsobjekte keine unabhängig ausführbaren Legacy-Einheiten sind.
6. **Fehlende SimObjects bei der Generierung erzeugen** ist standardmäßig aktiv. Die Einstellung kann je Container oder über **Alle/Keine** für alle erzeugbaren Container geändert werden. Ein ausgewählter Container mit SimObjectTarget benötigt entweder eine grüne Zuordnung oder diese Erzeugungsoption; andernfalls bleibt das Ziel dunkelrot und die Validierung erklärt den Fehler.
7. Bei einem Signal mit nicht mehr gültigem Slot den Signalknoten auswählen und unter **Erwarteten Slot korrigieren** einen der vom tatsächlichen Runtime-Container unterstützten Slots wählen. Die Quell-XML bleibt unverändert; die Korrektur gilt für Validierung, Ausführung und Provenienz. Die mittlere Spalte zeigt außerdem sämtliche Signale des ausgewählten Containers. Zusätzliche und importierte Signale können dort, über `Entf` oder das Kontextmenü aus dem wirksamen Plan entfernt werden. **Generieren / nicht generieren** im Kontextmenü schaltet den gesamten zugehörigen Container. Änderungen mit **Rückgängig/Wiederholen** korrigieren und über **Plan speichern** sichern. **Container.xml speichern** schreibt zusätzlich eine eigenständige XML mit der wirksamen Containerauswahl, Slotkorrekturen, Ergänzungen und Löschungen.
8. Für eine vollständige Neuerzeugung **Start Generation** drücken. Fehlerhafte Knoten sind rot markiert; ihre Tooltips erklären sowohl das vorgesehene FEE-Objekt beziehungsweise die gesuchte Verknüpfung als auch den Fehler. Wenn Container/Logiken bereits existieren, kann stattdessen **Nur SimObjects verknüpfen** verwendet werden. **Nur Signale verknüpfen** verwendet ausschließlich Variablen des ausdrücklich ausgewählten vorhandenen Interfaces und ergänzt Verknüpfungen an eindeutig gefundenen Logiken beziehungsweise Cabinet-Elementen; dieser Modus erzeugt weder Variablen noch FEE-Objekte.

Technische Objekte sind im Baum standardmäßig eingeklappt. **Alles aufklappen/Alles zuklappen** wirkt auf die kombinierte Container- und Objektstruktur. Verfügbare FEE-SimObjects und gefundene FEE-Signale besitzen getrennte, unabhängig scrollbar und filterbar dargestellte Spalten. **Nur kompatible Objekte** bezieht sich auf das aktuell ausgewählte Ziel. Die früher schwer lesbare Kantenansicht ist als aufklappbares technisches Detail mit lesbaren Quell-/Zielnamen verfügbar.

## Sidecar-Datei

Benutzeränderungen werden nicht in die Container-XML geschrieben. Standardmäßig entsteht daneben:

```text
Container.xml.container2fee.visual.json
```

Gespeichert werden ausschließlich Quellfingerabdruck, Ziel-/FEE-Zuordnungen, ausdrücklich bestätigte Signalzuordnungen, zusätzliche oder aus dem wirksamen Plan entfernte Signale, geprüfte Slot-Overrides, deaktivierte SimObject-Erzeugung und abgewählte Container. Schema 8 liest weiterhin Sidecars aus Schema 1–7 und migriert deren frühere Positivliste auf den neuen sicheren Standard. Die entfernte Einstellung **Signale erzeugen** wird beim Laden alter Sidecars ignoriert und als Information ausgewiesen. Der Schreibvorgang erfolgt über eine temporäre Datei und anschließendes Ersetzen. Beim erneuten Öffnen wird der Sidecar automatisch angewendet, sofern der SHA-256-Fingerabdruck der XML noch stimmt. Nach einer XML-Änderung werden alte Zuordnungen nicht stillschweigend übernommen.

## Drag-and-drop-Regeln

- Zulässig sind nur vorhandene FEE-SimObjects, deren Wrapper-Typ dem `AllowedType` des unveränderten Legacy-Containers entspricht.
- Einzelziele besitzen höchstens eine, Mehrfachziele mehrere Zuordnungen.
- Eine Objekt-GUID ist im gesamten Plan höchstens einmal zugeordnet.
- Signal-/Slot- und Parent-/Child-Verknüpfungen werden sichtbar gemacht, aber nicht frei umverdrahtet. Die Identität eines vorhandenen FEE-Signals kann bewusst einem Plan-Signal zugewiesen werden. Zusätzlich darf der Slot eines Signal-Knotens ausschließlich auf einen Wert aus der Dropdownliste des konkreten Runtime-Containers geändert werden; freie oder unbekannte Werte sind nicht zulässig.
- Eine Mehrfachzuordnung auf einen vorhandenen Signalknoten benötigt entsprechend viele freie Einträge desselben effektiven Slots. Beim Drop auf die Gruppe **Signals** werden dagegen neue Sidecar-Einträge erzeugt und erst im effektiven Laufzeitdokument ergänzt; die Quell-XML bleibt unverändert. `PLC_IN_`-Slots dürfen mehrfach belegt werden, exklusive `PLC_OUT_`- und sonstige Slots nicht.
- Das Entfernen einer Zuordnung löscht kein Objekt in FEE.

## Statusfarben und Link-only

- Ein einzelnes Objekt oder Signal ist **grün**, wenn es eindeutig in FEE gefunden beziehungsweise in der aktuellen Sitzung erfolgreich erzeugt wurde. Gruppen und Container aggregieren ihre Kinder mit der Priorität Rot vor Gelb vor Grün; ein Parent wird daher nur grün, wenn sämtliche Unterknoten grün sind.
- Ein gefundenes beziehungsweise manuell ausgewähltes Objekt oder Signal ist **helllila**, solange die konkrete Slotverknüpfung noch nicht durch den ausgeführten Generator bestätigt wurde.
- Ein Element ist **gelb**, wenn es bei der vollständigen Generierung erzeugt oder vervollständigt werden soll.
- Es ist **dunkelrot**, wenn weder Zuordnung noch Erzeugungswunsch vorliegt. Die Validierung nennt das konkrete Ziel und mögliche Korrekturen.
- Ein Eintrag unter **Verfügbare FEE-SimObjects** wird grün, sobald er zugeordnet ist, und nennt das Ziel.
- Ein Eintrag unter **Gefundene FEE-Signale** wird grün, sobald er zugeordnet ist. Rot kennzeichnet doppelte Signalnamen oder dieselbe FEE-GUID an mehreren Container-Einträgen; unverwendete eindeutige Signale behalten den neutralen Hintergrund.
- **FEE aktualisieren** liest zusätzlich vorhandene `LogicObject`-, `Cabinet`- und `CabinetElement`-Bestände. Logiken werden über Komponentenname und normalisierte Logikdefinition erkannt; Cabinet-Elemente über Komponentenname und Elementdefinition. Ein eindeutiger Treffer färbt den entsprechenden Baumknoten grün und zeigt die FEE-GUID an. Mehrere Treffer bleiben rot und werden als mehrdeutig gekennzeichnet. Der Cabinet-Sammelknoten gilt auch dann als vorhanden, wenn die SDK zwar das passende CabinetElement, aber keinen separat abfragbaren Cabinet-Knoten liefert.
- Signale eines unbekannten Containertyps bleiben unter **Unknown → Signals** gelb, wenn sie im Unknown-Interface erzeugt werden sollen oder bereits gefunden beziehungsweise ausdrücklich zugeordnet wurden. Rot ist dort nur noch einem konkreten Validierungs- oder Identitätsfehler vorbehalten.
- Ein reiner Signalcontainer ohne Logik-, SimObject- oder Hilfsobjektbeziehung bleibt gelb. Tooltip und Prüfhinweis erklären ausdrücklich, dass nur Interface-Signale verarbeitet werden und kein Szenenobjekt entsteht.

**Nur SimObjects verknüpfen** erzeugt keine BasicFrames, Interfaces, Signale, Logiken oder Container. Der Befehl verwendet die in **Model Validation → Update Objects** eingelesenen `FeeLogic`-Objekte. Für jeden ausgewählten Container muss genau ein vorhandenes LogicObject mit identischem Komponentennamen existieren. Fehlende oder doppelte Logiknamen sowie nicht mehr verfügbare SimObjects brechen vor dem ersten Schreibzugriff mit einer präzisen Fehlermeldung ab. Der Vorgang ist auf `ILogicSimObjectOwner` begrenzt; reine SimObject-Container besitzen keine bestehende Logik, an die in diesem Modus verknüpft werden könnte.

Dieser Link-only-Modus benötigt keine Interface-Auswahl, weil er weder Signale erzeugt noch verändert. Er wird erst aktiv, wenn ein Plan, eine FEE-Verbindung, mindestens ein ausgewählter Container und mindestens eine vorhandene Zielzuordnung vorliegen. Die Tooltips von **FEE aktualisieren**, **Start Generation** und **Nur SimObjects verknüpfen** nennen jeweils die erste konkret fehlende Voraussetzung oder den ersten blockierenden Validierungsfehler.

## ModelValidation-Vertrag

Vor der vollständigen Erzeugung prüft Container2FEE Visual die aus dem ContainerFile eindeutig ableitbaren Pflichtbeziehungen der vorhandenen `ModelValidation`. Fehlt eine erforderliche Signal- oder SimObject-Beziehung, verlangt eine Best-Effort-Generierung eine eindringliche, standardmäßig verneinte Bestätigung. Bei **Ja** läuft derselbe Startvorgang unmittelbar weiter; ein zweiter Klick ist nicht erforderlich. Der erste erzeugte BasicFrame trägt den Zusatz **Trotz Validierungsfehlern erstellt**, hält die Fehler weiterhin als `vibn.validation.*`-Properties und erhält pro Fehler einen eigenen untergeordneten BasicFrame mit Code, Meldung und Knotenbezug. Nicht deterministische Laufzeitkonflikte wie mehrere widersprüchliche Signaltreffer bleiben gesperrt, bis sie eindeutig aufgelöst wurden.

Alle geschriebenen Variablen- und Slotverknüpfungen werden über die FEE-API zurückgelesen. Eine nicht übernommene Verbindung gilt als Fehler. Beim Stopper wird `Floor.CollisionSlot` für neue und vorhandene Floors vor dem Verbinden aktiviert und ebenfalls zurückgelesen. Die Größen bleiben die Werte des bisherigen Container2FEE-Generators: Floor `0,01 × 0,2 × 0,05`, Sensor `0,01 × 0,03 × 0,01`, Surface `2 × 0,5 × 0,05`, MotionJoint/Button `0,5 × 0,5 × 0,5` und PickAndPlace `0,1 × 0,1 × 0,1`. Fehlende Bewegungsparameter erhalten prüfbare Startwerte.

Nicht aus dem ContainerFile ableitbar sind reale Positionen, Pick-/Drop-Marks und die konkrete BeltControl-Achsbeziehung. Diese werden nicht erfunden. Nach deren fachlicher Festlegung ist **Model Validation → Update Objects** als Live-Abnahme auszuführen.

## Codeaufteilung

| Bereich | Verantwortung |
| --- | --- |
| `ContainerToFeeVisual/Domain` | stabile Plan-, Knoten-, Kanten-, Ziel- und Zuordnungsmodelle |
| `ContainerToFeeVisual/Planning` | sichere XML-Auswertung und Metadaten der bestehenden Containerklassen |
| `ContainerToFeeVisual/Persistence` | versionierter JSON-Sidecar mit Fingerabdruckprüfung |
| `ContainerToFeeVisual/Discovery` | FEE-Objekterkennung ohne SDK-Objekte an die View weiterzugeben |
| `ContainerToFeeVisual/Execution` | gemeinsame Runtime-Bindung, vollständige Legacy-Generierung und getrennte Link-only-Ausführung |
| `ContainerToFeeVisual/Services` | Orchestrierung, Validierung und Undo/Redo |
| `Application/VM/ContainerToFeeVisualPageVM.cs` | UI-Zustand, Commands, Filter und Status |
| `Application/View/ContainerToFeeVisualPage.xaml` | dreigeteilte WPF-Ansicht und Drag-and-drop-Ziele |

## Bewusste technische Grenzen

Vor jeder vollständigen Generierung werden FEE-SimObjects, Interfaces und Variablen neu eingelesen; dadurch arbeitet auch ein zweiter Start nicht mit dem veralteten Snapshot des ersten Laufs. Objekt- und Interfaceeigenschaften werden begrenzt parallel gelesen. Die eigentlichen FEE-Schreiboperationen der Container laufen dagegen bewusst seriell und über eine gemeinsame Sperre, weil parallele Szenenmutationen derselben SDK-Verbindung zu Stillständen führen können. Eine dauerhaft sichtbare Statushilfe weist darauf hin, nach externen Modelländerungen zunächst **Model Validation** und anschließend erneut **FEE aktualisieren** auszuführen. Der normale Visual-Refresh prüft vollständige grüne Container nur anhand exakter Container2FEE-Provenienz; die Strukturrekonstruktion alter Projekte wird im Reiter **FEE2Container** ausgeführt. `SignalResolutionPlanner` berücksichtigt für vorhandene Signale ausschließlich das in **Gefundene FEE-Signale** ausgewählte Interface; ohne Auswahl bleibt die Liste leer und fehlende Signale werden über das Grob Generation Interface erzeugt. Ein vorhandenes Signal wird nur bei eindeutiger, widerspruchsfreier Identität wiederverwendet und niemals aktualisiert. `EXISTING_SIGNAL_IDENTITY_CONFLICT` bedeutet konkret: Derselbe Tag wurde gefunden, aber Adresse oder symbolischer Pfad des ContainerFiles widerspricht dem FEE-Treffer. Die Fehlermeldung nennt erwartete und vorhandene Quelle. Eine ausdrückliche Drag&Drop-Zuordnung darf diesen Konflikt auflösen, ohne das FEE-Signal zu verändern. Fehlen Signale, wird zuerst eine bestehende Interfaceinstanz mit der stabilen Provider-GUID `a6222164-be37-49de-b760-9b1c97c320bb` wiederverwendet. Nur wenn keine solche Instanz existiert, wird eine neue zeitgestempelte Instanz angefordert. Die FEE-Signalliste ist nach Tag, Adresse/Pfad, Interface und Datentyp filterbar.

Der bestehende FEE-Executor unterstützt keinen transaktionalen Rollback. Wird eine laufende SDK-Schreiboperation abgebrochen, kann bereits erzeugter Inhalt bestehen bleiben und muss in FEE geprüft werden. Der Abbruch wird vor und nach allen Container-, Signal-, BasicFrame- und Fehlerknotenphasen geprüft; einen bereits laufenden einzelnen SDK-Aufruf kann das SDK selbst mangels `CancellationToken` nicht hart abbrechen. Die UI kehrt am nächsten sicheren Prüfpunkt zuverlässig aus dem Busy-Zustand zurück. Die Pipeline führt zuerst alle read-only Prüfungen und danach die fehlenden Signalvariablen aus; erst anschließend entstehen BasicFrame, Logiken und SimObjects. Scheitert die SDK-Anlage einer späteren Variablen, können zuvor angelegte Variablen bestehen bleiben. Eine freie grafische Neuverdrahtung oder unabhängige Auswahl einzelner Hilfsobjekte ist nicht Bestandteil dieser Version.

Die Legacy-Bezeichnungen `PLC_IN_PartPresent` und `PLC_IN_NoPartPresent` werden für `GrobSensor` kompatibel auf Kanal 1 abgebildet; bei zwei gleichnamigen Einträgen erfolgt die Zuordnung auf Kanal 1/2. Die Validierung nennt bei einem wirklich unbekannten Slot jetzt zusätzlich alle zulässigen Slotnamen.

`SensorX` aus `AutoCreate_Master.xml` ist als bekannter signal-only Container abgebildet. Seine Slots `PLC_orientation_OK` und `PLC_orientation_nOK` werden validiert, ohne eine in Requirements oder ModelValidation nicht definierte FEE-Logik zu erfinden. Nicht mehr vorhandene Slots lassen sich am jeweiligen Signal über eine typgebundene Dropdownliste korrigieren. Der Sidecar speichert nur den Override; Quelldatei, effektiver Ausführungsplan und Provenienz werden nicht unbemerkt vermischt. Ein freier Slottext ist nicht möglich.

Vor einer Neuerzeugung werden vorhandene Signale, Logiken, SimObjects und Cabinet-Elemente wiederverwendet. Logik und Cabinet-Element müssen über Komponentennamen und Definition eindeutig sein; mehrere Treffer stoppen den Lauf, statt ein weiteres Duplikat zu erzeugen. Auch MotionJoints werden bei exakt passendem Komponentenname und kompatiblem Typ einem Mehrfachziel zugeordnet; ihre CAD-Unterstruktur wird dabei weder verschoben noch zusammengeführt. Für mehrere MotionJoints werden Befehlsausgänge der Logik (`InTarget`/`InVelocity`) an alle ausgewählten Joints verteilt, während der Rückwert `OutValue` nur vom ersten Joint in die Logik geführt wird. Wird ein zuvor gespeichertes SimObject oder Signal extern in FEE gelöscht, entfernt **FEE aktualisieren** nur die veraltete GUID-Zuordnung; der gelbe Planknoten kann anschließend wieder erzeugt beziehungsweise neu zugeordnet werden.

Neue Containerobjekte erhalten die versionierten Properties `vibn.container-object.*` im `TagComponent.TagEntries`. `MarkComponent` wird für neue Provenienz nicht mehr beschrieben. Alte Mark-Einträge bleiben ausschließlich als rückwärtskompatibler Lesefallback erhalten.

Mehrere Einträge auf demselben `PLC_IN_`-Slot sind zulässig. Bei einem einfachen Einzel-Slot erzeugt Container2FEE je Signal ein unsichtbares `FeeSimpleMove` und führt dessen Eingang gemeinsam mit dem Logik-Slot zusammen; so wird kein Signal überschrieben. Containerklassen, die bereits eine Signalliste für diesen Eingang besitzen, verwenden weiterhin ihre eigene gleichwertige Move-Abbildung. Mehrfach belegte `PLC_OUT_`-Slots sowie sonstige doppelte Slots werden mit Slotname und Anzahl vor dem ersten FEE-Schreibzugriff abgewiesen.

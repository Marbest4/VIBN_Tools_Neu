# Release-Abnahmecheckliste

Diese Liste auf einem GROB-Desktop mit Netzwerkzugriff, FEE, Kanbanize-Berechtigung und mindestens einer unterstützten TIA-Installation ausführen.

## Automatische Basis

- [ ] `dotnet build VIBN_Tools_App.sln --configuration Release` hat keine Fehler.
- [ ] `VIBN_Tools.ContainerGeneration` und `VIBN_Tools.SharedWpf` erscheinen als eigene Solution-Projekte; das Hauptprojekt kompiliert deren Ordner nicht zusätzlich über Wildcards.
- [ ] `Tests/CoreSmokeTests` ist erfolgreich.
- [ ] `Tests/ContainerGenerationSmokeTests` liest alle sieben bereitgestellten Interface-/Container-Paare, bilanziert jedes Signal, hält die verifizierten Zuordnungs-/Slot-Untergrenzen ein und meldet `SixLabors.Fonts 1.0.1.0`.
- [ ] `Tests/UiStartupSmokeTests` ist erfolgreich und meldet keine Binding-Fehler.
- [ ] `Tests/Test-TiaHardwareTraversal.ps1` bestätigt Gerätegruppen, Local Session und exakt `E62–73/A62–67` sowie `E74–79/A68–79`.
- [ ] `Tests/TiaLiveRead` liest aus dem geöffneten `Projekt1.ap20` genau eine PLC, drei Teilnehmer und sechs eindeutige adressführende Modulzeilen, ohne das Projekt zu speichern.
- [ ] Anwendung startet ohne XamlParseException.
- [ ] Anwendung startet ohne FEE-Verbindungs-/Interface-Abfrage und ohne entsprechende Fehlermeldung; die gemeinsame `CoreApi`-Instanz wird vorbereitet, lädt aber keine Interfaces.

## Rollen und Navigation

- [ ] Nicht-Level7-Benutzer sehen CAD Wizard, Container Generation und Container2Fee nicht.
- [ ] Level7 sieht genau diese drei Bereiche zusätzlich.
- [ ] Level8 sieht außerdem Kanbanize Karten und AI-Test, aber keine Administration.
- [ ] Level9 sieht den Hauptreiter Administration und kann Rollen ändern.
- [ ] `lutzma` wird als Level9 erkannt und kann nicht verändert/entfernt werden.
- [ ] Eine Änderung, die weniger als zwei Level9-Benutzer hinterließe, wird abgewiesen.
- [ ] Navigation einklappen reduziert die linke Leiste sichtbar auf Symbole; Ausklappen und Alt+N stellen die volle Breite wieder her.

## Project Settings und ViCo

- [ ] Project Settings zeigt nur erreichbare PCs und der Filter wirkt sofort.
- [ ] Ein fehlgeschlagener FEE-Connect zeigt nicht fälschlich „verbunden“.
- [ ] **Connect** ruft die FEE-SDK genau einmal auf und lässt einen laufenden SDK-Handshake ohne anwendungsseitigen Timeout oder automatisches Disconnect bestehen; **Disconnect** bleibt eine bewusste Benutzeraktion.
- [ ] `localhost` bleibt nach der Online-PC-Aktualisierung ausgewählt und Connect verwendet die gespeicherten FEE-Zugangsdaten; die gebundene Serverliste erzeugt keinen CollectionView-Threadfehler.
- [ ] Project Settings zeigt verwendete SDK- und lokal installierte FEE-Version; eine künstlich abweichende Version wird rot hervorgehoben.
- [ ] Von mehreren lokalen Versionsordnern zählt nur ein Ordner mit `Bin\FS.SDK.dll`; höhere unvollständige Ordner werden ignoriert.
- [ ] Bei mehreren vollständigen SDKs listet `Prepare-Development.cmd` alle Versionen absteigend; Enter wählt die neueste und eine Nummer wählt nach VS-Neustart exakt den angegebenen Ordner.
- [ ] Project Settings zeigt FEE-Zugang, API-Key und RDP-Passwort jeweils als konfiguriert/nicht konfiguriert; Speichern gilt ohne Neustart und die Löschen-Buttons entfernen nur die eigene Gruppe.
- [ ] Jede PasswordBox aktualisiert ihr ViewModel beim Tippen und bleibt nach einer ViewModel-Aktualisierung gebunden; aktuell eingegebene FEE-Daten können direkt für **Connect** verwendet werden.
- [ ] Lokale Automatisierungsinstallationen stehen am Seitenende; gleiche Produkt-/Versionsfunde werden unabhängig von Registryquelle und leerem Pfad nur einmal angezeigt.
- [ ] Im normalen interaktiven Windows-Profil erscheinen die Ziele `GROB/VIBN_Tools/FeeUsername`, `GROB/VIBN_Tools/FeePassword`, `GROB/VIBN_Tools/KanbanizeApiKey` und `GROB/VIBN_Tools/RemoteDesktopPassword` im Credential Manager; App-Neustart liest sie, Löschen entfernt sie. Der Codex-Dienstkontext konnte diesen Live-Test wegen Windows-Fehler 1312 (keine Anmeldesitzung) nicht ausführen.
- [ ] ViCo-Countdown startet mit dem gespeicherten Intervall neu, pausiert ohne API-Key und führt bei Ablauf genau einen Kanbanize-Abruf aus.
- [ ] Hauptfenster bleibt auf 1366 × 768 bedienbar; Project Settings und ViCo zeigen bei Bedarf Scrollleisten ohne die DataGrid-Virtualisierung zu verlieren.
- [ ] IBN startet kompakt mit ausschließlich PC/Online/Projekte; Details, RDP und Zugangsdaten bleiben über die Expander auf 480 × 340 erreichbar.
- [ ] Ohne FEE-Verbindung sind alle dokumentierten FEE-Aktionen grau, nicht ausführbar und zeigen den Tooltip „Keine Verbindung zu FEE vorhanden.“.
- [ ] ViCo-Suche findet PC, Benutzer und Projekt mit demselben Suchfeld.
- [ ] Alle ViCo-Spalten lassen sich über das Checkbox-Dropdown einzeln persistent ein-/ausblenden und sortieren.
- [ ] Planung und In Arbeit stehen in getrennten Spalten ohne `[P]`/`[W]`; Start-/Enddatum und die ausklappbare Swimlane **Abgeschlossen** werden korrekt zugeordnet.
- [ ] Klick beziehungsweise Kontextaktion öffnet anhand der gespeicherten Karten-ID genau die sichtbare Kanbanize-Karte.
- [ ] Frei ist grün, Belegt rot; Online ist grün, Offline rot.
- [ ] Offline-PCs lassen RDP, Anmeldedialog und PC-Projektordner sichtbar aber deaktiviert; vorhandene Serverpfade für Simulation, PLC und Planung bleiben nutzbar.
- [ ] Strg-/Umschalt-Mehrfachauswahl führt RDP-/Pfadaktionen für alle selektierten Zeilen aus; Kontextaktionen erhalten eine bestehende Mehrfachauswahl.
- [ ] Ohne Planung/In-Arbeit-Karte sind Simulation, PLC-Projekt und Planung in Button und Kontextmenü deaktiviert.
- [ ] Projektstart/-ende erscheinen ausschließlich als lokales Datum ohne Uhrzeit oder Zusatztext; fehlende Werte bleiben leer. Die Pfadanzeige lässt sich markieren und kopieren.
- [ ] RDP-Sitzungsrechte fehlen: Anzeige lautet „Nicht abrufbar“, nicht „offline“.
- [ ] Automatischer Remote-Button nutzt den Kanbanize-Benutzer; der zweite Button zeigt den Windows-Anmeldedialog.
- [ ] Eine vorhandene KONFIGURATION-Unteraufgabe lässt sich bearbeiten und zurückspeichern; keine andere Karteninformation ändert sich.
- [ ] Enter in einem KONFIGURATION-Wertefeld speichert ohne zusätzlichen Button und aktualisiert die sichtbare Tabellenzeile erst nach erfolgreicher Board-Antwort.

## Kanbanize

- [ ] Vorschau verwendet Quell- und Zielboard, Lane und Spalte korrekt.
- [ ] Start ist Quell-Deadline minus 14 Tage.
- [ ] Ziel-Deadline ist Quell-Deadline plus 56 Tage.
- [ ] Nur in der Vorschau markierte Sync-Zeilen werden erstellt oder aktualisiert.
- [ ] Neue Karten sind nach dem Prüfen markiert; Deadline-Updates nicht. Alle selektieren/deselektieren funktioniert.
- [ ] Unterschiedliche Uhrzeiten am selben lokalen Kalendertag erzeugen kein Termin-Update.
- [ ] Ein zweiter Lauf erzeugt keine Duplikate.
- [ ] Mehrdeutige Zielkarte führt zu Konflikt ohne Änderung.
- [ ] Bestehende generierte Karte ändert nur Startfeld und Deadline, nicht Titel/Position/Beschreibung.
- [ ] Eigene Karte kann unabhängig erstellt werden.
- [ ] Auf dem Board **Virtuelle Inbetriebnahme** werden ausschließlich Quellkarten aus dem Workflow **Team-Aufgaben** berücksichtigt; eine gleichnamig passende Karte in einem anderen Workflow bleibt ausgeschlossen.

## TIA und SpecialDevices2FEE

- [ ] TIA-Version, Attach und PLC-Auswahl funktionieren.
- [ ] **Auswahl konfigurieren** verarbeitet einen statischen Auswahlsatz ohne Dynamic-Binder-Ausnahme; **Was wird geändert?** zeigt alle zehn Parameter und die Linear-/Rotatorikregel.
- [ ] **Gesamtes TIA-Projekt speichern** ist von der Konfiguration getrennt und weist darauf hin, dass `Project.Save()` alle offenen Projektänderungen persistiert.
- [ ] **ViCo-Bibliothek → Was wird gemacht?** beschreibt Voraussetzungen sowie Überschreiben/Speichern beim Import, Achsen-/AxisXML-Option und read-only TIA-Export vollständig.
- [ ] Die einzige Hardwareansicht unter SpecialDevices2FEE gruppiert gleiche Gerätenamen und zeigt Hardware-ID, GSDML, IP, Modultyp, Firmware, E-/A-Bereich und -Länge, Präfix, Logik und Status; #, Tiefe, Modul, Parent, Slot/Subslot, Pfad und Objektklasse sind ausgeblendet.
- [ ] Die Logikauswahl bietet **Keine Logik**; eine entsprechend gesetzte Zeile gelangt auch mit aktivem Übernehmen-Haken nicht in die Warteschlange.
- [ ] Nach vollständig erfolgreicher SpecialDevices2FEE-Erzeugung ist der Root in FEE2SpecialDevices sichtbar; ein absichtlich fehlgeschlagener Teilvorgang ist nicht als gültige Quelle markiert.
- [ ] JSON-Export enthält Präfix, Hersteller, Gerätetyp, E-/A-Startbyte und alle Signal-GUIDs; eine nachträglich geänderte FEE-Variable wird über ihre GUID aktualisiert.
- [ ] Ältere/manuelle BasicFrames werden gezählt, aber nicht heuristisch als Special Device exportiert.
- [ ] Ein gültiges FEE2SpecialDevices-JSON wird über den bestehenden Gerätekatalog genau einmal in die Warteschlange übernommen; unbekannter Typ und doppelte Präfix-/Herstellerkombination werden abgewiesen.
- [ ] Abweichende FEE-Signale erzeugen beim Queue-Import einen sichtbaren Prüfhinweis und überschreiben die katalogisierte Gerätedefinition nicht.
- [ ] Eine geänderte Logik-/Adresszuordnung wird gespeichert und nach erneutem Auslesen wiederhergestellt.
- [ ] Der reale PN/PN Coupler X2 zeigt genau zwei PROFIsafe-Zeilen, keine adresslosen Kopf-/Interfacezeilen und Byte-Längen 12/6 sowie 6/12.
- [ ] Das verifizierte `Projekt1.ap20` zeigt `KRC4` unter `192.168.1.4`, `PN-PN-Coupler` unter `192.168.0.3` und `PN-PN-Coupler_1` unter `192.168.0.2`; kein Rack-/Gerätekopf-Proxypfad erzeugt eine doppelte Zeile.
- [ ] Geräteüberschrift zeigt realen Gerätenamen und -typ; IP, PROFINET-Name und Firmware werden vom Geräte-/Interfaceknoten auf beide adressführenden Module übernommen.
- [ ] `TIA trennen / abbrechen` beendet Attach/Session, leert Listen und beendet TIA Portal selbst nicht.
- [ ] Special-Device-Hardwaretabelle übernimmt nur bewusst ausgewählte/validierte Zeilen.
- [ ] Geräte erscheinen zuerst in der Warteschlange.
- [ ] Fehlerhafte FEE-Erzeugung bleibt prüfbar in der Warteschlange.

## Bestehende VIBN-Funktionen

- [ ] CAD Wizard, Zuli Converter, Container Generation und Container2Fee funktionieren mit einer bekannten Testvorlage.
- [ ] Container Generation lädt nach einer Requirements-XML ein bestehendes ContainerFile als aktiven Arbeitsstand; ohne aktiven Stand ist der Vergleich deaktiviert.
- [ ] **Aktiven Stand vergleichen** fragt nur einen Kandidaten ab und zeigt feldgenaue Unterschiede zum sichtbaren Workspace; **Arbeitsstand laden** lädt weiterhin ausschließlich das interne Workspaceformat.
- [ ] Der bestehende Container2Fee-Reiter arbeitet unverändert.
- [ ] Container2FEE Visual lädt dieselbe XML ohne FEE, zeigt Container/Signale/Links, speichert und lädt Sidecar-Schema 6, erlaubt nur kompatible Drag-and-drop-Ziele und beschränkt Slot-Overrides auf die Runtime-Slots des Containertyps.
- [ ] Gefundene FEE-Signale lassen sich auf Signal-Knoten ziehen; die bestätigte GUID bleibt nach erneutem Öffnen erhalten und löst einen dokumentierten Tag-/Adresskonflikt eindeutig auf.
- [ ] Rot, Gelb und Grün kennzeichnen fehlende/mehrdeutige, geplante und vollständig gefundene beziehungsweise erfolgreich erzeugte Baumknoten bis hinunter zu Signal und Logikobjekt.
- [ ] Eine ausdrücklich bestätigte Best-Effort-Generierung kennzeichnet den erzeugten Root und legt pro akzeptiertem Fehler genau einen untergeordneten Fehler-BasicFrame an; der Fortschritt endet mit einer Fertigmeldung.
- [ ] FEE2Container exportiert einen erkannten älteren Container ohne Signalverknüpfung mit einem `FEE-UNASSIGNED-*`-Prüfeintrag statt ihn auszublenden.
- [ ] FEE2SpecialDevices findet bekannte Gerätelogiken auch in verschachtelten BasicFrames und unterdrückt doppelte Treffer desselben Geräts.
- [ ] SpecialDevices2FEE zeigt den Gerätefortschritt und entfernt ein vollständig vorhandenes, eindeutig erkanntes Gerät ohne erneute Erzeugung aus der Warteschlange.
- [ ] ViCo liest den Projektstart aus Custom-Field 508, ordnet Start und Deadline mit Kartentitel sowie Karten-ID zu und zeigt Planung/In Arbeit für `Angelegt (Tool)` ausklappbar an.
- [ ] ViCo aktualisiert Karten ohne `fields`-Reduktion; ein leerer oder nicht mit Arbeitsplatz-Lanes verknüpfbarer API-Stand ersetzt keinen vorhandenen Cache und die Suche meldet keine interne Trefferspalte `Robotik`.
- [ ] Containercheckboxen sowie Alle selektieren/deselektieren begrenzen die Aktion auf vollständige unterstützte Container; abgewählte Container werden nicht erzeugt.
- [ ] Fehlende SimObject-Ziele sind rot, Erzeugungswünsche gelb und vorhandene Zuordnungen auf Ziel- und FEE-Objektseite grün dargestellt.
- [ ] **Nur SimObjects verknüpfen** verbindet nach Model Validation → Update Objects vorhandene SimObjects mit genau einer gleichnamigen vorhandenen Logik und erzeugt kein Modellobjekt neu.
- [ ] Container2FEE Visual erzeugt mit denselben Zuordnungen fachlich dasselbe Ergebnis wie der bestehende Executor; Erzeugen und Überspringen sind geprüft.
- [ ] Ein Stopper-Floor besitzt nach Erzeugung oder Link-only-Aktualisierung einen aktiven `CollisionSlot`; `SIM_Collision` und alle gewählten `Floor/Collision`-Slots sind nach Save/Reload verbunden.
- [ ] Eine von FEE abgewiesene Variablen- oder Slotverknüpfung wird mit GUID-/Slot-Kontext als Fehler gemeldet und nicht als Erfolg angezeigt.
- [ ] Fehlende ModelValidation-Pflichtsignale/-ziele brechen vor dem ersten Schreibzugriff ab; Stopper-Rückmeldungen werden über `Opened/Closed` geprüft.
- [ ] Neu erzeugte SimObjects besitzen die dokumentierten bisherigen Container2FEE-Größen; Bewegungscontainer haben plausible Startparameter.
- [ ] Container2FEE Visual erzeugt in FEE einen BasicFrame mit `vibn.container2fee.schema`-Tag; nach FEE-Speichern, Schließen und Öffnen findet FEE2Container denselben Root und exportiert ein semantisch gleiches ContainerFile.
- [ ] Wird eine TagComponent-Property nach bestätigter Best-Effort-Freigabe von FEE nicht zurückbestätigt, läuft die fachliche Generierung weiter und protokolliert die eingeschränkte Provenienz, statt beim Root oder einem erzeugten Unterobjekt abzubrechen.
- [ ] Eine direkte Slotänderung und eine PLC_IN-Änderung über MoveBit werden nach Save/Reload als eindeutige Route innerhalb des Roots exportiert; externe oder mehrdeutige Routen bleiben unverändert und erscheinen als Diagnose.
- [ ] Model Validation, Model Control und Interface Operation funktionieren mit dem Testmodell; Update Objects protokolliert Objektzahl und Laufzeit und ist gegenüber dem Referenzmodell nicht langsamer.
- [ ] Keine bestehende Funktion wurde durch ViCo-/Kanbanize-Aufrufe verändert.

## Übergabe

- [ ] Diagnoseprotokoll enthält keine sensiblen Werte.
- [ ] Anwenderhandbuch und Screenshots sind Bestandteil des Releasepakets.
- [ ] `scripts/Publish-IbnRemote.ps1` erzeugt nur `VIBN_Tools_IBN.exe`; die EXE startet auf einem sauberen Windows-x64-PC ohne .NET-, FEE- oder TIA-Installation und enthält keine Volltool-Reiter.
- [ ] Bekannte externe SDK-Warnungen bzw. Abhängigkeiten sind dokumentiert und keine neue funktionale Warnung aus den geänderten Integrationsmodulen offen.

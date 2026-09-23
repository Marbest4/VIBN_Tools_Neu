# Refactoring Phase A: Bestandsaufnahme und Zielarchitektur

Stand: 4. September 2026  
Branch: `feature/vibn-tools-refactor`

Dieses Dokument ist die technische Ausgangsbasis für die schrittweise Weiterentwicklung. Phase A verändert bewusst noch keine produktive Fachlogik. Aussagen mit externen Abhängigkeiten werden nicht als vollständig verifiziert dargestellt.

## 1. Verifizierte Ausgangslage

| Prüfung | Ergebnis | Grenze |
| --- | --- | --- |
| `dotnet build VIBN_Tools_App.sln -c Release --no-restore` | erfolgreich, 0 Warnungen, 0 Fehler | verwendet das Repository-SDK; in der Codex-Sandbox wurden `TargetPlatformSdkPath` und `TargetPlatformDisplayName` explizit gesetzt, um ausschließlich deren gesperrten Windows-SDK-Suchpfad zu umgehen |
| `dotnet build VIBN_Tools.csproj -c Release --no-restore` | erfolgreich, 0 Warnungen, 0 Fehler | verwendet automatisch das unveränderte flache Repository-SDK `SDK` in Version `5.0.11.48415` |
| `Tests/CoreSmokeTests` | erfolgreich | keine Live-Zugriffe auf Kanbanize, TIA oder FEE |
| `Tests/ContainerGenerationSmokeTests` | erfolgreich; Interface5: 420, Interface7: 345 Signale | bekannte Beispieldaten, kein vollständiger fachlicher Golden Master |
| `Tests/UiStartupSmokeTests` | erfolgreich | prüft Initialisierung und Bindings, keine vollständigen Benutzerabläufe |

Das Repository enthält inzwischen den vollständigen, vom Anwender bereitgestellten flachen FEE-SDK-Satz unter `SDK`. Die rekursive Abhängigkeitsprüfung löst alle 16 benötigten `FS.*`-Assemblies auf. Das unverändert übernommene Projekt `Grob Generation Interface` ist als eigenes Solution-Projekt eingebunden; seine maschinenspezifischen SDK- und Ausgabepfade werden ausschließlich zentral überschrieben. Hauptprojekt, Plugin und vollständige Solution bauen gemeinsam mit 0 Warnungen und 0 Fehlern. Eine reale FEE-Abnahme benötigt weiterhin einen laufenden kompatiblen FEE-Host und ersetzt nicht den Buildnachweis.

`Projekt1.7z` ist als reales TIA-V20-Testartefakt vorhanden und enthält `Projekt1/Projekt1.ap20`. TIA Portal V20 und die zugehörige `Siemens.Engineering.dll` sind installiert; die Bridge baut gegen diese reale PublicAPI. Die synthetische Traversierung läuft unter Windows PowerShell 5.1 und PowerShell 7. Für den Live-Attach fehlt noch die Windows-Gruppenmitgliedschaft des aktuellen Benutzers in `Siemens TIA Openness` und danach eine neue Anmeldung.

## 2. Solution und Abhängigkeitsrichtung

| Projekt | Ziel | Verantwortung | Wesentliche Abhängigkeiten |
| --- | --- | --- | --- |
| `VIBN_Tools` | .NET 8 Windows/WPF | Hauptanwendung, Views, ViewModels, Container-/FEE-Funktionen, Composition | Core, Infrastructure, TIA Client, FEE-SDK, Grob.UX |
| `VIBN_Tools.Core` | .NET 8 | testbare ViCo-/Kanbanize-Domäne, Ports und Policies | keine UI-/Herstellerabhängigkeit |
| `VIBN_Tools.Infrastructure` | .NET 8 | HTTP-, Datei-, Cache-, Rollen-, RDP- und Windows-Adapter | Core |
| `VIBN_Tools.Tia.Contracts` | .NET Standard 2.0 | serialisierbare Named-Pipe-Kommandos und DTOs | keine UI-Abhängigkeit |
| `VIBN_Tools.Tia.Client` | .NET 8 | typisierter Client zum isolierten TIA-Prozess | TIA Contracts |
| `VIBN_Tools.TiaBridge` | .NET Framework 4.8 | Siemens Openness in separatem Prozess | TIA Contracts, Siemens Engineering |
| `VIBN_Tools.IbnRemote` | .NET 8 Windows/WPF | reduzierte IBN-Remote-Anwendung | Core, IBN Infrastructure |
| `VIBN_Tools.IbnRemote.Infrastructure` | .NET 8 | bewusst begrenzte Read-/RDP-Adapter | Core |
| drei Smoke-Test-Projekte | Console | Core-, Generator- und WPF-Startup-Regressionen | jeweils gezielte Produktprojekte |

Die Trennung von Core, Adaptern und TIA-Bridge ist eine tragfähige Grundlage. Im WPF-Hauptprojekt liegen jedoch noch Fachlogik, Herstellerzugriffe und UI-Zustand eng zusammen. Insbesondere `ContainerGenerationPageVM` ist eine große Legacy-Klasse. Ein mechanisches Aufteilen ohne zusätzliche Golden-Master-Tests wäre regressionsgefährlich.

## 3. Composition und Laufzeit

1. `App.xaml.cs` initialisiert die statische Service-Fassade und protokolliert unbehandelte Dispatcher-Fehler.
2. `GlobalClasses/Services.cs` erstellt `CoreApi`, `FeeConnectionService`, `FeeObjectService` und `ProjectSettings`. Fehlt die FEE-Laufzeit, bleibt die Anwendung startbar und sperrt FEE-Funktionen.
3. `MainWindow.xaml.cs` erzeugt `MainWindowVM` direkt. Ein DI-Container wird nicht verwendet.
4. `Application/ViCoFeatureBootstrapper.cs` ist eine manuelle Composition Root für ViCo, Kanbanize, TIA und SpecialDevices2FEE. Einige Instanzen werden geteilt, andere pro View erzeugt.
5. TIA läuft aus Stabilitätsgründen über eine eigene .NET-Framework-Bridge je TIA-View. Das ist beizubehalten.

Ziel ist zunächst keine vollständige DI-Migration. Sinnvoller ist eine schrittweise Composition-Root-Bereinigung: gemeinsame Fähigkeiten als Interfaces, eindeutige Lebensdauern und keine neuen direkten statischen Zugriffe in ViewModels.

## 4. Navigation: Tab zu View, ViewModel und Services

| Navigation | View | ViewModel | Zentrale Dienste/Abhängigkeiten | Aktuelle Freigabe |
| --- | --- | --- | --- | --- |
| Project Settings | `SettingsPage` | `SettingsPageVM` | ProjectSettings, FEE-Verbindung/-Objekte, WorkstationDirectory, CredentialConfiguration, Versionsinfo | immer sichtbar |
| Kanbanize Karten | `KanbanizeCardPage` | `KanbanizeCardPageVM`, `VibnWorkplaceSynchronizationVM` | KanbanizeCardApiService, VibnWorkplaceSynchronizationService | Level 8 |
| ViCo | `ViCoWorkspacePage` | untergeordnete ViewModels | PC-/Projektsuche und Projekte/Favoriten | immer sichtbar |
| ViCo → PC-/Projektsuche | `ViCoSearchPage` | `ViCoSearchPageVM` | WorkstationCatalog/Search, Kanbanize Refresh/Configuration, RDP, Session, Netzwerk, Pfadauflösung, Preferences | innerhalb ViCo |
| ViCo → Projekte/Favoriten | `ViCoPage` | `ViCoPageVM` | ProjectCatalog/Search, Favorites, PathLauncher | innerhalb ViCo |
| Transfer | `ViCoCopyPage` | `ViCoCopyPageVM` | FileCopy, FolderSelection, WorkspaceContext, ProjectStructure | immer sichtbar |
| TIA Portal | `TiaPortalPage` | `TiaPortalPageVM` | NamedPipeTiaBridgeClient, TiaLibraryService, FolderSelection | immer sichtbar |
| Administration | `ViCoAdministrationPage` | `ViCoAdministrationPageVM` | RoleStore, Outlook Meetings, UpdateService, PathLauncher | Level 9 |
| CAD Wizard | `CadWizardPage` | `CadWizardPageVM` | ProjectSettings und bestehende CAD/FEE-Hilfen | Level 7 |
| Zuli Converter | `ZuliConverterPage` | `ZuliConverterPageVM` | Excel-/ZuLi-Konvertierung | immer sichtbar |
| Container Generation | `ContainerGenerationPage` | `ContainerGenerationPageVM` | ZuLi/Requirements-Reader, Generator, Reimport/Reconciliation, Persistenz, ActionLog | Level 7 |
| Container2Fee | `ContainerToFeePage` | `ContainerToFeePageVM` | Container-Reader, FEE-Factories/-Wrapper | Level 7, Ausführung zusätzlich FEE-Gate |
| Container2FEE Visual | `ContainerToFeeVisualPage` | `ContainerToFeeVisualPageVM` | Planning, Discovery, Sidecar, Binder, Executor | Level 7, Ausführung zusätzlich FEE-Gate |
| SpecialDevices2FEE | `SpecialDevicePage` | `SpecialDevicePageVM` | eigene TIA-Bridge, HardwareMappingStore, FEE-Import | sichtbar; FEE-Aktion gegated |
| Model Validation | `ModelValidationPage` | `ModelValidationPageVM` | statische FEE-Services/Wrapper | nur mit FEE-Verbindung bedienbar |
| Model Control | `ModelControlPage` | `ModelControlPageVM` | statische FEE-Services/Wrapper | nur mit FEE-Verbindung bedienbar |
| Interface Operation | `InterfaceOperationPage` | `InterfaceOperationPageVM` | FEE-Interfaces und Verbindungslogik | sichtbar; einzelne Aktionen gegated |
| AI-Test | `AITrainingTestPage` | `AITrainingTestPageVM` | ActionLogger, ML.NET-Training/Evaluation, Containerdaten | Level 8 |

Abweichungen zum Zielbild:

- Die linke Navigation kann nicht zwischen Symbol- und Symbol/Text-Modus wechseln.
- Verfügbarkeitsgründe sind nicht einheitlich modelliert; häufig existieren nur `bool`-Gates oder pauschale Tooltips.

Transfer, TIA und Administration wurden im ersten kleinen Umsetzungsschritt nach Phase A in die Hauptnavigation verschoben. Das redundante Workspace-Level-8-Gate wurde entfernt; Administration folgt jetzt dem zentral berechneten Level-9-Gate.

## 5. Fachmodelle und Datenflüsse

### 5.1 Container Generation

`ComponentContainer` bildet einen Container mit ID, Komponente, Typ, Min/Max und `DataList` ab. `ContainerEntry` enthält unter anderem Laufzeit-`SignalId`, XML-ID, Adresse, Datentyp, Signal, Slot, Notiz sowie Prüf- und Änderungszustand. `ContainerData` ergänzt Slots, Gültigkeit, `ManuallyChecked` und Validierungsinformationen.

Der aktuelle Importfluss besitzt bereits wichtige Stabilitätsbausteine:

- `GenerationWorkspaceSnapshot` als persistierbarer Arbeitsstand,
- `GenerationWorkspaceReconciler` für den erneuten ZuLi-/Requirements-Import,
- feldgenaue `ReimportDifference`-Einträge und selektive Entscheidungen,
- Undo/Redo für bis zu 20 Aktionen,
- Review-Markierungen, Signal-IDs, Zusammenfassung und ActionLog.

Offene Kernpunkte:

- Der Vergleich zweier fertiger ContainerFiles verwendet inzwischen dieselbe semantische Reconciliation wie der Reimport. Hinzugefügt, entfernt, Quelldaten- und Zuordnungsänderungen werden feldgenau angezeigt und einzeln übernommen; die geladenen Dateien bleiben unverändert. Automatische Add/Remove/Source/Slot-Tests sind vorhanden.
- Die Slot-Multiplizität ist jetzt zentral und case-insensitive geregelt: `PLC_OUT_` und sonstige Slots dürfen nicht mehrfach belegt werden. Jede `PLC_IN_`-Mehrfachbelegung bleibt zulässig. Einzel-Slotmodelle werden beim FEE-Export über je ein `FeeSimpleMove` pro Signal verlustfrei auf den gemeinsamen Eingang geführt; bereits listenbasierte Grob-Container behalten ihren bewährten Move-Ablauf. Policy, XML-Parsing und Signalzählung sind automatisch getestet; die tatsächlichen SDK-Kanten benötigen weiterhin eine FEE-Live-Abnahme.
- Erzeugung und FEE-Abbildung verteilen Typwissen über Switches, Factories, Slot-Reflection und einen separaten Metadatenkatalog.

### 5.2 FEE und Container2FEE

Die FEE-Seite verwendet Wrapper wie `FeeAbstractObject`, `FeeLogic`, `FeeInterface`, `FeeInterfaceSignal` und Logiktypen wie `FeeSimpleMove`, `FeeNot`, `FeeAnd` und `FeeOr`. Die visuelle Variante besitzt bereits getrennte Bereiche für Planning, Discovery, Persistence und Execution sowie einen fingerprintgeschützten Sidecar.

Der aktuelle Plan unterscheidet Container, Logiken, Signale, technische Ziele, Erzeugungsauswahl, optionale Interface-Präferenz und Kanten. Die frühere getrennte Signalerzeugung ist entfernt: `SignalResolutionPlanner` durchsucht alle geladenen Interfaces, blockiert mehrdeutige oder widersprüchliche Identitäten, dedupliziert neue Variablen und erzeugt nur fehlende Signale im streng über Name, Provider-GUID und Provider erkannten Grob Generation Interface. Policy und WPF-Start sind automatisch getestet; die reale SDK-Ausführung ist noch live abzunehmen.

Für eine Rückrichtung FEE → Container fehlt ein kanonisches Zwischenmodell. In bestehenden FEE-Modellen sind ursprüngliche Container-ID, Typ und Slot nicht überall eindeutig als Provenienz hinterlegt. Der fachliche Scope ist inzwischen festgelegt: Die erste Version muss nur künftig durch Container2FEE erzeugte Modelle mit Provenienz zuverlässig erkennen und rückwandeln; historische Modelle gehören nicht zum garantierten Round-Trip.

Empfehlung: zuerst ein gemeinsames semantisches Mapping-Modell und eine Mapping-Policy für beide Richtungen einführen. Neue Generationen erhalten zusätzlich stabile Provenienz. Historische Modelle werden heuristisch eingelesen und müssen Unsicherheiten explizit anzeigen.

### 5.3 TIA

`VIBN_Tools.Tia.Contracts` transportiert Prozess-, Projekt-, PLC-, Bibliotheks-, Achsen- und Hardwareinformationen. `TiaHardwareModuleInfo` enthält Geräteindex, Traversierungsindex, Tiefe, Elternknoten, konkrete TIA-Objektklasse, Hardware-ID, Slot/Subslot, Geräte-/Modulname, Typen, Hersteller-/Bestelldaten, Netzwerkdaten und Ein-/Ausgangsadressen. Die UI zeigt zusätzlich den konservativ ermittelten Zuordnungskandidaten.

Achsen werden jetzt separat und schreibgeschützt gelesen, über Gruppenpfad plus Namen stabil identifiziert und einzeln beziehungsweise über Alle/Keine ausgewählt. Das mutierende Kommando erhält nur diese IDs und liefert pro gefundenem Parameter Erfolg oder Fehler an UI und Log zurück. Die Namensheuristik X/Y/Z für linear/rotatorisch bleibt fachlich riskant und darf nicht ohne Live-Abnahme erweitert werden.

`Save` speichert das angehängte TIA-Projekt. Bibliotheksimport/-export arbeitet mit VICOBIB-Blöcken und Datentypen, aber die UI erklärt Wirkung, Voraussetzungen und Speicherverhalten nicht ausreichend.

### 5.4 SpecialDevices2FEE

Der Hardware-Reader traversiert die TIA-Hierarchie und projiziert derzeit überwiegend adressführende Blätter. Die automatische Namenswahl enthält bereits Heuristiken zwischen Geräte-, PROFINET- und Modulnamen. Vor weiteren Änderungen ist die geforderte Diagnoseansicht nötig, damit Geräteindex, Hierarchie und echte Openness-Typen an realen Projekten sichtbar werden.

### 5.5 ViCo und Kanbanize

ViCo besitzt testbare Core-Interfaces und Infrastrukturadapter. Tabelle und Commands verwenden aber noch ein zu grobes gemeinsames Offline-Gate. Dadurch verschwinden oder sperren auch Aktionen, die nicht zwingend einen erreichbaren Remote-PC benötigen. Datum, Spaltenmodell, Kontextmenü, kopierbare Pfade und gefilterte Detailinformationen fehlen im Zielumfang.

Die Arbeitsplatz-Synchronisation ist idempotent und vergleicht Kalendertage ohne Uhrzeit. Aktuell werden nur Grundinbetriebnahme-Karten berücksichtigt; Nachpflege, strukturierte Rollen-/Zusatzbezeichnungen, die differenzierten Mehrfachtrefferregeln und Planansicht-URL fehlen. Die UI formatiert Termine weiterhin mit Uhrzeit.

### 5.6 Zugangsdaten

Project Settings verwendet verdeckte `PasswordBox`-Eingaben mit Two-Way-Behavior. FEE-Benutzer/-Passwort, API-Key und RDP-Passwort werden als generische Einträge im lokalen Windows Credential Manager gespeichert und zur Laufzeit aktualisiert. Die feste FEE-Anmeldung `admin/admin` ist entfernt. Alte Benutzerumgebungsvariablen werden nach erfolgreicher Migration gelöscht. Die Werte müssen pro Benutzer/Rechner nicht bei jedem Start eingegeben werden, werden aber bewusst nicht automatisch zwischen Rechnern synchronisiert.

Für mehrere Rechner ist weiterhin eine einmalige Einrichtung je Windows-Profil oder eine zentral verwaltete, organisationskonforme Secret-Verteilung erforderlich. Eine öffentliche oder repositorybasierte Speicherung ist ausgeschlossen.

## 6. Zielarchitektur in kleinen Schritten

1. **Sicherheitsnetz:** bestehende Smoke-Tests beibehalten, Golden Master für Requirements + Container ergänzen, fachliche Slot-Policies isoliert testen.
2. **Navigation und Capability-Modell:** Navigationseinträge als Datenmodell, Level-/FEE-/Online-/Konfigurationsvoraussetzungen mit einheitlichem `Availability`-Objekt aus Grund und Tooltip.
3. **Systemerkennung:** read-only `IInstalledComponentDiscovery` für FEE, TIA/Openness, WinCC/Siemens-Komponenten und TwinCAT; keine Änderung der SDK-Auswahl zur Laufzeit.
4. **ViCo/Kanbanize:** Core-Modelle erweitern, dann ViewModels und UI. Live-Schreibzugriffe erst nach Preview- und Contract-Tests.
5. **Container-Domäne:** Slot-Policy, semantischer Containervergleich und Golden Master vor weiterer Zerlegung des großen ViewModels.
6. **Container2FEE:** kanonisches Mapping-Modell, deterministische Signalauflösung, präzise Planvalidierung, anschließend UI-Vereinfachung.
7. **TIA/SpecialDevices2FEE:** read-only Diagnose und Achsenliste beibehalten; Schreibkommandos bleiben separat und selektiv.
8. **KI-Regelvorschläge:** versioniertes Ereignisschema und deterministische Aggregation vor ML-Modellen; niemals automatische XML-Änderung ohne Vorschau, Backup und Auswahl.
9. **FEE2Container:** Reverse-Extractor auf demselben Mapping-Modell, Provenienz für neue Modelle, Unsicherheitsdiagnose für Altmodelle, semantische Round-Trip-Tests.

`FEE2SpecialDevices` sollte als eigener Hauptreiter umgesetzt werden, aber denselben FEE-Root-Selektor und Reverse-Extractor wie `FEE2Container` verwenden. Hardware-/TIA-Zielmodell, Validierung und Ausgabe unterscheiden sich stark genug, dass eine Integration in dieselbe Arbeitsfläche die Bedienung und Testbarkeit verschlechtern würde.

## 7. Hauptrisiken

| Risiko | Auswirkung | Gegenmaßnahme |
| --- | --- | --- |
| Proprietäre FEE-/Grob-Abhängigkeiten sind binär und versionsgebunden | falsche DLL-Mischung kann trotz erfolgreichem Build zur Laufzeit scheitern | unveränderten vollständigen SDK-Satz verwenden, Closure vor Publish erzwingen und reale FEE-Abnahme getrennt dokumentieren |
| Container-Golden-Master deckt Requirements noch nicht vollständig ab | unbemerkte Generatorregression | freigegebene Requirements- und erwartete Containerdatei versionieren oder intern referenzieren |
| Großes `ContainerGenerationPageVM` | hohe Kopplung und UI-Regressionen | erst Policies/Services extrahieren, dann UI; jeder Schritt mit Golden Master |
| PLC_IN-Fan-in scheitert erst im proprietären SDK | automatischer Test kann reale FEE-Slotkanten nicht beobachten | zentrale Preflight-Policy und Parsertests sind vorhanden; vollständige Move-/Slotverschaltung in einer FEE-Testkopie live abnehmen |
| TIA-Openness-Versionen und Proxytypen | Laufzeitfehler trotz erfolgreichem Build | Bridge isoliert lassen, DTO-kompatibel erweitern, reale Projekte versionenweise abnehmen |
| FEE-Rückabbildung ohne Provenienz | Datenverlust oder falsche Container | Altmodelle nur mit Confidence/Diagnose, neue Modelle mit stabilen IDs |
| Kanbanize-Titel als implizites Datenmodell | falsche Konflikte/Duplikate | Titelgrammatik und Rollen als explizite Parser-/Policy-Tests |
| UI-Farben allein als Status | schlecht zugänglich und missverständlich | zusätzlich Text, Icon, Tooltip und Filter; Farbkontrast testen |
| Credential Manager ist lokal, nicht rechnerübergreifend | erneute Einrichtung pro Profil/Rechner | Unternehmens-Secretsystem für zentrale Verteilung; keine Logs, Exporte oder Repositorywerte |
| Live-Schreibzugriffe auf FEE/TIA/Kanbanize | externe Seiteneffekte | Preview, selektive Bestätigung, Backup/Idempotenz und getrennte Live-Abnahme |

## 8. Geklärte Fachregeln und verbleibende externe Blocker

Geklärt sind:

1. **PLC_IN/PLC_OUT:** Jede `PLC_IN_`-Mehrfachbelegung ist mit einem eigenen `FeeSimpleMove` je Signal zulässig. `PLC_OUT_` bleibt eindeutig und darf nicht mehrfach verschaltet werden.
2. **Grob Generation Interface:** Erkennung über den Namen `Grob Generation Interface`, GUID `a6222164-be37-49de-b760-9b1c97c320bb` und Provider `GrobGenerationInterface.Interface.GrobInterfaceProvider`. Mehrdeutige oder widersprüchliche Treffer müssen die Generierung vor Schreibzugriff blockieren.
3. **Kanbanize:** Der generierte Marker `*[Gen]*` begrenzt den Basisnamen von rechts. Die kopierte Hauptkarte erhält den Zusatz `CORE`, weitere Karten den Zusatz `CLIENT`. Parser-Tests müssen weiterhin verschiedene Orts- und Kundenlängen sowie bereits ergänzte Rollen abdecken.
4. **FEE2Container:** Garantierter Round-Trip nur für künftig durch Container2FEE erzeugte Modelle mit Provenienz.

Verbleibende externe Blocker sind keine offenen Businessentscheidungen:

- Der SDK-Abhängigkeitsabschluss und der Build des unveränderten Grob-Plugins sind erledigt; offen bleibt ausschließlich die reale Ausführung in einem kompatiblen FEE-Host.
- Für die TIA-Live-Abnahme von `Projekt1.7z` muss der aktuelle Benutzer Mitglied der vorhandenen lokalen Gruppe `Siemens TIA Openness` sein und sich danach neu anmelden.
- Für die selektive Achsenkonfiguration sind reale ausgelesene Achsendaten weiterhin erforderlich; das TIA-Beispielprojekt kann dafür erst auf einem entsprechend ausgestatteten Rechner ausgewertet werden.

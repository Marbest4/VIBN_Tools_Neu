# Klassen- und Verzeichnisreferenz

## Anwendung und Navigation

| Klasse/Datei | Aufgabe |
| --- | --- |
| `Application/View/MainWindow.xaml` | Hauptnavigation, zentrierte Reiterbeschriftungen und Level7-/Level8-/Level9-Sichtbarkeit |
| `Application/VM/MainWindowVM.cs` | lädt Arbeitsplätze/Rollen beim Start und berechnet Hauptreiter-Berechtigungen |
| `Application/NavigationPreferenceStore.cs` | speichert ausschließlich den ein-/ausgeklappten Zustand der linken Navigation atomar pro Benutzer |
| `Application/ViCoFeatureBootstrapper.cs` | Composition Root für ViCo, Kanbanize, RDP, Rollen und TIA-Bridge |
| `Application/ApplicationLogService.cs` | zentraler Anwendungslog für Status, Warnungen und Fehler |
| `Application/View/DiagnosticsPanel.xaml` | sichtbares Diagnosefenster im Hauptfenster |
| `Settings/FeeVersionInfoProvider.cs` | ermittelt verwendete SDK- und höchste vollständige lokale FEE-Version; Kandidaten ohne `Bin\FS.SDK.dll` werden verworfen |
| `Settings/ConnectionService.cs` | bestätigt den SDK-Verbindungszustand und stellt das zentrale FEE-Funktions-Gate bereit |
| `GlobalClasses/Services.cs` | initialisiert FEE-Dienste; hält bei unvollständiger Runtime die restliche Anwendung lauffähig und das FEE-Gate geschlossen |
| `Application/Behaviors/EnterKeyCommandBehavior.cs` | übergibt den aktuellen TextBox-Wert bei Enter an einen ViewModel-Command, ohne Boardlogik in Code-behind zu verlagern |
| `SharedWpf/Commands/*` | gemeinsame synchrone und nicht-reentrante asynchrone WPF-Commands für Hauptanwendung und IBN-Remote |
| `SharedWpf/NotifyBase.cs` | kleine FEE-unabhängige Observable-Basis für Feature- und Präsentationsmodelle |
| `VIBN_Tools.Core/Collections/ObservableCollectionExtensions.cs` | einheitliches Ersetzen gebundener Collections ohne Austausch der Instanz |
| `Application/ExportFileNamePolicy.cs` | gemeinsame sichere Basisnamen für Reverse-Exporte |

## ContainerGeneration-Projekt

| Projekt/Ordner | Aufgabe |
| --- | --- |
| `ContainerGeneration/VIBN_Tools.ContainerGeneration.csproj` | eigenständige Featuregrenze mit ClosedXML-, ML-, NLog- und WPF-Abhängigkeiten |
| `ContainerGeneration/BusinessLogic` | Requirements, ZuLi-Import, Matching und Containererzeugung |
| `ContainerGeneration/Models` | Workspace, Reimport, Undo/Redo und Validierungsmodelle |
| `ContainerGeneration/XmlSchemas` | eingebettete AutoCreate-/CAAResult-Schemata im besitzenden Featureprojekt |
| `ContainerGeneration/AI` | nachvollziehbare Trainings-, Analyse- und Regelvorschlagsbausteine |

## ViCo-Modelle und Fachregeln (`VIBN_Tools.Core/ViCo`)

| Datei/Typ | Aufgabe |
| --- | --- |
| `Workstations.cs` | PC-, Projekt-, Konfigurations-, RDP- und Dienstverträge; `ViCoWorkstation` berechnet Status/Projektübersicht |
| `UserRoles.cs` | `ViCoUserRole`, `ViCoRolePolicy`, `IViCoUserRoleStore`, feste `lutzma`-Rolle und Zwei-Level9-Invariante |
| `ProjectCatalog.cs` | Projekt-/Favoritenmodelle und Suchverträge |
| `Diagnostics.cs` | neutraler Logvertrag `IApplicationLog` |
| `Administration.cs` | Outlook-/Updateverträge für die ViCo-Verwaltung |
| `AutoRefreshSettings.cs` | Intervallmodell, 1–1440-Minuten-Regel und persistenter Store-Vertrag |
| `UserCredentialConfiguration.cs` | statusorientierter Vertrag zum Speichern/Löschen von FEE-Zugang, API-Key und RDP-Passwort ohne Secret-Ausgabe |

## ViCo-Infrastruktur (`VIBN_Tools.Infrastructure/ViCo`)

| Datei/Typ | Aufgabe |
| --- | --- |
| `LegacyWorkstationCatalog.cs` | liest kompatible Cachedateien und die strukturierte `KONFIGURATION`-Karte; Kanbanize-Benutzer hat Vorrang |
| `KanbanizeRefreshService.cs` | lädt Arbeitsplätze/Robotik aus Kanbanize und ersetzt Caches atomar |
| `WorkstationBoardCache.cs` | typisierte Cacheform mit Karten- und Unteraufgaben-IDs |
| `KanbanizeWorkstationConfigurationService.cs` | aktualisiert/ergänzt Standard-Unteraufgaben und legt eine fehlende KONFIGURATION-Karte nur auf expliziten Befehl an |
| `DesktopWorkstationServices.cs` | Ping, temporärer Credential-Manager-Eintrag, `.rdp`-Start und read-only `quser`-Abfrage mit Fehlerdiagnose |
| `RemoteDesktopProfileBuilder.cs` | reine, testbare `.rdp`-Profilbildung für automatische oder abgefragte Anmeldungen |
| `JsonViCoUserRoleStore.cs` | atomare `roles.json`-Ablage |
| `LegacyRoleMigrationReader.cs` | einmaliger Nur-Lese-Import älterer Zuordnungen |
| `BoundedFileCopyService.cs` | begrenzte parallele Dateiübertragung |
| `JsonViCoAutoRefreshSettingsStore.cs` | atomare lokale Persistenz des AutoUpdate-Intervalls |
| `UserEnvironmentCredentialConfigurationService.cs` | ersetzt PowerShell durch per-user Speichern/Löschen und aktualisiert den laufenden Prozess |

## ViCo-ViewModels (`Application/VM`)

| Klasse | Aufgabe |
| --- | --- |
| `ViCoSearchPageVM` | Suche, manueller/periodischer Refresh samt Countdown, Pfadauflösung, Online-/Session-Abfragen, RDP sowie KONFIGURATION speichern/anlegen |
| `ViCoWorkstationRowVM` | Präsentation einer Tabellenzeile: Farben, Erreichbarkeit, RDP-Sitzung und Konfigurationsspalten |
| `ViCoConfigurationFieldVM` | Änderungsnachverfolgung einer vorhandenen Konfigurations-Unteraufgabe |
| `ViCoPageVM` | Projekte und Favoriten |
| `ViCoCopyPageVM` | Transfer zwischen Quell- und Zielpfaden |
| `ViCoAdministrationPageVM` | Rollen, Termine, Versionen; nur Level9 schreibt Rollen |
| `TiaPortalPageVM` | PLC-, Library- und Achsenansicht mit abgefangenen Bridge-Fehlern |

## Kanbanize

| Datei/Typ | Aufgabe |
| --- | --- |
| `Core/Kanbanize/CardCreation.cs` | neutrale Karten-/Boardmodelle und `IKanbanizeCardService` |
| `Core/Kanbanize/VibnWorkplaceSynchronization.cs` | Auswahl-, Zeitplan-, Konflikt- und Idempotenzregel |
| `Infrastructure/Kanbanize/KanbanizeCardApiService.cs` | v2-HTTP-Abfragen, Kartencreate und minimale Deadline-/Start-Patches |
| `Application/VM/KanbanizeCardPageVM.cs` | Boards, Auswahl und manuelle Kartenerstellung |
| `Application/VM/VibnWorkplaceSynchronizationVM.cs` | Vorschauzeilen, Zähler und bewusste Synchronisierung |
| `Application/View/KanbanizeCardPage.xaml` | UI mit Trennung zwischen Automatik und eigener Karte |

## TIA-Bridge

| Projekt/Datei | Aufgabe |
| --- | --- |
| `VIBN_Tools.Tia.Contracts` | serialisierbare DTOs, `TiaCommands`, Request/Response-Umschläge |
| `VIBN_Tools.Tia.Client` | typed Named-Pipe-Client, Bridge-Start/-Abbruch, Library-Import/-Export |
| `VIBN_Tools.TiaBridge/Bridge/TiaCommandDispatcher.cs` | ordnet Protokollkommandos Sessionmethoden zu |
| `VIBN_Tools.TiaBridge/Openness/TiaOpennessSession.cs` | versionsgebundene Siemens-Openness-Implementierung; `ListHardware` liest Modul- und E/A-Adressdaten |
| `Application/VM/TiaPortalPageVM.cs` | WPF-Steuerung und protokollierter Fehlerpfad |
| `VIBN_Tools.TiaBridge/Openness/TiaHardwareReader.cs` | read-only Traversierung aller Geräte; filtert adresslose Eltern, bildet getrennte Adresssätze und konvertiert Bit- in Byte-Längen |

## Container2FEE Visual

| Datei/Typ | Aufgabe |
| --- | --- |
| `VisualPlanModels.cs` | unveränderlich nach außen sichtbare Knoten, Kanten, Ziele, Zuordnungen und Validierungsergebnisse |
| `ContainerXmlVisualPlanParser.cs` | begrenztes, DTD-freies XML-Lesen, Fingerabdruck und deklarativer Plan |
| `ContainerMetadataCatalog.cs` | Metadaten der bestehenden Containerklassen und ihrer `SimObjectTarget`s |
| `VisualPlanSidecarStore.cs` | versionierte JSON-Persistenz ohne Änderung der Quell-XML |
| `FeeSimObjectDiscovery.cs` | SDK-Objekte in stabile, UI-neutrale Identitäten übersetzen |
| `ContainerToFeeVisualPlanService.cs` | Zuordnung, Eindeutigkeit, Auto-Matching, Undo/Redo, Validierung und Orchestrierung |
| `RuntimeVisualPlanBinder.cs` | eine gemeinsame, typgeprüfte Abbildung des Plans auf frische Legacy-Container für beide Ausführungsarten |
| `LegacyContainerToFeeExecutionAdapter.cs` | nur ausgewählte vollständige Container an den bestehenden Generator übergeben |
| `ExistingSimObjectLinkAdapter.cs` | ausschließlich vorhandene SimObjects mit vorhandenen gleichnamigen LogicObjects verbinden; keine Erzeugung |
| `ContainerToFeeVisualPageVM.cs` / `.xaml` | Commands, Filter, Drag-and-drop und dreigeteilte Darstellung |

## SpecialDevices2FEE und bestehende VIBN-Bereiche

| Bereich | Einstiegspunkt |
| --- | --- |
| TIA-Hardware nach SpecialDevices2FEE | `SpecialDevicePageVM.cs`, `SpecialDeviceHardwareImportVM.cs`, `Application/TiaHardwareMappingStore.cs`, `SpecialDevices/DeviceFactory.cs` |
| CAD Wizard | `CadWizardPageVM.cs` |
| Zuli Converter | `ZuliConverterPageVM.cs` |
| Container Generation | `ContainerGenerationPageVM.cs` (funktionierender Legacy-ZULI-/Generierungsworkflow) und `ContainerGeneration/` (Fachlogik); erneute Aufteilung erst nach Golden-Master-Tests |
| Container2Fee (bestehend) | `ContainerToFeePageVM.cs`, `ContainerToFee/` |
| Container2FEE Visual (zusätzlich) | `ContainerToFeeVisual/`, `ContainerToFeeVisualPageVM.cs`, `ContainerToFeeVisualPage.xaml` |
| Model Validation | `ModelValidationPageVM.cs`, `GlobalClasses/FeeObjects/FeeObjectService.cs` und `FeeInterface.cs`; letzteres liest Interfacevariablen einmal je Gesamtabfrage statt einmal je Interface |
| Model Control | `ModelControlPageVM.cs` |
| Interface Operation | `InterfaceOperationPageVM.cs` |
| ZuLi-/Container-Regressionstest | `Tests/ContainerGenerationSmokeTests` mit `Interface5.xlsx` und `Interface7.xlsx` |

Bestehende große VIBN-ViewModels werden nicht durch neue ViCo-Logik vergrößert. Neue Integrationslogik gehört in die oben genannten modulierten Klassen und Dienste.

## Separate IBN-Remote-Anwendung

| Projekt/Datei | Aufgabe |
| --- | --- |
| `VIBN_Tools.IbnRemote` | kompakte eigenständige WPF-Anwendung: PC/Online/Projekte als Standard, ausklappbare Details/RDP und lokale Zugangsdatenverwaltung |
| `VIBN_Tools.IbnRemote.Infrastructure` | minimaler Compile-Ausschnitt der bewährten Read-/RDP-Adapter; enthält bewusst keine Board-Write-, FEE-, TIA-, Kopier- oder Administrationsklasse |
| `scripts/Publish-IbnRemote.ps1` | self-contained `win-x64`-Single-file-Publish und Prüfung auf unerwartete Begleitdateien |
| `docs/IBN_REMOTE.md` | Build, Verteilung, Benutzerkonfiguration, Logging und Sicherheitsgrenze |

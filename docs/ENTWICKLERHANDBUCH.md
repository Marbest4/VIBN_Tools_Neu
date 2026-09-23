# Entwicklerhandbuch

## Architekturregel

Neue ViCo-, Kanbanize- und TIA-Funktionen folgen dieser Richtung:

```text
View (XAML) → ViewModel → Core-Modell/Interface → Infrastructure-Adapter
TIA: ViewModel → Tia.Client → Named Pipe → TiaBridge → Siemens Openness
```

`VIBN_Tools.Core` darf keine WPF-, HTTP-, Windows- oder Siemens-Abhängigkeit erhalten. `Infrastructure` implementiert Core-Verträge. `Application/VM` koordiniert Bedienung und Status, enthält aber keine Transportformate oder Geschäftsregeln. Die bestehende VIBN-Logik wird nur dort angefasst, wo eine neue Integration sie benötigt.

ContainerGeneration ist ein eigenes Feature-Projekt. Neue Parser-, Requirements-, Matching-, Reimport- oder Generatorlogik gehört unter `ContainerGeneration/` und darf nicht wieder als Compile-Link in das Hauptprojekt aufgenommen werden. Wiederverwendbare WPF-Commands und Bindings gehören in `SharedWpf`; plattformneutrale kleine Policies und Collection-Helfer gehören in Core. Ein neuer Reiter rechtfertigt erst dann ein neues Projekt, wenn daraus eine gerichtete, zyklusfreie fachliche Grenze entsteht – nicht allein wegen seiner Position in der Navigation.

## Einstieg in den Code

1. `Application/View/MainWindow.xaml` zeigt alle Hauptreiter und die Rollen-Sichtbarkeit.
2. `Application/VM/MainWindowVM.cs` lädt dynamische Arbeitsplätze und die zentrale Rollenliste.
3. `Application/ViCoFeatureBootstrapper.cs` verbindet Core-Interfaces mit konkreten Infrastrukturdiensten.
4. Für den gewünschten Funktionsbereich die Tabelle in [KLASSENREFERENZ.md](KLASSENREFERENZ.md) verwenden.
5. Vor einer Änderung die korrespondierenden Smoke-Tests in `Tests/CoreSmokeTests/Program.cs` lesen.

## Erweiterungsmuster

### Neue ViCo-Arbeitsplatzinformation

1. Feld als neutrales Modell oder Vertrag in `VIBN_Tools.Core/ViCo` definieren.
2. Cache-/Kanbanize-Parsing in `LegacyWorkstationCatalog` bzw. `KanbanizeRefreshService` ergänzen.
3. Nur wenn editierbar: vorhandene Feld-/Subtask-ID im Core-Modell bewahren und einen eng begrenzten Infrastruktur-Write implementieren.
4. Anzeige in `ViCoWorkstationRowVM` und XAML ergänzen.
5. Parser- und Write-Scope-Test hinzufügen.

Die `KONFIGURATION`-Bearbeitung ist das Referenzmuster: vorhandene Unteraufgaben werden per PATCH geändert, fehlende Standard-Unteraufgaben per POST ergänzt. Eine fehlende Karte wird nur nach dem expliziten UI-Befehl standardisiert erstellt; normale Karten bleiben unberührt.

### Neue RDP-/Windows-Aktion

Zuerst ein Interface in `Workstations.cs` ergänzen. Danach eine konkrete Implementierung in `DesktopWorkstationServices.cs` schreiben und sie im Bootstrapper registrieren. Keine `Process.Start`-Aufrufe direkt aus einem ViewModel einfügen. Offline-Schutz und Fehlerprotokoll gehören in das ViewModel.

Ein RDP-Profil darf ausschließlich Ziel-PC, Benutzer, Monitorwahl und Abfragemodus enthalten. Der einzige Kennwortprovider ist `IUserCredentialConfigurationService.GetRemoteDesktopPassword`; `WindowsTemporaryRemoteCredentialStore` reicht den Wert über `ProcessStartInfo.ArgumentList` an `cmdkey`, protokolliert ihn nie und entfernt den Zieleintrag verzögert. Keine zweite Passwortquelle und kein Literal ergänzen.

`SecureUserCredentialConfigurationService` ist der einzige produktive UI-Schreibpfad. Er verwendet `WindowsCredentialManagerSecretStore` für FEE-Benutzer/-Passwort, Kanbanize-Key und RDP-Passwort und migriert alte `VIBN_VICO_KANBANIZE_API_KEY`-, `VIBN_RDP_PASSWORD`-, `VIBN_FEE_USERNAME`- und `VIBN_FEE_PASSWORD`-Benutzervariablen nach erfolgreichem Schreiben. `UserEnvironmentCredentialConfigurationService` bleibt ausschließlich als Migrationsadapter und Test-Seam bestehen. Neue Kanbanize-/RDP-/FEE-Adapter müssen Provider verwenden und Werte erst für die jeweilige Aktion auflösen. Secret-Werte dürfen nicht als bindbare Statusproperty, Logparameter oder Cachewert zurückgegeben werden; PasswordBox-Eingaben sind nach dem Speichern zu leeren. `PasswordBoxBindingBehavior.Password` muss mit `null` initialisiert bleiben: ein leerer DP-Standardwert verhindert bei einer initial ebenfalls leeren TwoWay-Bindung den ersten Property-Callback und damit die Registrierung des `PasswordChanged`-Handlers. Der UI-Smoke-Test prüft Quelle→UI, UI→Quelle und den Erhalt des Binding-Ausdrucks.

`Services.Initialize()` erzeugt die eine gemeinsame `CoreApi`-Instanz wieder im bewährten UI-Thread-Lebenszyklus, bevor `FeeConnectionService` mit dem Polling beginnt. Das ist noch kein Verbindungs- oder Interfacezugriff. Schlägt die lokale Runtime-Erzeugung fehl, bleibt der Start still und der explizite Connect-Ablauf versucht sie über `Services.TryInitializeFeeApi(...)` erneut; erst dort wird ein Fehler für den Benutzer protokolliert. `SettingsPageVM.Connect_ToFee` muss danach den ursprünglichen SDK-Lebenszyklus bewahren: Zugangsdaten auflösen, `CoreApi.Connect` genau einmal aufrufen und den asynchronen Handshake der SDK überlassen. Kein vorgeschaltetes `Disconnect`, kein zweiter aktiver Polling-Loop und kein automatisches Trennen nach einem anwendungsseitigen Timeout. Der vorhandene `FeeConnectionService` meldet den normalen SDK-Zustandswechsel an die UI. Interface-/Signalabfragen benötigen `Connection.CanUseFeeFeatures` und einen ausdrücklichen Benutzerbefehl. Die Interface-Seite darf keinen `Loaded`-Trigger für SDK-Abfragen erhalten. Die Serverliste muss über den WPF-Dispatcher verändert werden; `localhost` darf nicht durch eine leere ViCo-Liste verworfen werden.

`quser /server:<PC>` besitzt keinen sicheren Rechte-Bypass. Fehler 5 wird als Berechtigungsdiagnose an die Oberfläche gereicht. Alternative Implementierungen dürfen keine Credentials auslesen oder Berechtigungen umgehen.

### Neue Kanbanize-Funktion

1. Modell, Validierung und Fachregel in `VIBN_Tools.Core/Kanbanize`.
2. Eventuelle HTTP-Operation als schmalen Member von `IKanbanizeCardService` formulieren.
3. Den v2-Adapter in `VIBN_Tools.Infrastructure/Kanbanize/KanbanizeCardApiService.cs` umsetzen.
4. Payload auf das fachlich erlaubte Minimum begrenzen.
5. Einen `RecordingHttpMessageHandler`-Test hinzufügen, der Methode, URL und JSON-Felder prüft.

Keine generische „Update alles“-Methode einführen: Gerade beim Arbeitsplatz-Board ist der eng begrenzte Write-Scope Teil der Fachanforderung.

### Neue TIA-Operation

Die Reihenfolge ist verbindlich:

1. DTO und Kommandoname in `VIBN_Tools.Tia.Contracts`.
2. Member in `ITiaBridgeClient` und `NamedPipeTiaBridgeClient`.
3. Dispatch im `TiaCommandDispatcher`.
4. Implementierung in `ITiaOpennessSession` und `TiaOpennessSession`.
5. ViewModel-Command, Status und XAML.
6. Protocol-/Fake-Test in `Tests/CoreSmokeTests`.

Die Hauptanwendung darf keine Siemens-Openness-Assembly direkt laden. TIA-Fehler sind im ViewModel zu fangen und über `IApplicationLog` zu dokumentieren.

Die Prozessgrenze ist funktional relevant und nicht nur ein Deploymentdetail: Hauptanwendung und Client laufen auf .NET 8, die Siemens-Bridge auf .NET Framework 4.8. Außerdem kann der Client einen blockierten synchronen Openness-Aufruf nur durch Beenden seiner eigenen Bridge zuverlässig abbrechen, ohne die Hauptanwendung zu beenden. Deshalb darf `Siemens.Engineering` nicht in `VIBN_Tools.exe` integriert werden, solange Siemens keine kompatible gemeinsame Laufzeit und keinen gleichwertigen Abbruchmechanismus freigibt.

`TiaHardwareReader` ist kein unerreichbarer Code: Er wird über Named Pipe im separaten Prozess `VIBN_Tools.TiaBridge.exe` ausgeführt. Ein Breakpoint dort wird bei normalem F5 im WPF-Prozess nicht automatisch getroffen. Zum Debuggen nach dem Start der Hardwareabfrage in Visual Studio **Debuggen → An Prozess anfügen** wählen und `VIBN_Tools.TiaBridge.exe` auswählen. Die Bridge-Quellen sind als `UpToDateCheckInput` registriert, damit F5 nach einer Änderung keine alte kopierte Bridge startet.

`Address.Length` ist im Bridge-Modell eine Bitlänge. Nur `TiaHardwareReader` konvertiert mit Aufrundung in die zusätzlichen Bytefelder. UI oder Special-Device-Code dürfen die rohe Länge nicht ein zweites Mal umrechnen. Adresslose Hierarchieknoten liefern nur geerbte Metadaten; eine Tabellenzeile entsteht ausschließlich für einen konkreten E-/A-Adresssatz.

### Container2FEE Visual erweitern

Der alte Reiter und `ContainerToFeePageVM` bleiben die Verhaltensreferenz. Neue Planfunktionen gehören unter `ContainerToFeeVisual/`:

1. reine Struktur in `Domain` ergänzen;
2. XML-Metadaten in `Planning` erweitern, ohne FEE-Aufrufe auszuführen;
3. persistente, versionierte Nutzerdaten ausschließlich in `Persistence` ändern;
4. SDK-Objekte in `Discovery` kapseln;
5. tatsächliche Erzeugung weiterhin über `LegacyContainerToFeeExecutionAdapter` und `ContainerToFeeService` ausführen;
6. Bindings auf schreibgeschützte Eigenschaften explizit `Mode=OneWay` setzen und den UI-Smoke-Test erweitern.

`RuntimeVisualPlanBinder` ist der einzige Übergang vom visuellen Plan zu den Legacy-Containern. Vollständige Generierung und `ExistingSimObjectLinkAdapter` dürfen keine zweite Zuordnungslogik aufbauen. Die Auswahlgrenze ist ein vollständiger unterstützter Container: Logik, Signale und Hilfsobjekte bilden im bisherigen Executor eine Abhängigkeitseinheit. Beliebige Signal-/Slot-Neuverdrahtung darf erst eingeführt werden, wenn der Executor dieselbe Änderung deterministisch anwenden und testen kann. Der Sidecar darf die Quell-XML nie überschreiben.

Die Signale vollständiger Generierungen laufen ausschließlich über `SignalResolutionPlanner`: Vor jedem Schreibvorgang werden Interfaces, Variablen und SimObjects neu eingelesen, anschließend werden Tag-/Adress-/Typwidersprüche oder Mehrdeutigkeit gemeldet, identische fehlende Signale dedupliziert und bestehende GUIDs mit `ReuseExistingWithoutUpdate` gebunden. Eine ausdrücklich per Drag-and-drop bestätigte Signal-GUID hat Vorrang vor der automatischen Identitätssuche, verändert das vorhandene Signal aber nicht. Für fehlende Variablen wird eine vorhandene Instanz mit der stabilen GGI-Provider-GUID wiederverwendet; nur ohne eine solche Instanz wird eine neue erzeugt. Fehlende Variablen werden vor BasicFrame/Logik/SimObject angelegt und anschließend ebenfalls nur noch per GUID verwendet. Sidecar-Schema 6 speichert zusätzlich typgebundene Slot-Overrides; `RuntimeVisualPlanBinder` erzeugt daraus ein nur im Speicher angepasstes XML-Dokument für Parser, Ausführung und Root-Provenienz. Die Quelldatei wird nicht überschrieben.

Per-Objekt-Provenienz verwendet `ContainerObjectProvenance` und die namespaced `vibn.container-object.*`-Einträge der FEE-Property `TagComponent.TagEntries`. Neue Objekte werden nicht über `MarkComponent` gekennzeichnet. Der Reader behält alte Mark-Tokens ausschließlich als Migrationsfallback und gruppiert mehrere Objekte derselben Container-ID wieder zu einem fachlichen Container.

Der Bestands-Refresh in Container2FEE Visual darf aus Performancegründen nur Roots mit exakter Root-Provenienz vollständig vergleichen. Die strukturelle Rekonstruktion historischer Roots einschließlich ihrer Slot- und Variablenzuordnungen ist Aufgabe von FEE2Container. Damit bleibt das Öffnen und erneute Generieren auch bei großen Interfacebeständen reaktionsfähig, ohne die Rückwärtskompatibilität des expliziten Reverse-Exports einzuschränken.

`ContainerSlotMultiplicityPolicy` ist die einzige fachliche Quelle für doppelte Slots. Validierung und FEE-Parser müssen sie beide aufrufen. `ContainerBaseClass` bewahrt sämtliche eingelesenen Signale in einer case-insensitiven Slotabbildung auf; deshalb dürfen Binder oder Diagnosen nicht erneut nur über einzelne Reflection-Properties iterieren. Ein mehrfach belegter einfacher `PLC_IN_`-Slot wird erst nach dem Erzeugen der Ziel-Logik über `AssignAdditionalInputFanInsAsync` mit je einem `FeeSimpleMove` pro Signal verbunden. Listen-Slots erledigen dies weiterhin in der konkreten Containerklasse und dürfen nicht zusätzlich in den zentralen Fan-in gelangen.

Der Link-only-Adapter darf keine Erzeugungsmethode aufrufen. Er verlangt den aktuellen Objektbestand aus **Model Validation → Update Objects**, genau ein vorhandenes gleichnamiges `FeeLogic` je ausgewähltem `ILogicSimObjectOwner` und validiert alle Arbeitseinträge vor dem ersten Slot-Schreibzugriff.

### Neues Special Device

1. konkrete Geräteklasse unter `SpecialDevices/Devices` ergänzen;
2. in `DeviceCatalog` und `DeviceFactory` registrieren;
3. optional eine konservative TIA-Erkennung in `SpecialDeviceLogicOption.Suggest` hinzufügen;
4. zuerst nur in die Warteschlange übernehmen, FEE-Erzeugung erst nach Benutzerprüfung starten.

### Neue Rolle oder Reiterberechtigung

Rollenlogik liegt allein in `ViCoRolePolicy`. Die Hauptnavigation bindet ausschließlich die von `MainWindowVM` berechneten Level7-/Level8-/Level9-Gates. Die Regel darf nicht als Zeichenvergleich in mehreren XAML-Dateien dupliziert werden.

## Nebenläufigkeit und UI-Stabilität

- Netzwerk-, Datei-, Kanbanize- und TIA-Arbeit niemals im UI-Thread ausführen.
- Fan-out begrenzen: Arbeitsplatz-Pings sind auf acht, RDP-Sitzungsabfragen auf vier parallele Anfragen begrenzt.
- Bei Benutzerfiltern Abbruchtokens/Debounce einsetzen.
- Schreiboperationen, die dieselbe externe Ressource betreffen, serialisieren oder idempotent machen.
- Fehler einer optionalen Detailabfrage dürfen nie den gesamten Tabellen-Refresh abbrechen.
- Beim Binden von WPF-Eigenschaften `OneWay` einsetzen, wenn keine Quelle geschrieben werden darf. Das verhindert die früheren schreibgeschützten `PropertyPathWorker`-Fehler.

`FeeInterface.GetAllInterfacesAsync` darf `GetAllVariablesAsync` nur einmal je Gesamtsnapshot aufrufen und gruppiert anschließend nach `InterfaceGuid`. `LoadSignalsAsync` bleibt als gezielte Einzelobjekt-API bestehen, darf aber nicht wieder in die Schleife des vollständigen Model-Validation-Refreshs eingebaut werden.

## IBN-Remote-Variante erweitern

Die IBN-Variante ist ein separates Produktartefakt. Neue IBN-Funktionen dürfen nur aufgenommen werden, wenn sie für Arbeitsplatzsuche oder RDP notwendig und schreibgeschützt sind. Wiederverwendete Adapter werden im Projekt `VIBN_Tools.IbnRemote.Infrastructure` explizit einzeln verlinkt; eine Referenz auf `VIBN_Tools`, die vollständige Infrastructure oder TIA-/FEE-Projekte ist unzulässig. Das Präprozessorsymbol `IBN_REMOTE_MINIMAL` entfernt aus gemeinsam genutzten Windows-Adaptern nicht benötigte Pfadfunktionen.

Die IBN-Standardtabelle bleibt bewusst auf PC, Online und aktive Projekte begrenzt. Neue Diagnosefelder gehören in den ausklappbaren Detailbereich, damit die minimale Fenstergröße nicht erneut von einer breiten DataGrid-Spalte abhängig wird.

Nach einer Änderung immer `scripts/Publish-IbnRemote.ps1` ausführen und prüfen, dass der Zielordner ausschließlich `VIBN_Tools_IBN.exe` enthält. Eine versteckte Hauptnavigation ist kein Ersatz für diese Abhängigkeitsgrenze.

## Tests

| Test | Ziel |
| --- | --- |
| `Tests/CoreSmokeTests` | Modelle, Parser, Rollen, RDP-Profil, Kanbanize-Idempotenz, schmale HTTP-Payloads, TIA-Library und Named-Pipe-Protokoll |
| `Tests/ContainerGenerationSmokeTests` | echter ClosedXML-/ZuLi-Import, alle sieben bereitgestellten Interface-/Container-Referenzpaare mit Bilanz- und Regressionsgrenzen, erwartete Fonts-Assembly, PLC_IN/PLC_OUT-Policy und semantischer ContainerFile-A/B-Vergleich mit selektiver Übernahme |
| `Tests/UiStartupSmokeTests` | integrierte WPF-Views einschließlich Interface Operation ohne Startabruf, deferred Tabs, DataGrid-/ComboBox-Bindings, visueller XML-Plan, Sidecar, Undo/Redo und Screenshot-Erzeugung |
| `Tests/Test-TiaHardwareTraversal.ps1` | Gerätegruppen, Proxy-Deduplizierung, Local Session und exakte PN/PN-Bit-/Bytebereiche |
| `scripts/Publish-IbnRemote.ps1` plus kurzer Starttest | minimale, selbstständige IBN-Einzeldatei ohne zusätzliche Publish-Dateien |
| manuelle Abnahme | reale UNC-Pfade, echte Kanbanize-Berechtigung, FEE, Outlook, RDP und TIA Openness |

Vor dem Commit mindestens Core-Smoke, WPF-UI-Smoke und einen Release-Build ausführen. Für reale Systeme zusätzlich [ACCEPTANCE_CHECKLIST.md](ACCEPTANCE_CHECKLIST.md) abarbeiten.

## Kommentare und Lesbarkeit

XML-Kommentare erklären öffentliche Modelle, Grenzen und Invarianten. Kommentare innerhalb einer Methode erklären ausschließlich nicht offensichtliche Entscheidungen, beispielsweise Timeout-, Cache- oder Datenintegritätsgründe. Sie dürfen keinen Code in eigenen Worten wiederholen.

Neue Klassen sollen eine eng abgegrenzte Aufgabe haben. Wenn eine ViewModel-Datei mehrere eigenständige Präsentationsmodelle enthält, diese in getrennte Dateien auslagern – beispielsweise `ViCoWorkstationRowVM` gegenüber `ViCoSearchPageVM`.

`ContainerGenerationPageVM` ist derzeit eine dokumentierte Ausnahme. Die frühere Aufteilung hat den ZULI-Import verändert und wurde deshalb zurückgenommen. Der Regressionstest verarbeitet jetzt alle sieben bereitgestellten Interface-/Container-Paare und bilanziert jedes Eingangssignal als zugeordnet, offen oder gefiltert. Die Referenzcontainer wurden laut XML-Metadaten allerdings mit mehreren Regelständen (`EN V15/V16`, `DE V13/V18` und unversioniert) erzeugt, während nur `DE V17` freigegeben vorliegt. Deshalb sind verifizierte Untergrenzen gegen Verschlechterungen hinterlegt, aber keine fachlich falsche byte- oder slotgenaue Gleichheit erzwungen. Eine weitere Zerlegung der UI-Klasse ist erst mit den exakten zu jedem Paar gehörenden Regelständen vertretbar.

`ContainerFileWorkspaceReader` projiziert exportierte XML-Dateien direkt auf `ContainerData` und `ContainerEntry`. **ContainerFile laden** ersetzt damit nach geladener Requirements-Datei den aktiven Workspace. Der Vergleich arbeitet nicht mehr als unabhängiger Datei-A/B-Dialog: Er erzeugt einen Snapshot des sichtbaren aktiven Workspace, liest genau einen Kandidaten und verwendet anschließend `GenerationWorkspaceReconciler`. Eine zweite Matching- oder Differenzhierarchie ist unzulässig. Damit gelten dieselben stabilen Signal-Schlüssel, feldgenauen Unterschiede, Review-Zustände und selektiven Entscheidungen für Reimport und fertige ContainerFiles. Die Requirements-Datei bleibt für Slotgültigkeit und Min-/Max-Prüfung verbindlich. **Arbeitsstand laden** bleibt die separate Persistenzfunktion für `*.vibn-workspace.xml` und ältere interne XML-Stände.

Die Kanbanize-Synchronisation löst für das Quellboard 1392 den Workflow **Team-Aufgaben** über `/boards/{id}/workflows` auf und filtert Karten über `workflow_id`, bevor Titel-, Lane- und Terminregeln angewendet werden. Ein fehlender Workflow ist ein harter Vorschaufehler; ein stiller Fallback auf alle Karten ist fachlich unzulässig.

TIA-Achsenwerte stehen zentral in `TiaAxisConfigurationPolicy`. Auswahlname und TechnologyObject-Attribute müssen vor der dynamischen Openness-Grenze in statische `string`-Werte konvertiert werden; insbesondere darf `ISet<string>.Contains` nicht über einen dynamischen Operanden gebunden werden. Der Konfigurationsbefehl speichert nie automatisch. `SaveProjectAsync` ruft bewusst das projektweite `Project.Save()` auf und ist daher in der UI als separater, weitreichender Schritt beschrieben.

`TiaLibraryService.ImportAsync` bildet die lokalen Wurzeln `_Programm` und `_Datatype` auf die jeweilige PLC-Gruppenstruktur ab, legt fehlende Gruppen an, importiert mit Überschreiboption und ruft am Ende projektweit `SaveAsync` auf. Die Achsenoption verändert zusätzlich alle gefundenen Achsen und schreibt generierte Axis-XML-Dateien in den gewählten Importordner. `ExportAsync` sucht den angegebenen Bibliotheksordner separat im Baustein- und Datentypbaum und ersetzt gleichnamige lokale XML-Exporte; TIA bleibt dabei read-only. Diese Seiteneffekte müssen in UI und Handbuch sichtbar bleiben.

`SpecialDeviceLogicOption.Selectable` enthält genau einen expliziten leeren Eintrag. `TiaHardwareDeviceRowVM.TryCreate` muss für diesen Zustand ohne Gerät und ohne Validierungsfehler zurückkehren. `AddSelectedHardwareDevices` überspringt ihn unabhängig von `Include`; niemals einen Platzhaltertyp an `DeviceFactory` übergeben. Die ausgeblendeten TIA-Diagnosefelder bleiben im DTO und Mapping-Key erhalten, auch wenn sie nicht als DataGrid-Spalten dargestellt werden.

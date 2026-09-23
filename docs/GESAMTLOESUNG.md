# VIBN Tools – Gesamtübersicht der Solution

## Zielbild

Die Solution verbindet die bestehende VIBN-Tools-Oberfläche mit ViCo, Kanbanize und TIA Portal, ohne die bisherige VIBN-Funktionalität in eine unübersichtliche Monolithklasse zu verschieben. Neue fachliche Regeln liegen in `VIBN_Tools.Core`, externe Zugriffe in `VIBN_Tools.Infrastructure`, WPF-Koordination in `Application/VM` und TIA Openness in einem separaten Bridge-Prozess.

## Funktionslandkarte

| Bereich | Benutzerziel | Wichtige Module |
| --- | --- | --- |
| Project Settings | FEE-PC wählen, Verbindung bestätigen, Projektbasis anlegen | `SettingsPageVM`, `FeeConnectionService`, `WorkstationDirectory` |
| ViCo Übersicht | Arbeitsplätze, Belegung, Projekte, Konfiguration, RDP und Pfade | `ViCoSearchPageVM`, `ViCoWorkstationRowVM`, `LegacyWorkstationCatalog` |
| ViCo Projekte | Projekte öffnen und Favoriten speichern | `ViCoPageVM` |
| Transfer | Dateien und Ordner übertragen | `ViCoCopyPageVM`, `BoundedFileCopyService` |
| TIA Portal | PLC-, Bibliotheks- und Achsenfunktionen | `TiaPortalPageVM`, TIA Client/Bridge |
| Administration | Rollen, Outlook-Termine, Versionen | `ViCoAdministrationPageVM`, `JsonViCoUserRoleStore` |
| Kanbanize | sichere VIBN-Übernahme und eigene Karten | `VibnWorkplaceSynchronizationService`, `KanbanizeCardApiService` |
| SpecialDevices2FEE | manuelle und TIA-basierte Geräteerzeugung; einzige Hardwareansicht | `SpecialDevicePageVM`, `SpecialDeviceHardwareImportVM`, `DeviceFactory` |
| Container2FEE Visual | zusätzlicher XML-Plan, Sidecar und typgeprüftes Drag-and-drop bei unverändertem Executor | `ContainerToFeeVisual/*`, `ContainerToFeeVisualPageVM` |
| IBN Remote | getrennte schreibgeschützte Arbeitsplatz-/RDP-Einzeldatei ohne Volltool und Hersteller-SDKs | `VIBN_Tools.IbnRemote`, `VIBN_Tools.IbnRemote.Infrastructure` |
| bestehende VIBN-Reiter | CAD, Zuli, Container, Modell und Schnittstellen | bestehende ViewModels und FEE-Services |

## Projektstruktur

```text
Application/
  View/                         WPF-XAML und zugehörige Lebenszyklusklassen
  VM/                           ViewModels und Bedienabläufe
  ViCoFeatureBootstrapper.cs    zentraler Composition Root für neue ViCo-/TIA-Dienste

VIBN_Tools.Core/
  Collections/                  plattformneutrale Collection-Helfer
  ViCo/                         Modelle, Verträge, Rollen- und Arbeitsplatzregeln
  Kanbanize/                    Kartenmodelle, Validierung und Synchronisierungsregel

ContainerGeneration/           eigenes Feature-Projekt für Import, Matching, Reimport,
                                XML-Schemas und AI-Bausteine
SharedWpf/                     gemeinsame Observable-Basis, Commands und WPF-Behaviors

VIBN_Tools.Infrastructure/
  ViCo/                         Datei-, Windows-, RDP-, Cache-, Kanbanize- und Rollenadapter
  Kanbanize/                    Businessmap-/Kanbanize-v2-HTTP-Adapter

VIBN_Tools.Tia.Contracts/       serialisierbare TIA-DTOs und Kommandonamen
VIBN_Tools.Tia.Client/          Typed Named-Pipe-Client und Bibliotheksservice
VIBN_Tools.TiaBridge/           isolierter .NET-Framework-/TIA-Openness-Prozess
VIBN_Tools.IbnRemote/           separate, schlanke WPF-Anwendung für Inbetriebnehmer
VIBN_Tools.IbnRemote.Infrastructure/
                                ausschließlich Read-/RDP-Infrastruktur für IBN

SpecialDevices/                 Gerätefactory, Katalog und konkrete Gerätekategorien
ContainerToFeeVisual/           visueller Plan, Persistenz, Discovery und Legacy-Adapter
Tests/CoreSmokeTests/           fachliche und transportnahe Smoke-Tests
Tests/UiStartupSmokeTests/      WPF-XAML-/Binding-Starttest und Anleitungsbilder
docs/                           Anwender-, Betriebs- und Entwicklerdokumentation
```

## Abhängigkeitsrichtung

```text
WPF View → ViewModel → Core-Vertrag → Infrastructure-Adapter
                                     ↘ TIA Client → Named Pipe → TIA Bridge → Openness

IBN View → IBN ViewModel → Core-Vertrag → minimaler Read-/RDP-Adapter
```

`Core` kennt weder WPF noch UNC-Pfade, Windows-Prozesse oder HTTP. Dadurch können Rollen, Terminberechnung, Suchlogik, Duplikatschutz und gemeinsame Collection-Operationen ohne produktive Systeme getestet werden. Die ausführliche Begründung der Projektgrenzen steht in [ARCHITEKTUR_REFACTORING_2026-09.md](ARCHITEKTUR_REFACTORING_2026-09.md).

Die IBN-Anwendung referenziert nicht das vollständige Hauptprojekt. Damit sind FEE, TIA, Generatoren, Verwaltung und Kanbanize-Schreibzugriffe nicht nur ausgeblendet, sondern nicht Bestandteil ihrer Binärabhängigkeiten.

## Wichtige fachliche Regeln

- Kanbanize hat Vorrang vor alten PC-/Benutzer-Zuordnungen. `KONFIGURATION / USER` ist die bevorzugte Quelle.
- Frei bedeutet ausschließlich Backlog/Erledigt; Planung oder In Arbeit bedeutet Belegt.
- Offline-PCs haben keine Remote- oder Pfadbuttons.
- Die VIBN-Synchronisierung nutzt die Quellkarten-ID als eindeutige Ziel-ID. Sie erstellt keine Duplikate und verändert niemals Titel, Position, Beschreibung oder fremde Felder bestehender Karten.
- Die Zeitplanung lautet: Start = Deadline der jeweiligen Quellkarte − 14 Tage; Ende/Deadline = Deadline derselben Quellkarte + 56 Tage.
- `lutzma` ist fest Level9; mindestens zwei unterschiedliche Level9-Benutzer sind beim Speichern erforderlich.
- TIA-Daten werden nur gelesen, bis der Benutzer eine explizite Import-/Speicher-/FEE-Erzeugungsaktion ausführt.

## Erweiterungspunkte

| Änderung | richtiger Ort |
| --- | --- |
| neue reine Arbeitsplatz-/Rollenregel | `VIBN_Tools.Core/ViCo` |
| neuer Kanbanize-Fachablauf | `VIBN_Tools.Core/Kanbanize` und dazugehöriger HTTP-Adapter |
| neuer Wert der KONFIGURATION-Karte | Core-Modell, `LegacyWorkstationCatalog`, Editor-VM und gezielter Adaptertest |
| neue RDP-/Windows-Funktion | Core-Interface plus `DesktopWorkstationServices.cs` |
| neue TIA-Operation | Contracts → Client → Bridge Dispatcher → `TiaOpennessSession` → ViewModel |
| neues Special Device | `SpecialDevices/Devices`, `DeviceCatalog`/`DeviceFactory`, optional Zuordnungsvorschlag |
| bestehende VIBN-Funktion | im zugehörigen vorhandenen ViewModel, ohne ViCo-Transportlogik hineinzuziehen |

Die ausführlichen Regeln befinden sich im [Entwicklerhandbuch](ENTWICKLERHANDBUCH.md) und in der [Klassenreferenz](KLASSENREFERENZ.md).

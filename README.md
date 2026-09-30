# VIBN Tools

VIBN Tools ist die WPF-Desktopanwendung für Modellierung, virtuelle Inbetriebnahme und die dazugehörigen ViCo-Arbeitsabläufe. Die vorhandenen VIBN-Werkzeuge bleiben erhalten. ViCo, Kanbanize und die TIA-Bridge sind als getrennte, testbare Module integriert.

## Einstieg

- [Anwenderhandbuch](docs/BENUTZERHANDBUCH.md) – vollständige Bedienung aller Reiter, Beispiele und Screenshots.
- [Gesamtübersicht der Solution](docs/GESAMTLOESUNG.md) – Funktionslandkarte und Zuständigkeiten.
- [Kanbanize Karten](docs/KANBANIZE_KARTEN.md) – sichere VIBN-zu-Arbeitsplätze-Synchronisierung und manuelle Karten.
- [Rollenverwaltung](docs/ROLLENVERWALTUNG.md) – Level, Sichtbarkeiten und die Level9-Mindestregel.
- [Konfiguration und Betrieb](docs/KONFIGURATION_UND_BETRIEB.md) – Voraussetzungen, Datenquellen, Protokolle und Fehlersuche.
- [Entwicklerhandbuch](docs/ENTWICKLERHANDBUCH.md) und [Klassenreferenz](docs/KLASSENREFERENZ.md) – Architektur, Erweiterungspunkte und Codewegweiser.
- [Software-Audit 2026](docs/AUDIT_REPORT_2026.md) – Executive Summary, Befunde, Risiken und priorisierte Maßnahmen.
- [Architektur-Refactoring 2026-09](docs/ARCHITEKTUR_REFACTORING_2026-09.md) – neue Projektgrenzen, Entscheidung gegen ein Projekt pro Reiter und Begründung der separaten TIA-Bridge.
- [Umsetzungsstatus 2026](docs/UMSETZUNGSSTATUS_2026.md) – ehrlicher Abgleich zwischen implementiert, geprüft und noch offen.
- [TIA-Hardwareauslesung](docs/TIA_OPENNESS_HARDWARE.md) – Datenmodell, Ursache der Altdaten und Live-Abnahme.
- [Container2FEE Visual](docs/CONTAINER2FEE_VISUAL.md) – zusätzlicher Planer, Drag-and-drop-Regeln, Sidecar und Legacy-Ausführung.
- [FEE2Container](docs/FEE2CONTAINER.md) – exakter Provenienz-Rückexport sowie gekennzeichnete Rekonstruktion bestehender BasicFrames.
- [AI-Regelvorschläge](docs/AI_REGELVORSCHLAEGE.md) – nachvollziehbare Vorschläge aus manuellen Generatoränderungen.
- [Lokale Automatisierungsinstallationen](docs/INSTALLATION_DISCOVERY.md) – dynamische TIA-/Openness-, WinCC-, Siemens- und TwinCAT-Erkennung.
- [IBN Remote](docs/IBN_REMOTE.md) – separat minimierte Ein-EXE-Ausgabe nur für Arbeitsplatzsuche und RDP.
- [FEE2SpecialDevices](docs/FEE2SPECIALDEVICES.md) – provenance-basierter Reverse-Export künftig erzeugter Special Devices.
- [Dependency Management](docs/DEPENDENCY_MANAGEMENT.md), [Deployment](docs/DEPLOYMENT.md) und [Diagramme](docs/ARCHITECTURE_DIAGRAMS.md).
- [Installation und Installer](docs/INSTALLATION_UND_INSTALLER.md) – Setup erzeugen, auf andere Rechner übertragen und SDK-/Buildfehler beheben.

## Wichtige Eigenschaften

- ViCo verwendet einen gemeinsamen, dynamischen PC-/Benutzerbestand aus Kanbanize; es gibt keine fest kompilierte PC-Benutzer-Zuordnung.
- Die ViCo-Übersicht trennt aktive Karten in **Planung** und **In Arbeit**, zeigt Start/Ende und öffnet die konkrete Kanbanize-Karte über ihre ID; Statusmarker werden nicht mehr im Projektnamen angezeigt.
- Der normale Button **Remote Desktop** legt den lokalen Credential-Manager-Eintrag aus `VIBN_RDP_PASSWORD` nur für den Start an und entfernt ihn nach 20 Sekunden. **RDP mit Anmeldedaten** öffnet den Windows-Anmeldedialog ohne diesen temporären Eintrag.
- API-Key und RDP-Passwort werden verdeckt in Project Settings beziehungsweise der IBN-Oberfläche pro Windows-Benutzer konfiguriert; PowerShell ist im normalen Ablauf nicht nötig.
- Die ViCo-Übersicht zeigt den Countdown bis zum nächsten Kanbanize-AutoUpdate; das 1–1440-Minuten-Intervall wird lokal pro Benutzer gespeichert.
- Kanbanize synchronisiert keine Duplikate und ändert bei vorhandenen generierten Karten ausschließlich den berechneten Starttermin und die Deadline.
- Die TIA-Openness-Kommunikation läuft in einem separaten Bridge-Prozess; ein TIA-Fehler beendet nicht die WPF-Anwendung.
- Rollen ersetzen Lizenzanfragen. Level7 schaltet CAD Wizard, Container Generation und den bisherigen Container2Fee-Reiter frei; Level8 zusätzlich Kanbanize. Container2FEE Visual, FEE2Container, FEE2SpecialDevices, AI-Test und Administration sind ausschließlich ab Level9 sichtbar.

## Build und lokale Prüfungen

Die vollständige Solution ist `VIBN_Tools_App.sln`. Für einen Build werden Windows, .NET 8, Grob.UX und das FEE-SDK benötigt. Für reale TIA-Funktionen muss außerdem eine unterstützte Siemens-TIA-Portal-/Openness-Installation vorhanden sein.

Auf einem neuen Entwicklungsrechner zuerst `Prepare-Development.cmd` ausführen und Visual Studio danach neu starten. Die eingecheckte `.vsconfig` bietet fehlende Visual-Studio-Komponenten an; das Vorbereitungsskript überspringt unvollständige FEE-Installationen, bietet bei mehreren vollständigen SDKs eine Auswahl (neueste als Standard) und restauriert die Solution. Anschließend in der Visual-Studio-Startauswahl das geteilte Profil **VIBN Tools** wählen und F5 drücken. Zeigt eine ältere Visual-Studio-Version das `.slnLaunch`-Profil nicht an, im Projektmappen-Explorer `VIBN_Tools` per Rechtsklick als Startprojekt festlegen. Der Debug-Build ist selbstenthaltend für `win-x64`.

```powershell
.\Build.ps1
dotnet run --project Tests/CoreSmokeTests/VIBN_Tools.Core.SmokeTests.csproj --configuration Release
dotnet run --project Tests/ContainerGenerationSmokeTests/VIBN_Tools.ContainerGeneration.SmokeTests.csproj --configuration Release
dotnet run --project Tests/UiStartupSmokeTests/VIBN_Tools.UiStartup.SmokeTests.csproj --configuration Release
.\Tests\Test-TiaHardwareTraversal.ps1
```

`Build.ps1` erkennt die höchste installierte fe.screen-sim-V5-SDK-Version automatisch. Für einen abweichenden Pfad kann `-FeeScreenSimRoot` oder die Umgebungsvariable `FEE_SCREEN_SIM_ROOT` verwendet werden. Portable ZIP- und Setup-Erstellung sind in [DEPLOYMENT.md](docs/DEPLOYMENT.md) beschrieben.

Die lokalen Smoke-Tests verwenden keine produktiven Kanbanize-Boards und keine realen TIA-Projekte. Die zusätzliche Live-Abnahme ist in [ACCEPTANCE_CHECKLIST.md](docs/ACCEPTANCE_CHECKLIST.md) beschrieben.

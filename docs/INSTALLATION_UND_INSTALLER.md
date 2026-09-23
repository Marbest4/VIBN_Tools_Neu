# Installation und Installer

Diese Anleitung unterscheidet zwischen dem Build-Rechner, auf dem das Setup erzeugt wird, und dem Zielrechner, auf dem ein Benutzer VIBN Tools verwendet.

## 1. Bereits erzeugtes Setup auf einem anderen Rechner verwenden

Wenn `VIBN_Tools_Setup.exe` bereits vorliegt:

1. Nur `VIBN_Tools_Setup.exe` auf den Zielrechner übertragen.
2. Setup mit einem Benutzer ausführen, der Software installieren darf.
3. Installationsordner bestätigen und optional die Desktopverknüpfung auswählen.
4. `VIBN_Tools.exe` beziehungsweise die Desktopverknüpfung starten.
5. In **Project Settings → Geschützte Zugangsdaten** FEE-Benutzer/-Passwort, API-Key und RDP-Kennwort eingeben. Die Statusanzeige bestätigt die Konfiguration; eine CMD-Datei oder ein Neustart ist nicht erforderlich.

Visual Studio und eine separate .NET-Installation werden nicht benötigt. Für TIA-Funktionen müssen eine passende TIA-/Openness-Version, .NET Framework 4.8 und die Siemens-Openness-Benutzergruppe vorhanden sein. FEE-Funktionen benötigen die freigegebene FEE-Laufzeit beziehungsweise die zugehörigen betrieblichen Dienste und Lizenzen.

## 2. Setup auf dem Build-Rechner erzeugen

Voraussetzungen:

- Windows x64
- vollständige fe.screen-sim-SDK-Installation
- Zugriff auf das private Grob.UX-NuGet-Paket beziehungsweise den internen Feed
- Inno Setup 6
- .NET SDK

Aus dem Repository-Stamm:

```powershell
.\Build-Installer.bat
```

Dieser eine Befehl restauriert, veröffentlicht und kompiliert das Setup. Ein separates `Build.ps1` ist zur Setup-Erstellung nicht mehr erforderlich. Der Installer-Pfad erzeugt auch kein unbenutztes Portable-ZIP.

Falls Grob.UX aus einem zusätzlichen lokalen Feed kommt:

```powershell
.\Build-Installer.bat -AdditionalPackageSource "D:\InternerNuGetFeed"
```

Ein explizites SDK ist nur erforderlich, wenn mehrere vollständige Installationen vorhanden sind oder eine bestimmte Version verwendet werden soll:

```powershell
.\Build-Installer.bat -FeeScreenSimRoot "C:\Program Files\fe.screen-sim V5\5.0.11.48415"
```

Das fertige Setup liegt anschließend unter:

```text
artifacts\installer\VIBN_Tools_Setup.exe
```

Die Datei `installer\VIBN_Tools.iss` ist nicht das fertige Setup, sondern das Inno-Setup-Rezept. Normalerweise wird sie nicht manuell geöffnet; `Build-Installer.bat` veröffentlicht zuerst die Anwendung und ruft danach den Inno-Compiler auf. Inno Setup 6 baut nicht den C#-Code und löst keine NuGet-Pakete auf. Es verpackt den bereits self-contained veröffentlichten Ordner, erzeugt Verknüpfungen und stellt die Deinstallation bereit.

Der Publish enthält nicht den vollständigen FEE-Installationsordner. Das Skript startet bei den direkten Projektverweisen und der `ReadingUnitPlugin.dll` und nimmt nur deren rekursiv benötigte `FS.*.dll` auf. Fremde FEE-Plugins, unbenutzte FS-Werkzeuge oder mitgelieferte Drittanbieter-DLLs werden nicht übernommen. Dadurch bleibt der Ordner kleiner und kann insbesondere keine NuGet-DLL wie `SixLabors.Fonts.dll` überschreiben.

## 3. SDK-Auswahl

`Build.ps1`, der Portable Publisher und der Installer prüfen nacheinander:

1. `-FeeScreenSimRoot`
2. `FEE_SCREEN_SIM_ROOT`
3. installierte Unterordner von `C:\Program Files\fe.screen-sim V5`, absteigend nach Version
4. `external\fe-screen-sim` als CI-/Testfallback
5. `SDK` als unveränderter flacher Repository-Buildfallback

Produktinstallationen benötigen `Bin\FS.SDK.dll`; die beiden Repository-Fallbacks dürfen zusätzlich das flache Layout mit `FS.SDK.dll` direkt im Stammordner verwenden. Ordner ohne diese Markierung werden mit einer Warnung übersprungen. Ohne explizite oder zuvor gespeicherte Auswahl wird die höchste vollständige Installation gewählt und als

```text
FEE SDK erkannt: Version ... unter '...'
```

ausgegeben. `Prepare-Development.cmd` listet mehrere vollständige SDKs absteigend auf, markiert die neueste als Standard und lässt den Entwickler den Referenzordner auswählen. Die Auswahl wird als `FEE_SCREEN_SIM_ROOT` für den aktuellen Windows-Benutzer gespeichert. Visual Studio muss danach neu gestartet werden, weil bereits geladene Projektverweise nicht innerhalb eines laufenden Prozesses ausgetauscht werden können. Eine Laufzeit-Auswahl in Project Settings wäre technisch zu spät und ist deshalb bewusst nicht vorhanden.

Der eingecheckte flache Ordner `SDK` enthält den vollständigen derzeit benötigten Abhängigkeitsabschluss. Hauptprojekt, `Grob Generation Interface` und Solution bauen gemeinsam. Einzelne Assemblies aus anderen Versionen zu mischen bleibt nicht unterstützt; vor Installer/Publish berechnet das Buildskript weiterhin die rekursive Runtime-Closure und bricht bei jeder Lücke ab.

### Direktes Debuggen in Visual Studio

Auf einem neuen Entwicklungsrechner:

1. Repository vollständig klonen oder entpacken.
2. `VIBN_Tools_App.sln` öffnen. Visual Studio liest die eingecheckte `.vsconfig` und bietet fehlende Komponenten an: .NET-Desktop, .NET 8, .NET-Framework-4.8-SDK/Targeting Pack und NuGet.
3. Einmal `Prepare-Development.cmd` starten. Das Skript überspringt unvollständige FEE-Installationsordner. Bei mehreren vollständigen Versionen die gewünschte auswählen; Enter übernimmt die neueste. Das Skript setzt `FEE_SCREEN_SIM_ROOT` für den Benutzer und restauriert alle NuGet-Pakete.
4. Visual Studio nach dem erstmaligen Setzen der Umgebungsvariable neu starten.
5. In der Startauswahl das geteilte Profil **VIBN Tools** wählen und F5 drücken. Falls eine ältere Visual-Studio-Version `.slnLaunch` noch nicht anzeigt, `VIBN_Tools` per Rechtsklick als Startprojekt festlegen.

Die Debug-Konfiguration erzeugt eine selbstenthaltende x64-Anwendung unter `artifacts\build\Debug\net8.0-windows\win-x64`. Damit hängt F5 nicht von einer global installierten .NET-8-Patchversion ab. Visual Studio beziehungsweise das Vorbereitungsskript führt die NuGet-Wiederherstellung automatisch aus.

Falls Grob.UX nicht aus einer bereits konfigurierten Paketquelle erreichbar ist:

```powershell
.\Prepare-Development.cmd -AdditionalPackageSource "D:\InternerNuGetFeed"
```

Nicht automatisierbar sind die Zugriffsberechtigung auf den privaten Grob.UX-Feed sowie die Bereitstellung eines vollständigen, lizenzkonform verwendbaren FEE-SDKs.

## 4. Fehler `Metadata file ... VIBN_Tools.dll could not be found`

Diese Meldung kommt normalerweise aus einem nachgelagerten Projekt, beispielsweise dem UI-Startup-Test. Sie bedeutet, dass das Hauptprojekt `VIBN_Tools` vorher nicht erfolgreich gebaut wurde. Die Metadatenmeldung ist nicht die eigentliche Ursache.

Vorgehen:

1. In der Fehlerliste nach dem ersten Fehler des Projekts `VIBN_Tools` suchen.
2. SDK-Ausgabe kontrollieren.
3. Solution bereinigen und über das Skript bauen:

```powershell
dotnet clean VIBN_Tools_App.sln
.\Build.ps1
```

Bei einer vollständigen SDK-Version muss der Build mit `VIBN_Tools.dll` unter `artifacts\build\Release\net8.0-windows` enden.

## 5. Portable ZIP statt Setup

Für Support- oder Offlinefälle kann ein self-contained ZIP erzeugt werden:

```powershell
.\Publish-Portable.bat
```

Das ZIP vollständig entpacken und anschließend `VIBN_Tools.exe` starten. Dateien innerhalb des ZIPs dürfen nicht einzeln herauskopiert werden, weil die kuratierten FEE-Assemblies, die TIA-Bridge und die .NET-Laufzeitdateien gemeinsam benötigt werden.

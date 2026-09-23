# Deploymentkonzept

## Vergleich

| Variante | Größe | Stabilität mit FEE/TIA | Updates | Benutzerkomfort | Empfehlung |
| --- | --- | --- | --- | --- | --- |
| Framework-dependent | Klein | Mittel; .NET muss vorhanden sein | Extern | Mittel | Für Entwickler |
| Self-contained Ordner | Groß | Sehr hoch | Paket ersetzen | Hoch | Portable Standardbasis |
| Single-file | Mittel/groß | Risiko bei dynamisch geladenen/native DLLs | Paket ersetzen | Hoch | Derzeit nicht verwenden |
| MSIX | Mittel | Gut, aber Installations-/Signaturregeln | Sehr gut | Hoch | Spätere verwaltete Verteilung |
| ClickOnce | Mittel | Gut | Sehr gut | Hoch | Alternative bei einfacher Updatequelle |
| WiX | Mittel | Sehr hoch | Eigene Logik | Hoch | Enterprise-MSI, hoher Aufwand |
| Inno Setup | Mittel | Sehr hoch | Eigene Logik | Sehr hoch | Empfohlenes Setup jetzt |
| Portable ZIP | Groß | Sehr hoch | ZIP ersetzen | Hoch | Support-/Offline-Fallback |

## Entscheidung

Die technische Basis des **vollständigen Tools** ist ein self-contained, mehrteiliges `win-x64`-Publish. Single-file ist dort wegen FEE-SDK, ML-native Runtimes, Plugins und separat gestarteter TIA-Bridge nicht die stabile Wahl. Für Endanwender wird dieser Ordner mit Inno Setup zu einer einzigen `VIBN_Tools_Setup.exe` verpackt. Nach der Installation startet der Benutzer ausschließlich `VIBN_Tools.exe`; Visual Studio und eine separate .NET-Installation sind nicht nötig.

Die getrennte **IBN-Remote-Anwendung** hat diese dynamischen Herstellerabhängigkeiten bewusst nicht. Sie kann daher stabil als self-contained Single-file veröffentlicht werden. Sie enthält ausschließlich Arbeitsplatzsuche/-status und RDP, nicht das vollständige Tool.

Grundlagen: [Microsoft – Windows-Verteilungswege](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path), [Microsoft – self-contained veröffentlichen](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app) und [Microsoft – Single-file Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

TIA Portal/Openness und fe.screen-sim bleiben fachliche Laufzeitvoraussetzungen, sofern die jeweilige Funktion genutzt wird.

Das Publish-Skript berechnet aus den direkten Projektverweisen und der `ReadingUnitPlugin.dll` rekursiv die tatsächlich benötigte `FS.*`-Assemblyclosure. Ein reines Compile-Test-SDK wird abgelehnt, statt ein Paket zu erzeugen, das erst beim Benutzerstart mit `FileNotFoundException` endet. Es wird **nicht** mehr der komplette FEE-`Bin`- oder Pluginordner kopiert. In das Publish gelangen nur diese errechneten `FS.*.dll` sowie die explizit referenzierte `ReadingUnitPlugin.dll`. NuGet- und .NET-Runtime-Dateien stammen weiterhin aus dem reproduzierbaren `dotnet publish`.

Die Einschränkung verhindert zwei frühere Probleme: unnötige FEE-Plugins vergrößerten und verschmutzten den Publish-Ordner; gleichnamige Fremd-DLLs konnten nach dem Build NuGet-Abhängigkeiten überschreiben. Letzteres war die Ursache des `SixLabors.Fonts.FontMetrics.TryGetGlyphMetrics`-Fehlers beim ZuLi-Import.

## Befehle

Eine vollständige Schritt-für-Schritt-Anleitung für Build- und Zielrechner befindet sich in [INSTALLATION_UND_INSTALLER.md](INSTALLATION_UND_INSTALLER.md).

Portable Paket:

```powershell
.\scripts\Publish-Portable.ps1 -FeeScreenSimRoot 'C:\Program Files\fe.screen-sim V5\<Version>'
```

Setup-EXE mit installiertem Inno Setup 6:

```powershell
.\scripts\Build-Installer.ps1 -FeeScreenSimRoot 'C:\Program Files\fe.screen-sim V5\<Version>'
```

Separate IBN-Remote-Einzeldatei (kein FEE-SDK und kein Inno Setup erforderlich):

```powershell
.\scripts\Publish-IbnRemote.ps1
```

`Build-Installer.ps1` führt Restore und self-contained Publish genau einmal aus und ruft danach den Inno-Compiler auf. Dabei wird kein zwischenzeitliches ZIP mehr erzeugt. Inno Setup 6 ist ausschließlich der Verpacker für Dateien, Verknüpfungen und Deinstallation; die Anwendung wird bereits vorher durch `dotnet publish` gebaut. Für das portable ZIP ist Inno Setup nicht erforderlich.

Ergebnisse:

- `artifacts\publish\VIBN_Tools-win-x64.zip`
- `artifacts\publish\VIBN_Tools-win-x64.zip.sha256.txt`
- `artifacts\installer\VIBN_Tools_Setup.exe`
- `artifacts\publish\IBN-Remote\VIBN_Tools_IBN.exe`

## Release-Checkliste

1. Release-Build und beide Smoke-Tests grün.
2. Reale TIA- und FEE-Abnahme durchgeführt.
3. Setup und Binärdateien signiert.
4. SHA-256 veröffentlicht und geprüft.
5. Installation auf sauberem Windows-x64-PC getestet.
6. Start, Konfiguration, ViCo/Kanbanize, TIA-Bridge und Deinstallation getestet.

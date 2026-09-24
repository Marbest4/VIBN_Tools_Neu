# IBN Remote – separate Ein-EXE-Ausgabe

## Abgrenzung

`VIBN_Tools_IBN.exe` ist eine separate WPF-Anwendung für Inbetriebnehmer. Sie ist kein Hauptprogramm mit ausgeblendeten Tabs. Der Build enthält nur:

- neutrale ViCo-Modelle und Suche aus `VIBN_Tools.Core`;
- einen minimal kompilierten Read-/RDP-Ausschnitt in `VIBN_Tools.IbnRemote.Infrastructure`;
- Arbeitsplatzliste, Filter, Onlineprüfung und RDP-Sitzungsdiagnose;
- automatische RDP-Anmeldung mit dem Benutzer der KONFIGURATION-Karte.

Die read-only Startansicht zeigt ausschließlich **PC**, **Online**, **In Arbeit (Projekt)**, **Ende In Arbeit**, **Standort**, **Sonstiges** und **Software**. Zusätzlich stehen Monitorauswahl und der automatische RDP-Start zur Verfügung. Karten oder Konfigurationsdaten können nicht geändert werden.

Nicht enthalten sind FEE-SDK, TIA-Bridge, Container-/CAD-/Modellfunktionen, Administration, Dateiübertragung und Kanbanize-Schreiboperationen. Der Kanbanize-Zugriff des IBN-Clients besteht ausschließlich aus GET-Abfragen und einem lokalen Cache.

## Erzeugen und verteilen

Auf dem Buildrechner genügt:

```powershell
.\scripts\Publish-IbnRemote.ps1
```

Das Skript fragt nacheinander den festen Filter, den Kanbanize-/Businessmap-API-Key und das gemeinsame RDP-Passwort ab. API-Key und Passwort werden bei der Eingabe nicht angezeigt. Für einen automatisierten Testlauf können alle Werte weiterhin ausdrücklich als Parameter übergeben werden:

```powershell
.\scripts\Publish-IbnRemote.ps1 -InWorkFilter 'GM7283' -ApiKey 'abcde' -RemoteDesktopPassword 'fghijk'
```

Das Ergebnis ist:

```text
artifacts\publish\IBN-Remote\VIBN_Tools_IBN.exe
```

Die Datei ist `win-x64`, self-contained und single-file. Auf dem Ziel-PC sind weder Visual Studio noch eine separate .NET-Installation, FEE oder TIA erforderlich. Nur diese EXE wird verteilt. Vor einer breiten Verteilung muss sie wie die Hauptanwendung signiert und auf einem sauberen Unternehmens-PC geprüft werden.

## Vorkonfigurierte Testwerte

Der feste Filter sowie API-Key und RDP-Passwort werden beim Publish gesetzt. Nicht übergebene Werte fragt das Skript über die Konsole ab. Die beiden verdeckt eingegebenen Zugangswerte werden nur für den untergeordneten Buildprozess als temporäre Prozess-Umgebungsvariablen gesetzt und danach wieder entfernt; sie stehen damit nicht in der von `dotnet publish` gestarteten Befehlszeile. Eine Konfigurationsoberfläche gibt es nicht. Der API-Key wird für den read-only Kanbanize-Abruf verwendet. Beim RDP-Start wird das konfigurierte Passwort kurzzeitig als `TERMSRV/<PC>`-Eintrag angelegt und nach 20 Sekunden wieder entfernt. Logs liegen unter `%LOCALAPPDATA%\GROB\VIBN_Tools_IBN\Logs` und enthalten die Werte nicht.

Die eingebetteten Zeichenfolgen sind weiterhin mit üblichen .NET-Werkzeugen aus der EXE auslesbar. Das Skript warnt deshalb ausdrücklich bei Werten außerhalb der Testplatzhalter `12345`/`67890`. Die verdeckte Konsoleneingabe schützt nur Bildschirm, Shell-Verlauf und Publish-Befehlszeile; sie macht die erzeugte EXE nicht zu einem Secret-Speicher. Ein produktiver Rollout benötigt einen Windows-/gerätegebundenen Secret-Speicher oder ein freigegebenes Unternehmens-Secretsystem.

## Technische Grenze

Eine einzelne EXE verhindert nicht das Kopieren der Anwendung durch einen berechtigten Benutzer. Die Trennung stellt sicher, dass nicht benötigte Toolfunktionen und Hersteller-SDKs gar nicht im IBN-Paket vorhanden sind; sie ersetzt keine Windows-Geräteverwaltung, Code-Signatur oder zentrale Softwareverteilung.

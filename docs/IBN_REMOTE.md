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

Das Ergebnis ist:

```text
artifacts\publish\IBN-Remote\VIBN_Tools_IBN.exe
```

Die Datei ist `win-x64`, self-contained und single-file. Auf dem Ziel-PC sind weder Visual Studio noch eine separate .NET-Installation, FEE oder TIA erforderlich. Nur diese EXE wird verteilt. Vor einer breiten Verteilung muss sie wie die Hauptanwendung signiert und auf einem sauberen Unternehmens-PC geprüft werden.

## Vorkonfigurierte Testwerte

Der feste Filter wird mit `-InWorkFilter` beim Publish gesetzt. Zusätzlich enthält die EXE auf ausdrücklichen Wunsch die ungültigen Platzhalter `12345` (Kanbanize API-Key) und `67890` (RDP-Passwort). Eine Konfigurationsoberfläche gibt es nicht. Der API-Key wird für den read-only Kanbanize-Abruf verwendet. Beim RDP-Start wird der Passwortplatzhalter kurzzeitig als `TERMSRV/<PC>`-Eintrag angelegt und nach 20 Sekunden wieder entfernt. Logs liegen unter `%LOCALAPPDATA%\GROB\VIBN_Tools_IBN\Logs` und enthalten die Werte nicht.

Die eingebetteten Zeichenfolgen sind mit üblichen .NET-Werkzeugen aus der EXE auslesbar. Sie dürfen deshalb nicht durch echte Zugangsdaten ersetzt werden. Ein produktiver Rollout benötigt einen Windows-/gerätegebundenen Secret-Speicher oder ein freigegebenes Unternehmens-Secretsystem.

## Technische Grenze

Eine einzelne EXE verhindert nicht das Kopieren der Anwendung durch einen berechtigten Benutzer. Die Trennung stellt sicher, dass nicht benötigte Toolfunktionen und Hersteller-SDKs gar nicht im IBN-Paket vorhanden sind; sie ersetzt keine Windows-Geräteverwaltung, Code-Signatur oder zentrale Softwareverteilung.

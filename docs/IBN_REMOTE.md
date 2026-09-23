# IBN Remote – separate Ein-EXE-Ausgabe

## Abgrenzung

`VIBN_Tools_IBN.exe` ist eine separate WPF-Anwendung für Inbetriebnehmer. Sie ist kein Hauptprogramm mit ausgeblendeten Tabs. Der Build enthält nur:

- neutrale ViCo-Modelle und Suche aus `VIBN_Tools.Core`;
- einen minimal kompilierten Read-/RDP-Ausschnitt in `VIBN_Tools.IbnRemote.Infrastructure`;
- Arbeitsplatzliste, Filter, Onlineprüfung und RDP-Sitzungsdiagnose;
- automatische RDP-Anmeldung und RDP mit Windows-Anmeldedialog.

Die Startansicht ist für kleine Notebook-/Serviceauflösungen ausgelegt und zeigt nur **PC**, **Online** und **Projekte**. Belegung, KONFIGURATION-Felder, Benutzer, RDP-Sitzung, letzte Anmeldung, Monitorauswahl und Verbindungsbuttons stehen im ausklappbaren Bereich **Erweiterte Anzeige und Verbindung**. Das Fenster startet mit 680 × 520 Pixeln und kann bis 480 × 340 Pixel verkleinert werden; die Detailbereiche besitzen eigene Scrollleisten.

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

## Benutzerkonfiguration

Im ausklappbaren Bereich **Zugangsdaten** können Kanbanize API-Key und Remote-Desktop-Passwort verdeckt eingegeben, geschützt im Windows Credential Manager gespeichert und einzeln gelöscht werden. Die Oberfläche zeigt nur **Konfiguriert** beziehungsweise **Nicht konfiguriert**, nie den gespeicherten Wert. Ein neu gespeicherter API-Key löst direkt eine Aktualisierung aus; ein Neustart ist nicht erforderlich.

Die Werte gelten pro Windows-Benutzer und Rechner und müssen deshalb auf jedem Ziel-PC beziehungsweise für jedes verwendete Windows-Konto einmal eingetragen werden. Die frühere CMD-/PowerShell-Ersteinrichtung ist für den normalen Betrieb nicht mehr nötig. Bestehende Werte in den früheren Benutzervariablen werden beim ersten erfolgreichen Zugriff in den Credential Manager migriert und anschließend aus der Umgebung gelöscht. Für administrierte Rollouts ist ein organisationskonformes Secretsystem statt eines Repository- oder Skriptwerts erforderlich.

Ohne Key liest der Client den gemeinsamen ViCo-Cache schreibgeschützt. Der Dialog-Button funktioniert ohne hinterlegtes RDP-Passwort. Kennwort und API-Key werden nicht in die EXE kompiliert oder protokolliert. Der eigentliche `TERMSRV/<PC>`-Eintrag bleibt nur für den RDP-Start bestehen und wird nach 20 Sekunden entfernt; die VIBN-Tools-Einträge im Credential Manager bleiben bis zum expliziten Löschen erhalten. Logs liegen unter `%LOCALAPPDATA%\GROB\VIBN_Tools_IBN\Logs`.

## Technische Grenze

Eine einzelne EXE verhindert nicht das Kopieren der Anwendung durch einen berechtigten Benutzer. Die Trennung stellt sicher, dass nicht benötigte Toolfunktionen und Hersteller-SDKs gar nicht im IBN-Paket vorhanden sind; sie ersetzt keine Windows-Geräteverwaltung, Code-Signatur oder zentrale Softwareverteilung.

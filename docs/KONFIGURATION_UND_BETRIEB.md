# Konfiguration, Betrieb und Fehlersuche

## Voraussetzungen

- Windows-Desktop; beim Visual-Studio-/framework-dependent-Start mit .NET-8-Desktop-Runtime, beim self-contained Setup beziehungsweise der IBN-EXE ohne separat installierte .NET-Runtime;
- Grob.UX und das kompatible FEE-/fe.screen-sim-SDK;
- Zugriff auf die vorgesehenen UNC-Pfade für ViCo-Caches, Projekte, Rollen und Versionen;
- Kanbanize-/Businessmap-Zugriff für Live-Aktualisierung und Kartenfunktionen;
- für Live-TIA: passende lokale Siemens-TIA-Portal-/Openness-Installation und Berechtigung;
- optional Outlook für Termine im Verwaltungsreiter.

Ohne Unternehmensnetz startet die Oberfläche weiterhin. Live-Daten, Kartenaktionen, Netzpfade oder TIA-Operationen können dann erwartbar nicht verfügbar sein und werden protokolliert.

## Zentrale Konfigurationsstellen

| Wert | Ort | Zweck |
| --- | --- | --- |
| FEE-Benutzer/-Passwort | Windows Credential Manager: `GROB/VIBN_Tools/FeeUsername`, `GROB/VIBN_Tools/FeePassword` | Anmeldung erst unmittelbar beim FEE-Verbindungsversuch |
| Kanbanize API-Schlüssel | Windows Credential Manager: `GROB/VIBN_Tools/KanbanizeApiKey` | geschützter Live-Zugang für ViCo und Kartenreiter |
| RDP-Kennwort | Windows Credential Manager: `GROB/VIBN_Tools/RemoteDesktopPassword` | geschützte Quelle für den kurzlebigen `TERMSRV/<PC>`-Eintrag |
| Rollen-Datei | `VIBN_VICO_ROLES_FILE` | optionaler zentraler Pfad zu `roles.json` |
| ViCo-Pfade | `VIBN_Tools.Infrastructure/ViCo/ViCoPathsOptions.cs` | Caches, Projekte, Versionen und Standard-Arbeitsordner |
| TIA-Bridge | `Application/ViCoFeatureBootstrapper.cs` | Bridge-Executable, Pipe pro Prozess, lokale Versionserkennung |
| Logging | `ApplicationLogService` / vorhandene Log-Konfiguration | sichtbares Diagnosepanel und Logdatei |

API-Schlüssel und Kennwörter gehören nicht in Quellcode, Screenshots, Tickets oder das Diagnoseprotokoll. In **Project Settings → Geschützte Zugangsdaten** können FEE-Benutzer/-Passwort, Kanbanize-Key und RDP-Passwort gespeichert, ihr Vorhandensein geprüft und sie getrennt gelöscht werden. Leere Passwort-/Key-Felder überschreiben bestehende Werte nicht. Die Änderung gilt sofort; PowerShell, CMD-Datei und Anwendungsneustart sind im normalen Ablauf nicht mehr erforderlich.

Die früheren Benutzer-Umgebungsvariablen werden nur noch als Migrationsquelle unterstützt. Nach erfolgreichem Schreiben in den Credential Manager löscht die Anwendung sie. Ein Alt-Rollout kann sie daher übergangsweise noch setzen:

```powershell
[Environment]::SetEnvironmentVariable('VIBN_VICO_KANBANIZE_API_KEY', '<BUSINESSMAP-API-KEY>', 'User')
[Environment]::SetEnvironmentVariable('VIBN_RDP_PASSWORD', '<REMOTE-PASSWORT>', 'User')
[Environment]::SetEnvironmentVariable('VIBN_FEE_USERNAME', '<FEE-BENUTZER>', 'User')
[Environment]::SetEnvironmentVariable('VIBN_FEE_PASSWORD', '<FEE-PASSWORT>', 'User')
```

`Configure-VIBN-Tools.cmd` bleibt im Quellrepository ausschließlich als Kompatibilitäts-/Migrationsassistent erhalten und wird nicht in Portable-/Setup-Pakete kopiert. Die UI schreibt direkt in den Windows Credential Manager.

Credential-Manager-Einträge sind benutzer- und rechnerbezogen und müssen je Ziel-PC/Windows-Konto eingerichtet werden. Sie werden nicht in die EXE oder Logs geschrieben und nicht über GitHub synchronisiert. Für eine zentral administrierte Verteilung ist ein freigegebenes Unternehmens-Secretsystem erforderlich.

Kanbanize/Businessmap verwendet hier keinen Benutzerpasswort-Login, sondern den API-Key im Header `apikey`. Ein abgelaufener, rotierter oder für das Board nicht berechtigter Key führt zu 401/403; eine 400-Feldvalidierung ist dagegen ein Abfragefehler. Der Refresh wiederholt nur sichere GET-Anfragen bei Netzwerk-, 408-, 429- und 5xx-Fehlern.

## Datenquellen und Aktualisierung

| Information | Quelle | Aktualisierung |
| --- | --- | --- |
| PCs, Benutzer, Projekte, Software und KONFIGURATION | Arbeitsplätze-Board / strukturierter Cache | Start, Daten aktualisieren und konfigurierbares AutoUpdate |
| Robotik-Informationen | Robotik-Board / Cache | Start, Daten aktualisieren und konfigurierbares AutoUpdate |
| Project-Settings-Dropdown | gemeinsames `WorkstationDirectory` plus Ping | beim Start, Filter und manueller Aktualisierung |
| Rollen | `roles.json` | Anwendungstart und Verwaltungs-Refresh |
| Kartenpositionen/Karten | Kanbanize v2 | ausdrücklich durch Kartenreiter |
| TIA-Versionen | lokale Siemens-PublicAPI-Pfade | beim Öffnen der TIA-Ansicht |
| verwendete FEE-SDK-Version | `FS.SDK.dll` neben der laufenden Anwendung | beim Öffnen von Project Settings |
| lokal installierte FEE-Version | ausschließlich lokale Installationsordner/Registry-Einträge, deren Installationspfad `Bin\FS.SDK.dll` enthält | beim Öffnen von Project Settings |

## Häufige Fehler

### PC fehlt im Project-Settings-Dropdown

1. **Liste aktualisieren** drücken.
2. Filter löschen oder präzisieren.
3. Prüfen, ob der PC aktuell auf Ping antwortet – Offline-PCs erscheinen absichtlich nicht.
4. In ViCo **Daten aktualisieren** drücken und das Diagnoseprotokoll auf Cache-/Kanbanize-Fehler prüfen.

### Connect zeigt trotzdem nicht „verbunden“

Die Anwendung setzt `Connected to` erst, wenn der normale Zustandsmonitor der FEE-SDK den Wechsel auf `Connected` meldet. Der Connect-Button ruft `CoreApi.Connect` genau einmal auf; es gibt keinen zusätzlichen Bestätigungs-Timeout und kein automatisches Trennen eines noch laufenden SDK-Handshakes. Bleibt der Status aus, Servernamen, gespeicherte Zugangsdaten, FEE-Service und das Diagnoseprotokoll prüfen. Erst danach manuell **Disconnect** beziehungsweise erneut **Connect** verwenden.

Project Settings zeigt zusätzlich **Verwendete SDK-Version** und **Installierte FEE-Version**. Die erste Angabe stammt vorrangig aus der tatsächlich geladenen `FS.SDK.dll`. Für die zweite Angabe gilt eine Installation nur dann als vollständig, wenn in genau ihrem Installationspfad `Bin\FS.SDK.dll` vorhanden ist. Höhere, aber unvollständige Versionsordner und Registry-Einträge ohne dieses Merkmal werden ignoriert. Eine rote Abweichung ist ein Diagnosehinweis: Sie verhindert den Start nicht, sollte aber vor FEE-Schreiboperationen mit der freigegebenen Kompatibilitätsmatrix abgeglichen werden.

Für Entwickler kann der Build als letzte Priorität den flachen Repository-Ordner `SDK` verwenden. Diese Auswahl wird korrekt als Assemblyversion angezeigt, zählt aber nicht als lokal installierte FEE-Version. Der Repository-Satz besitzt inzwischen einen vollständigen rekursiven Abhängigkeitsabschluss; eine grüne Buildausgabe ersetzt dennoch keine FEE-Live-Abnahme.

### Remote-FEE-Version ist nicht als Spalte vorhanden

Das ist eine bewusste Zuverlässigkeitsgrenze. Eine direkte Remote-Ermittlung über Registry, WMI/CIM, Admin-Share oder Dateiversion benötigt je nach PC andere Rechte und Dienste. Side-by-Side- oder portable Installationen machen selbst einen erfolgreichen Einzelwert mehrdeutig. Die Anwendung zeigt deshalb keine vermeintlich exakte Remote-Version an. Als kurzfristige betriebliche Angabe kann `KONFIGURATION → SW` verwendet werden. Für einen belastbaren Ist-Wert wird ein zentral verwalteter Inventardienst oder kleiner lokaler Agent empfohlen, der die aktive FEE-Binärdatei ermittelt und authentifiziert bereitstellt.

### RDP-Sitzung steht auf „Nicht abrufbar“

Der PC kann trotzdem online sein. Der aktuell angemeldete Benutzer darf die Remote-Terminalsitzungen nicht abfragen oder `quser` erreicht den Zielcomputer nicht. Mit einem Konto mit ausreichender administrativer Berechtigung starten oder die Remote-Abfrageberechtigung prüfen. Der RDP-Start selbst bleibt davon unabhängig.

Ein lokaler oder Domänen-Administrator funktioniert nur, wenn dieses Konto auch auf dem Ziel-PC autorisiert ist und Remote Desktop Services/RPC durch die Firewall erreichbar sind. Es gibt keinen sicheren Workaround, der diese Rechte umgeht. „Letzte Anmeldung“ bezeichnet die jüngste von `quser` noch gelistete aktive/getrennte Terminalsitzung; bereits abgemeldete Sitzungen sind ohne Zugriff auf das Windows-Sicherheitsereignisprotokoll nicht verlässlich bestimmbar.

### Remote Desktop verwendet einen falschen Benutzer

Die `USER:`-Unteraufgabe der `KONFIGURATION`-Karte hat Vorrang. Der normale Remote-Button erzeugt `TERMSRV/<PC-Name>` mit diesem Benutzer nur für den Start und entfernt den Eintrag nach 20 Sekunden; der zweite Button zeigt den Windows-Anmeldedialog.

### Automatische Remote-Anmeldung ist noch nicht eingerichtet

Der Credential-Manager-Eintrag für das RDP-Passwort fehlt oder ist leer. Unter **Project Settings → Geschützte Zugangsdaten** das Passwort eingeben und speichern. Der Status wechselt auf **Konfiguriert** und der Wert gilt sofort. Der separate Dialog-Button funktioniert weiterhin ohne gespeichertes Passwort.

### Konfigurationswerte lassen sich nicht speichern

Vorhandene Standard-Unteraufgaben werden per PATCH gespeichert; fehlende Standard-Unteraufgaben werden per POST an `/cards/{card}/subtasks` ergänzt. Fehlt die gesamte Karte, kann sie nur über **Standardkarte anlegen** bewusst erzeugt werden.

In **ViCo → PC-/Projektsuche → Arbeitsplatz-Konfiguration** speichert Enter den aktuellen Editorwert zusammen mit allen weiteren geänderten Standardfeldern. Erst nach erfolgreicher Board-Antwort werden Tabellenzeile, Benutzerzuordnung und gemeinsamer Arbeitsplatzbestand aktualisiert. Während eines laufenden Schreibzugriffs wird ein zweiter Enter-Befehl ignoriert; bei einem Fehler bleiben die Änderungen im Editor erhalten und der Fehler steht im Anwendungslog.

### Kanbanize meldet 400 bei `fields`

`subtasks`, Positionsfelder und Custom Fields sind in dieser Instanz keine zulässigen Werte des Kartenparameters `fields`. Die Arbeitsplatzabfrage verzichtet deshalb auf `fields`, paginiert über alle Seiten und verwendet `expand=subtasks`. Für jede gefundene KONFIGURATION-Karte wird zusätzlich der autoritative Endpunkt `/cards/{card_id}/subtasks` gelesen und mit der Expansion zusammengeführt. Dadurch werden auch direkt in der Businessmap-Oberfläche erzeugte oder nur teilweise expandierte Unteraufgaben übernommen.

### Kanbanize-Vorschau zeigt Konflikt

Keinen Synchronisieren-Lauf erzwingen. Prüfen, ob die betroffene Quellkarte eine Deadline hat und nicht mehrere Zielkarten dieselbe Quell-ID oder denselben generierten Titel tragen. Eine separate Vorlagenkarte ist nicht erforderlich; Konflikte führen bewusst zu keiner Änderung.

### TIA Bridge verbindet sich nicht oder Hardware bleibt leer

1. Exakt passende TIA-Version auswählen und das Projekt vollständig öffnen. Openness arbeitet nicht im Versions-Kompatibilitätsmodus.
2. Bei mehreren TIA-Fenstern nur das gewünschte Projekt geöffnet lassen. Die Bridge priorisiert `ProjectPath`, wartet bei großen Projekten bis zu 90 Sekunden auf die Openness-Projektfreigabe und unterstützt sowohl `Projects` als auch eine bereits geöffnete `LocalSessions[n].Project`-Sitzung.
3. Gruppe `Siemens TIA Openness`, TIA-Funktionsrecht **Edit project via Openness API**, Firewallfreigabe und installierte Optionen/HSPs prüfen.
4. Im Diagnosepanel die Bridge-Fehler lesen. Bei `Projects=0` insbesondere PID, Modus, `ProjectPath`, `Projects`- und `LocalSessions`-Diagnose vergleichen. Ein Lesefehler ist nicht dasselbe wie eine tatsächlich leere Collection.
5. Für SpecialDevices2FEE Gerätename/Gerätekopf, Hardware-ID, GSDML, IP, Modultyp, Firmware, E-/A-Adressbereich, Byte-Längen, Präfix und Logik kontrollieren. Die Hardwareabfrage durchläuft intern weiterhin `Project.Devices`, alle `DeviceGroups`/Untergruppen und `UngroupedDevicesGroup`, danach rekursiv `DeviceItems` und deren `Addresses`; die technischen Diagnosepfade werden nur nicht mehr als Bedienerspalten angezeigt. Bei Modulen, die keine Openness-Adresse bereitstellen, fehlende Adressen manuell ergänzen. **Keine Logik** schließt eine Zeile unabhängig vom Übernehmen-Haken aus der Warteschlange aus.
6. Die Bridge läuft als 64-Bit-fähiger .NET-Framework-Prozess. Nach einer Änderung der lokalen Gruppe `Siemens TIA Openness` Windows ab- und wieder anmelden; ein bloßer Neustart des Tools aktualisiert das Windows-Gruppentoken nicht.

Mit **TIA trennen / abbrechen** kann ein laufender Attach beendet werden. Bei einem in Siemens Openness blockierten synchronen Aufruf wird nur der für diese Seite gestartete Bridge-Prozess beendet; der TIA-Portal-Prozess und das geöffnete Projekt bleiben unangetastet. PLC- und Hardwarelisten werden anschließend bewusst geleert.

### Open Interface File meldet `FontMetrics.TryGetGlyphMetrics`

ClosedXML/NPOI wurden mit einer anderen `SixLabors.Fonts.dll` gestartet als beim Build vorgesehen. Die Solution pinnt Version `1.0.1`; der Publish übernimmt keine gleichnamige FEE-Drittanbieter-DLL mehr. Alte Ausgabe- oder Installationsordner vollständig bereinigen und das neue Paket geschlossen neu bauen/installieren. Der automatisierte Test `Tests/ContainerGenerationSmokeTests` muss sowohl `Interface5.xlsx` als auch `Interface7.xlsx` erfolgreich verarbeiten.

Unterstützt werden lokal erkannte PublicAPI-Installationen V15 bis V22. Es wird immer die Assembly der ausgewählten Version geladen; V20 verwendet ausschließlich `Portal V20/PublicAPI/V20/Siemens.Engineering.dll`.

### XamlParseException oder Binding-Fehler

Nicht mit einem erneuten Schreibvorgang fortfahren. Status/Stacktrace sichern, den WPF-UI-Smoke-Test ausführen und die betroffene View/Property in der [Klassenreferenz](KLASSENREFERENZ.md) nachschlagen. Die integrierten Views sind auf schreibgeschützte Anzeige-Bindings abgesichert.

## Performance-Leitplanken

- Tabellen verwenden Zeilen- und Spaltenvirtualisierung.
- PC-Pings sind auf acht, RDP-Sitzungsabfragen auf vier parallele Abfragen begrenzt.
- Such- und Filtereingaben werden entprellt; ein neuer Filter bricht die vorherige Prüfung ab.
- Kanbanize-Caches werden atomar geschrieben; die UI liest stabile Snapshots.
- Dateiübertragungen und FEE-Geräteerzeugungen sind begrenzt/serialisiert.
- Model Validation liest den vollständigen Interfacevariablen-Snapshot einmal pro **Update Objects** und gruppiert ihn lokal nach Interface-GUID; die frühere vollständige SDK-Abfrage pro Interface entfällt.
- Keine rekursiven Netzwerkscans oder TIA-/Outlook-Aufrufe im UI-Thread ergänzen.

## Warnungsstrategie

Der historische WPF-Bestand wurde vor Nullable-Referenztypen entwickelt. Im Hauptprojekt gilt deshalb `Nullable=annotations`; neue Integrationsprojekte bleiben `Nullable=enable`, Core und Infrastructure bauen Warnungen als Fehler. Externe `MSB3277`-Hinweise entstehen durch Versionsunterschiede zwischen geliefertem FEE-SDK und .NET 8 und dürfen nur durch ein abgestimmtes SDK-Upgrade, nicht durch blindes Suppressieren, beseitigt werden.

## Veröffentlichung

1. Release-Build ausführen.
2. Core- und UI-Smoke-Tests ausführen.
3. [ACCEPTANCE_CHECKLIST.md](ACCEPTANCE_CHECKLIST.md) auf einem echten GROB-Desktop abarbeiten.
4. Keine Cachedateien, Rollen-Dateien mit Realbenutzern, API-Schlüssel oder RDP-Credentials einchecken.

Für Inbetriebnehmer ohne FEE-/TIA-/Generierungsfunktionen wird getrennt `VIBN_Tools_IBN.exe` erzeugt. Der genaue schreibgeschützte Funktionsumfang, das Publish-Kommando und die Ziel-PC-Konfiguration stehen in [IBN Remote](IBN_REMOTE.md). Diese Einzeldatei ist nicht mit dem vollständigen Inno-Setup zu verwechseln.

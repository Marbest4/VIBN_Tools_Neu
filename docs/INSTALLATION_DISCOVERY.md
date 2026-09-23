# Lokale Automatisierungsinstallationen

## Zweck

Project Settings zeigt ein read-only Inventar der lokal erkennbaren Automatisierungskomponenten. Die Erkennung verändert weder Windows noch eine Installation und verwendet keine feste Liste unterstützter TIA-Versionen.

## Nachweisquellen

- Direkte Unterordner `Siemens/Automation/Portal V*` unter `Program Files` und `Program Files (x86)`.
- Vorhandene `PublicAPI/<Version>/Siemens.Engineering.dll` als konkreter TIA-Openness-Nachweis.
- Windows-Uninstall-Katalog in 32- und 64-Bit-Registry-Sicht für TIA Portal/STEP 7, Openness, WinCC, weitere Siemens/SIMATIC-Erweiterungen und TwinCAT.
- Übliche TwinCAT-3.1-Installationsordner unter Beckhoff beziehungsweise `C:\TwinCAT`.

Jede Zeile nennt Kategorie, Produkt, Version, Installationspfad und Nachweisquelle. Nicht lesbare Registry- oder Installationsbereiche werden als Diagnose angezeigt und nicht stillschweigend als „nicht installiert“ gewertet.

Die Tabelle verwendet bewusst breite Pfad-, Produkt- und Nachweisspalten; abgeschnittene Werte bleiben vollständig im Tooltip lesbar. Dateisystem- und Registry-Nachweise werden für TIA Portal, Openness, WinCC und TwinCAT über Produktfamilie und normalisierte Version zusammengeführt. Siemens-Erweiterungen behalten zusätzlich ihre normalisierte Produktidentität, damit unterschiedliche Add-ons derselben Version nicht fälschlich zusammenfallen.

Die TIA-Seiten verwenden dieselbe dynamisch ermittelte Versionsliste. Dadurch kann beispielsweise V20 erkannt werden, ohne eine Versionsschleife im Code zu erweitern. Eine gefundene Installation beweist noch nicht, dass der aktuelle Windows-Benutzer Mitglied der Gruppe **Siemens TIA Openness** ist oder dass eine Live-Verbindung erfolgreich sein wird.

## Verifikation

Der UI-Smoke erzeugt ein isoliertes Dateisystem-Fixture mit TIA Portal V20, Openness-DLL und TwinCAT sowie simulierte WinCC-/Siemens-Produkte. Er prüft Klassifizierung, dynamische V20-Auflösung, das Zusammenführen abweichend benannter Registry-/Ordnernachweise und die WPF-Bindings. Die tatsächliche lokale Anzeige bleibt zusätzlich auf dem Zielrechner manuell abzunehmen.

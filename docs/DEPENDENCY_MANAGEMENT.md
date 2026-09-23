# Dependency-Management-Konzept

## Ist-Ursache

Die FS-Assemblies waren direkte DLL-Referenzen mit relativem Pfad zu einer konkreten installierten Versionsnummer. Jede neue fe.screen-sim-Version änderte den Ordner und zwang zu manuellen Projektdateiänderungen. Direkte DLL-Referenzen besitzen außerdem keine transitive Abhängigkeitsauflösung oder Paketmetadaten. `Copy Local` war nicht durchgehend explizit. Grob.UX ist eine private Paketabhängigkeit und deshalb auf einem fremden PC ohne internen Feed nicht wiederherstellbar.

## Variantenvergleich

Skala: 1 = ungünstig, 5 = sehr gut.

| Variante | Wartbarkeit | Skalierung | Aufwand | Stabilität | TIA/FEE-Kompatibilität | Bewertung |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `Directory.Build.props` | 4 | 4 | 2 | 4 | 5 | Sofort sinnvoll für SDK-Pfade/Buildregeln |
| `Directory.Packages.props` | 5 | 5 | 2 | 3 | 5 | Zieloption nach Vereinheitlichung der Entwicklerumgebungen |
| Gemeinsame Shared Library | 4 | 4 | 3 | 4 | 5 | Für eigene Verträge/Adapter geeignet |
| Projekt- statt DLL-Referenzen | 5 | 4 | 3 | 5 | 2 | Nur wenn FS-Quellprojekte verfügbar sind |
| Privates NuGet-Repository | 5 | 5 | 3 | 5 | 5 | Technisch beste Zielarchitektur |
| Lokaler NuGet-Feed | 3 | 2 | 2 | 4 | 5 | Gute Offline-/Pilotlösung |
| Git Submodules | 2 | 3 | 3 | 3 | 4 | Für Binär-SDKs und Versionsauflösung schwach |
| GitHub Packages | 5 | 5 | 3 | 5 | 5 | Guter privater NuGet-Feed bei GitHub-Nutzung |
| CI/CD-Paketbereitstellung | 5 | 5 | 4 | 5 | 5 | Ziel für reproduzierbare Releases |

## Umgesetzter Zwischenstand

- NuGet-Versionen stehen vorerst explizit an den `PackageReference`-Einträgen. Das erhält die Restore-Kompatibilität mit den aktuell eingesetzten Visual-Studio-/NuGet-Versionen.
- `Directory.Build.props` ermittelt `FeeScreenSimRoot` aus Environment, einer eindeutigen vollständigen Installation, `external` und zuletzt dem flachen Repository-Ordner `SDK`.
- `Directory.Build.targets` bricht früh mit einer klaren SDK-Meldung ab und liest die tatsächlich referenzierte Assemblyversion aus `FS.SDK.dll` aus.
- Alle FS-Referenzen verwenden denselben Root und `Private=true`.
- `Build.ps1` erkennt die höchste installierte Version automatisch.
- Neue XML-Definitionen werden per Wildcard automatisch veröffentlicht.
- `SixLabors.Fonts` ist explizit auf `1.0.1` festgelegt, weil ClosedXML und NPOI dieselbe binär kompatible Assembly laden müssen.
- Build und Publish kopieren nicht mehr pauschal den FEE-`Bin`-/Pluginbaum. Der Releasepfad berechnet aus direkten Referenzen und `ReadingUnitPlugin.dll` die rekursiv benötigte `FS.*`-Closure. So gelangen weder unbenutzte FS-Werkzeuge noch Hersteller-Drittanbieterdateien in das Paket und können keine Paketabhängigkeiten überschreiben.
- Container-Generation- und UI-Startup-Smoke-Test laden Container2FEE- beziehungsweise FEE-Discovery-Typen. Da klassische DLL-Referenzen über eine `ProjectReference` nicht vollständig in den Testausgabeordner transitieren, kopieren ausschließlich diese Testprojekte die unveränderten `SDK/FS.*.dll` als Laufzeitfixture. Produkt-Build und SDK-Auswahl bleiben davon unberührt.
- `.vsconfig` und `Prepare-Development.cmd` bilden die minimale Entwicklungsumgebung und die normale Solution-Wiederherstellung reproduzierbar ab.

Der vom Anwender bereitgestellte Ordner `SDK` bleibt in seinem flachen Uploadlayout unverändert. Hauptprojekt und rekursive Publish-Closure sind mit dem vollständigen Satz verifiziert; `FS.SDK.dll` trägt Version `5.0.11.48415`. Der Closure-Test umfasst 16 benötigte `FS.*`-Assemblies. Ein erfolgreicher Build belegt weiterhin nur den technischen Abhängigkeitsabschluss, nicht die Live-Kompatibilität mit einem konkreten FEE-Host.

Das separat übernommene Projekt `Grob Generation Interface` wird nicht durch die Wildcard des SDK-Style-Hauptprojekts kompiliert, sondern als eigenes Solution-Projekt gebaut. Seine gelieferten Dateien bleiben bytegleich; `Directory.Build.props` und `Directory.Build.targets` injizieren Repository-SDK, lokalen Artefaktpfad und die zu `FS.Bridge` passende `System.Text.Json`-Version von außen.

Eine spätere zentrale Paketverwaltung kann nach Vereinheitlichung und Prüfung der Entwicklerumgebungen nach [Microsofts NuGet Central Package Management](https://learn.microsoft.com/en-gb/nuget/consume-packages/central-package-management) erneut eingeführt werden.

## Empfohlene Zielarchitektur

FS.*, ReadingUnitPlugin und Grob.UX werden als interne NuGet-Pakete mit SemVer veröffentlicht, vorzugsweise in GitHub Packages oder einem vorhandenen Unternehmensfeed. Ein Metapaket `Grob.Vibn.FeeSdk` referenziert exakt zueinander passende Assemblyversionen. Das Repository enthält nur PackageReferences; CI restauriert authentifiziert und baut ohne installierte fe.screen-sim-Entwicklungsumgebung.

TIA bleibt bewusst außerhalb dieses Pakets. Die jeweilige `Siemens.Engineering.dll` muss aus der installierten TIA-Version geladen werden und darf wegen Herstellerkopplung nicht als beliebig austauschbares App-Paket behandelt werden.

## Umsetzungsplan

1. Lizenz-/Redistributionsrecht der FS- und Grob-Assemblies klären.
2. Für jede freigegebene SDK-Version ein unveränderliches Paket erzeugen.
3. Paketabhängigkeiten und unterstützte .NET-/FEE-Version in Metadaten festhalten.
4. Private Feed-Authentifizierung in CI und Entwickleranleitung einrichten.
5. DLL-References in einem separaten PR gegen PackageReferences austauschen.
6. Golden-Master-, WPF-Startup- und reale FEE-Abnahme ausführen.
7. Lokalen SDK-Fallback nach einer Übergangsphase entfernen.

# Project Quality Gate

## Ziel und Sicherheitsgrenze

Der Level9-Reiter **Project Quality** bündelt wiederholbare Qualitätsnachweise pro Kundenprojekt. Er ersetzt keine fachliche oder herstellerspezifische Abnahme. Ein grünes Offline-Ergebnis bedeutet daher nur, dass die konfigurierten, tatsächlich ausgeführten Prüfungen erfolgreich waren. TIA gilt erst nach einem echten Compile der ausgewählten PLC als nachgewiesen. Für Emulate3D und EKS prüft diese Version ohne freigegebenes Hersteller-SDK ausschließlich Installation und Projektpfad; dies wird als Warnung und niemals als bestandener Live-Test dargestellt.

## Projektprofile

Profile werden atomar unter `%LOCALAPPDATA%\VIBN_Tools\quality\project-profiles.json` gespeichert. Sie enthalten Kunde, Projektwurzel, Requirements-/ContainerFile, TIA-Version, ViCo-Bibliothek, Rockwell-Standard, erlaubte Containertypen, Namensregeln, Signaladressbereiche und die aktivierten Simulationsadapter. Produktive Zugangsdaten gehören nicht in das Profil. Namensregeln werden als `Container=<Regex>; Signal=<Regex>` und Adressbereiche beispielsweise als `E0-E127, A0-A127` eingegeben; das Quality Gate wendet diese Regeln tatsächlich auf das ContainerFile an.

In der Oberfläche ist **Name** rot als einziges technisches Pflichtfeld markiert. Projektwurzel, Requirements.xml und Container.xml sind orange: Das Profil kann ohne sie gespeichert werden, das Gate überspringt dann aber wesentliche Datei-, Struktur- und Szenarioprüfungen und meldet dies. Blaue Felder sind optional beziehungsweise nur für die zugehörige Plattform oder Zusatzregel erforderlich. Die Eingabefelder sind in der Breite begrenzt; bei kleineren Fenstern stehen horizontale und vertikale Scrollleisten zur Verfügung.

## Quality-Gate-Ablauf

1. Profil auswählen, Pfade konfigurieren und speichern.
2. **Quality Gate ausführen** prüft Profilpfade, sichere XML-Lesbarkeit, erzeugt neutrale Testszenarien, prüft konfigurierte Adapter und führt vorhandene Nachweise zusammen.
3. **Bericht exportieren** schreibt JSON und HTML nach `<Projektwurzel>\QualityReports`; fehlt die Projektwurzel, wird der Dokumente-Ordner verwendet.
4. Fehler und Warnungen bleiben getrennt. Ein nicht live geprüfter externer Adapter bleibt eine Warnung.
5. Nachweise werden bei jeder Ausführung neu aus dem gemeinsamen Evidence-Store geladen. Bereits geöffnete Quality-Seiten reagieren außerdem auf neue TIA-Compile-, Signalregister- oder Generierungsnachweise. Ein Nachweis älter als 24 Stunden wird als `EVIDENCE_STALE` gewarnt und muss im zuständigen Reiter erneut erzeugt werden.

**Passed** belegt nur die tatsächlich ausgeführten strukturellen Prüfungen und frischen Nachweise. **Warning** kennzeichnet optionale Lücken, einen nicht live verifizierten Adapter, einen veralteten Nachweis oder notwendige Fachfreigabe. **Failed** bezeichnet einen reproduzierbaren Datei-, XML-, Regel-, Identitäts- oder Compilefehler. Keine dieser Stufen simuliert ohne Runtime-Adapter eine HMI-Bedienung oder physische Zylinderrückmeldung.

## Signalidentitätsregister

Das Register verwendet bevorzugt Signal-ID, danach FEE-Variablen-GUID, Interface-GUID plus Name und zuletzt Name plus Adresse. Namens- und Adressänderungen behalten eine stabile interne Identität und eine Historie. Abweichende Datentypen, widersprüchliche feste IDs, mehrere Kandidaten oder doppelte FEE-GUIDs sind harte Konflikte. **Nur vergleichen** verändert keine Datei. **Konfliktfrei übernehmen** schreibt nur, wenn kein Konflikt vorliegt; ein Teilergebnis wird nicht persistiert.

## Automatisch erzeugte Testszenarien

Aus jedem Container werden neutrale Arrange-/Act-/Assert-Szenarien erstellt. Zylinder, Lifte, Türen, Stopper, Sensoren/Schalter und Motion-/Achstypen besitzen eigene Grundmuster; unbekannte Typen erhalten einen Struktur- und Signalreaktionstest. Die Szenarien erfinden keine projektspezifischen Zeiten, Positionen oder Sollwerte. `RequiresDomainReview` kennzeichnet die notwendige fachliche Freigabe vor einer Live-Ausführung.

## TIA-Compile-Nachweis

**TIA Portal → TIA Quality Gate → Ausgewählte PLC kompilieren** ruft den Siemens-Compiler über die isolierte TIA-Bridge auf, liest den rekursiven Meldungsbaum, Fehler-/Warnungszahlen und Laufzeit zurück und hinterlegt den Nachweis. Der Vorgang ruft `Project.Save()` nicht auf. Safety-/Know-how-geschützte Inhalte können weiterhin eine Anmeldung in TIA erfordern.

## Generierungsmanifest

Jeder Container2FEE-Visual-Lauf speichert Vorher-/Nachher-Zustand, externe Zuordnung, Aktion, Laufzeitfehler und Quellfingerabdruck unter `%LOCALAPPDATA%\VIBN_Tools\quality\generation-manifests`. **Letzten Lauf reparieren** verwendet nur das neueste Manifest mit identischem Quellfingerabdruck und selektiert ausschließlich fehlgeschlagene oder offene Container. Das ist eine gezielte Wiederaufnahme, kein transaktionaler Rollback bereits ausgeführter FEE-SDK-Aufrufe.

## Adaptermodell

Alle Plattformadapter liefern dieselbe konservative Capability-/Probe-Struktur. Der FEE-Adapter bestätigt nur die gemeinsame, tatsächlich verbundene SDK-Sitzung. Die Emulate3D-/EKS-Adapter melden in dieser Version Dateisystembereitschaft und dokumentierte Fähigkeiten; `IsLiveVerified` bleibt bewusst `false`. Ein späterer Herstelleradapter kann dieselbe Grenze um echte Modellabfrage und Szenarioausführung erweitern, ohne die Quality-Gate-Logik neu zu schreiben.

## Automatische Verifikation

`Tests/CoreSmokeTests` deckt Profil-Roundtrip, XML-Signalextraktion, stabile Identität und Historie, Konfliktsperre, Szenariogenerierung, konservative Adaptermeldung, Berichtsexport, Manifestklassifikation/-laden und den typisierten TIA-Compile-Pipe-Vertrag ab. `Tests/UiStartupSmokeTests` lädt die neue WPF-Seite und prüft alle Bindings. Eine reale Herstellerabnahme bleibt Bestandteil der Release-Checkliste.

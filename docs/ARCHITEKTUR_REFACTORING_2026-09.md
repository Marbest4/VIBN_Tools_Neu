# Architektur-Refactoring 2026-09

## Ziel und Leitplanken

Dieses Refactoring verändert keine fachlichen Abläufe. Es zieht vorhandenen Code nur in überprüfbare Projektgrenzen und ersetzt identische technische Hilfen durch gemeinsame Implementierungen. Container-Golden-Master, Core-Smoke-Test und WPF-Starttest bleiben die Verhaltensreferenz.

## Ergebnis der Strukturanalyse

Ein eigenes Projekt pro sichtbarem Reiter ist nicht generell sinnvoll. Ein Reiter ist eine Navigations- und keine automatisch eine fachliche Grenze. Kleine Reiter, die dieselben FEE-Services und globalen SDK-Objekte verwenden, würden bei einer mechanischen Aufteilung entweder zyklische Referenzen oder viele künstliche Fassaden erzeugen. Sinnvolle Projektgrenzen erfüllen stattdessen drei Kriterien:

1. ein zusammenhängender fachlicher Zweck;
2. eine eindeutige Abhängigkeitsrichtung;
3. eigenständig baubare und testbare Abhängigkeiten.

| Bereich | Entscheidung | Begründung |
| --- | --- | --- |
| ContainerGeneration | eigenes Projekt `VIBN_Tools.ContainerGeneration` | zusammenhängende Import-, Requirements-, Matching-, Reimport-, XML- und AI-Funktion; eigene ClosedXML-/ML-/Schema-Abhängigkeiten |
| gemeinsame WPF-Helfer | eigenes Projekt `VIBN_Tools.SharedWpf` | von Hauptanwendung und IBN-Remote wiederverwendete PasswordBox-Bindung, Observable-Basis und Commands |
| ViCo/Kanbanize | bestehende Trennung Core/Infrastructure/Application beibehalten | Fachregeln und Adapter sind bereits sauber getrennt |
| TIA | Contracts, Client und separaten Bridgeprozess beibehalten | unterschiedliche Laufzeitgrenzen und kontrollierbarer Fehler-/Abbruchbereich |
| Container2FEE, ModelValidation, SpecialDevices | vorerst im Hauptprojekt | gemeinsame FEE-SDK-Objekte, `Services` und Containerklassen erzeugen noch eine starke Rückkopplung; ein Projekt pro Reiter würde diese Kopplung nur verdecken |

## Neue Projektgrenzen

```text
VIBN_Tools.exe
  ├─ VIBN_Tools.Core
  ├─ VIBN_Tools.Infrastructure
  ├─ VIBN_Tools.ContainerGeneration
  │    └─ VIBN_Tools.SharedWpf
  ├─ VIBN_Tools.SharedWpf
  └─ VIBN_Tools.Tia.Client
       └─ VIBN_Tools.Tia.Contracts

VIBN_Tools.TiaBridge.exe (net48)
  └─ VIBN_Tools.Tia.Contracts
```

Das Hauptprojekt kompiliert `ContainerGeneration/**` und `SharedWpf/**` nicht mehr über seine rekursive SDK-Wildcard. Beide Ordner besitzen eigene Projektdateien und werden ausschließlich per `ProjectReference` eingebunden. Dadurch werden versehentliche Doppelkompilierung und versteckte Paketabhängigkeiten verhindert. Die XSD-Ressourcen liegen jetzt beim Feature, das sie verwendet. Ihr Zugriff erfolgt über die definierende Assembly; die in Containerdateien ausgegebene Produktversion bleibt die Version der gestarteten Anwendung.

## Wiederverwendete technische Bausteine

- `AsyncRelayCommand`, `RelayCommand` und `RelayCommand<T>` ersetzen lokale, nahezu identische Command-Klassen in Hauptanwendung und IBN-Remote.
- `ObservableCollectionExtensions.ReplaceWith` ersetzt sechs Kopien von `Clear` plus `Add` und bleibt als plattformneutraler Helper in Core.
- `NotifyBase` liegt nicht mehr in der FEE- und WPF-Sammeldatei `MvvmBase.cs`, sondern in der gemeinsamen Presentation-Bibliothek.
- `ExportFileNamePolicy` stellt für FEE2Container und FEE2SpecialDevices dieselbe Dateinamensregel bereit.

## Warum die TIA-EXE bestehen bleibt

Die Integration der Bridge in `VIBN_Tools.exe` ist technisch nicht sinnvoll, solange die Hauptanwendung `net8.0-windows` und die freigegebene Siemens-Openness-Grenze `net48` verwendet. Eine direkte Referenz würde die Laufzeiten und Siemens-Assemblyauflösung im Hauptprozess koppeln. Wichtiger ist die Fehlerisolation: Siemens-Aufrufe sind synchron und können blockieren. Der Client kann dann ausschließlich den von ihm gestarteten Bridgeprozess beenden; FEE, ViCo und die übrige WPF-Anwendung laufen weiter. Bei einer In-Process-Integration gäbe es für denselben Fall keinen sicheren Abbruch ohne Beenden der gesamten Anwendung.

Die separate EXE ist für Anwender kein zweites manuell zu startendes Programm. `NamedPipeTiaBridgeClient` startet und beendet sie automatisch. Eine spätere Zusammenlegung wäre erst nach offizieller Siemens-Freigabe für dieselbe .NET-Laufzeit und nach einem belastbaren Ersatz für diese Prozessisolation zu bewerten.

## Bewusst nicht mechanisch geändert

Große Legacy-ViewModels wie `ContainerGenerationPageVM` enthalten weiterhin zu viele Bedienaufgaben. Ein bloßes Aufteilen in Partial-Dateien würde nur Dateigröße, nicht Kopplung reduzieren. Der nächste sichere Schritt ist, Import, Reimport, Undo/Redo und Export jeweils hinter kleine Workflow-Services zu stellen und dafür zuerst charakterisierende Tests auf ViewModel-Ebene einzuführen. Gleiches gilt für die FEE-gebundenen Bereiche: Zuerst müssen `Services` und konkrete SDK-Typen hinter Ports gekapselt werden, erst danach sind weitere Projektgrenzen ohne Zyklen sinnvoll.

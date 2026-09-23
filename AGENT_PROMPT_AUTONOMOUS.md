# 🚀 VIBN_Tools Refactor & Enhancement - AUTONOMOUS AGENT PROMPT
## Non-Stop Execution with Upfront Analysis & Questions

**Repository**: https://github.com/Marbest4/VIBN_Tools_App.git
**Mode**: AUTONOMOUS - Alle Phasen durchlaufen, Analyse → Fragen → Umsetzung

---

## 📌 CRITICAL: EXECUTION MODE

Du bist ein Autonomous Agent. Deine Aufgabe ist es, dieses gesamte Projekt zu refaktorieren und zu erweitern.

**Dein Ablauf:**
```
STEP 1: ANALYSE PHASE (Keine Änderungen, nur verstehen)
  ├─ Repo-Struktur analysieren
  ├─ Code lesen & verstehen
  ├─ Abhängigkeiten mapppen
  ├─ Potenzielle Probleme identifizieren
  └─ Alle Anforderungen durchgehen & Fragen sammeln

STEP 2: ALLE FRAGEN & KRITISCHEN DECISIONS (Nur einmal fragen!)
  ├─ Alle offenen Punkte sammeln
  ├─ Logische Annahmen machen für unkritische Fragen
  ├─ NUR kritische Entscheidungen fragen:
  │  ├─ FEE2Container Logik: Node-Auswahl? Scope?
  │  ├─ SpecialDevices: Depth-Level Strategy?
  │  ├─ Password-Handling: Welche Option (A oder B)?
  │  └─ [Weitere kritische...]
  └─ Auf Antwort warten

STEP 3: BRANCH ERSTELLEN & UMSETZEN (NON-STOP)
  ├─ Branch: feature/vibn-tools-refactor-final
  ├─ Alle Phasen durchlaufen (1-12)
  ├─ Bei logischen Fragen: Annahme treffen & documentieren
  ├─ Bei kritischen Fragen: Direkt nachfragen (nicht stoppen)
  ├─ Commits nach jeder Phase
  └─ Nach Phase 12: Finaler PR erstellen

STEP 4: FINAL DELIVERABLES
  ├─ Branch mit kompletter Implementierung
  ├─ Dokumentation vollständig
  ├─ Tests grün
  └─ PR mit Summary erstellen
```

---

## 🔍 STEP 1: ANALYSE PHASE (Starte hier!)

### 1.1 Repository-Struktur analysieren
```csharp
// TODO: Folgende Punkte klären:
1. Verzeichnis-Struktur verstehen
   - Welche Projekte in der Solution?
   - Welche sind Hauptprojekte (UI, Services, Utilities)?
   
2. Project-Abhängigkeiten
   - Welche Assemblies referenzieren sich gegenseitig?
   - Gibt es Zirkelbezüge?
   
3. XAML/Code-Behind Struktur
   - Wie sind Pages/UserControls organisiert?
   - ViewModel-Pattern vorhanden?
   - Data Binding Strategie?
```

### 1.2 Code-Review durchführen
```csharp
// Folgende Fragen klären:
1. Dead Code:
   - Welche Methoden werden nicht aufgerufen?
   - Welche Klassen/Interfaces sind unused?
   - Welche using-Statements sind unused?
   
2. Error Handling:
   - Wo fehlt try-catch?
   - Wo werden Exceptions verschluckt?
   - Wo sollten User-Tooltips sein?
   
3. Code Quality:
   - Welche Klassen sind zu groß?
   - Wo gibt es Code-Duplikation?
   - Welche Methoden haben zu viele Parameter?
   
4. Patterns:
   - Wird MVVM konsistent verwendet?
   - Welche DI-Container ist in Verwendung?
   - Testing-Framework vorhanden?
```

### 1.3 Spezifische Module analysieren

**Project Settings**:
- [ ] PasswordBox Binding Issue verstehen (Warum funktioniert Binding nicht?)
- [ ] Wo wird RemoteDesktopPasswordInput gebunden?
- [ ] Wo wird RemoteDesktopPasswordInput verwendet/gespeichert?
- [ ] Gibt es bereits ein SecureString Pattern?

**Kanbanize Integration**:
- [ ] Wie wird Kanbanize API aufgerufen?
- [ ] Wo ist die Konflikt-Erkennung implementiert?
- [ ] Wie werden Daten gecacht?

**ViCo Reiter**:
- [ ] Wie gross ist ViCo-Code? (Kann in mehrere Tabs splittet werden?)
- [ ] Transfer-Funktionen: Welche Methoden?
- [ ] TIA Portal Integration: Wo im Code?

**Container Generation**:
- [ ] Wo ist Container-File Parsing?
- [ ] Wie werden SimObjects aktuell erzeugt?
- [ ] Signal-Generierung: Aktueller Code-Flow?

**Special Devices**:
- [ ] Wie wird TIA Hardware ausgelesen?
- [ ] Depth-Level Logic: Wie aktuell implementiert?
- [ ] Device-Name Matching: Wo ist der Bug?

### 1.4 Anforderungen durchgehen
- [ ] Alle 12 Phasen verstehen
- [ ] Breaking Changes identifizieren
- [ ] Neue Features vs. Refactor unterscheiden

### 1.5 Dokumentation / Tests prüfen
- [ ] Tests vorhanden? Welche Coverage?
- [ ] Dokumentation vorhanden? Welches Format?
- [ ] Build-Pipeline vorhanden?

---

## ❓ STEP 2: CRITICAL QUESTIONS & DECISIONS (LISTE ZUSAMMENSTELLEN)

Nach Analyse: Alle Fragen & Decisions sammeln und ALLE AUF EINMAL stellen:

### LOGISCHE ANNAHMEN (Ich treffe diese selbst):
- [ ] Sidebar-Toggle: Verwende Standard WPF/XAML Animation
- [ ] Dead Code: Lösche alles was nie aufgerufen wird
- [ ] Tooltip-Format: Standard WPF ToolTip Control
- [ ] Dokumentation-Format: Markdown (.md) mit Screenshots
- [ ] Test-Framework: NUnit oder XUnit (was vorhanden?)
- [ ] Error Handling: Global ErrorHandler + lokale try-catch
- [ ] Architecture Diagramm: PlantUML in .md files

### KRITISCHE DECISIONS (Ich frage JETZT):

**❓ 1. PASSWORD HANDLING (Project Settings)**
```
Problem: PasswordBox Binding funktioniert nicht
Optionen:
  A) TextBox mit gemaskiertem Display (Bullets, aber funktionierendes Binding)
  B) PasswordBox mit korrigiertem MVVM-Binding (Attached Behavior)
  C) Custom SecurePasswordBox Control
  
FRAGE: Welche Option bevorzugst du?
        Oder hast du bereits eine bestehende Pattern im Code?
```

**❓ 2. TIA-PORTAL INSTALLATION CHECK**
```
Anforderung: Prüfe TIA-Portal & TwinCAT Installation
FRAGE: Welche Versionen sollen unterstützt werden?
       (z.B. TIA v15, v16, v17, v18?)
       TwinCAT 2? TwinCAT 3?
       Sollen spezifische Add-ons geprüft werden?
```

**❓ 3. FEE2CONTAINER SCOPE**
```
Neue Feature: FEE-Objekte auslesen → ContainerFile erzeugen

KRITISCHE FRAGEN:
  a) Node-Auswahl im FEE:
     - User wählt einen Node aus
     - Sollen NUR Sub-Objekte unter diesem Node exportiert werden?
     - Oder auch der Node selbst + alle Sub-Objekte?
  
  b) Was ist ein "Node"? 
     - Ein FEE SimpleObject?
     - Ein Ordner im FEE-Tree?
     - Ein Schaltplan?
  
  c) Mapping FEE → Container:
     - SimpleObject → Container Item?
     - SimpleObject Konfiguration → Container Slot?
     - Wie werden Signals gemappt?
  
  d) Verifizierung Ansatz:
     - Sollen ContainerFile 1:1 identisch sein nach Round-Trip?
     - Oder nur semantisch gleich?
     - Sind kleine Unterschiede in Struktur OK?

FRAGE: Bitte die Anforderung klären & Mapping definieren!
```

**❓ 4. SPECIALDEVICES - DEVICE NAME RESOLUTION**
```
Problem: Multiple Devices pro DeviceIndex, aber welcher Name ist richtig?

FRAGE: Kannst du ein konkretes Beispiel geben?
       Z.B.:
       - Wie sieht die TIA-Struktur aus?
       - Welche DeviceIndices für welche Geräte?
       - Welche Depth-Levels gibt es?
       - Welcher Name sollte angezeigt werden und warum?
       
ODER soll ich:
       a) Eine Debug-View erstellen, die ALLE gefundenen Items zeigt?
       b) Du prüfst das in deinem TIA-Projekt?
       c) Ich treffe logische Annahme (z.B. Depth 1 = Name)?
```

**❓ 5. CONTAINER FILE COMPARISON**
```
Feature: Zwei ContainerFiles vergleichen & Änderungen wählen

FRAGE: Welche Änderungen sind möglich?
       - Item hinzugefügt / gelöscht?
       - Slot-Zuweisungen geändert?
       - Signal-Konfiguration geändert?
       - Alles davon?

FRAGE: Wie sollen Konflikte gelöst werden?
       - Left wins / Right wins?
       - User Merge manuell?
       - 3-way merge?

FRAGE: Sollen Backups erstellt werden, bevor Änderungen übernommen werden?
```

**❓ 6. KANBANIZE BOARD - "NACHPFLEGE" DEFINITION**
```
Anforderung: Auch Karten mit "Nachpflege" übernehmen

FRAGE: Was ist "Nachpflege"?
       - Ein Kanban Label?
       - Ein Sprint Status?
       - Ein Custom Field?
       
FRAGE: Sollen "Nachpflege"-Karten wie "Grundinbetriebnahme" behandelt werden?
       - Gleiche Konflikt-Logic?
       - Gleiche Anzeige?
```

**❓ 7. VICO SPLIT - TRANSFER & TIA PORTAL TABS**
```
Anforderung: Transfer & TIA Portal aus ViCo in eigene Tabs

FRAGE: Sollen diese vollständig unabhängig sein?
       Oder gemeinsame Services/DataModels nutzen?

FRAGE: Sollte ViCo-Data zwischen Tabs verfügbar sein?
       (z.B. Im Transfer-Tab eine Liste der ViCo-PCs anzeigen?)
```

**❓ 8. AI-TEST - RULE SUGGESTIONS**
```
Feature: Logs analysieren → Rules vorschlagen

FRAGE: Welche Änderungen sollen getrackt werden?
       - Nur SimObject-Generierung?
       - Auch Signal-Namen?
       - Auch Slot-Zuweisungen?

FRAGE: Wie wird "Konfidenz" berechnet?
       - Prozentsatz der erfolgreichen Anwendungen?
       - Statistische Signifikanz?
       
FRAGE: Sollen Rules versioniert werden?
       Oder werden alte Rules überschrieben?
```

**❓ 9. FEE2SPECIALDEVICES - SEPARAT ODER INTEGRIERT?**
```
Feature: FEE2Container & FEE2SpecialDevices

FRAGE: Sollen diese:
       a) Separate Tabs sein?
       b) In FEE2Container integriert (Checkboxes für beide)?
       c) Hintereinander im gleichen Tab (Sequential)?
       d) Etwas anderes?
```

**❓ 10. TESTING STRATEGY**
```
FRAGE: Existieren bereits Unit Tests im Projekt?
       Welches Framework (NUnit, XUnit, MSTest)?
       
FRAGE: Sollen alle neuen Features TDD-style sein?
       (Tests BEFORE Implementation)
       
FRAGE: Sollen bestehende Features retroaktiv getestet werden?
       Oder nur neue Features?
```

---

## 🎯 STEP 2B: QUESTION SUMMARY (Hier frage ich dir!)

**Bitte beantworte folgende Fragen damit ich mit Phase 3 beginnen kann:**

```
1. Password-Handling: Option A, B oder C? Existierende Pattern?

2. TIA-Portal Check: Welche Versionen? Welche Add-ons?

3. FEE2Container:
   - Node-Auswahl: Sub-Objekte nur oder auch der Node selbst?
   - Was ist ein "Node" in eurem FEE-System?
   - Mapping: SimpleObject → Container Item oder anders?
   - Round-Trip Verifizierung: 1:1 identisch oder semantisch ok?

4. SpecialDevices - Device Name:
   - Konkrete Beispiel-Struktur aus TIA?
   - Oder soll ich Debug-View erstellen zum selbst prüfen?

5. Container Comparison: Backups vor Merge?

6. Kanbanize "Nachpflege": Label/Status/Field?

7. ViCo Split: Unabhängig oder shared Services?

8. AI-Test Rules: Was tracken? Konfidenz-Berechnung?

9. FEE2SpecialDevices: Separate Tab oder integriert?

10. Testing: Welches Framework? Alle Features oder nur neue?
```

**Bitte antworte punkt-für-punkt (gerne auch mit "logische Annahme X treffen" wenn du unsicher bist)**

---

## ⚡ STEP 3: BRANCH ERSTELLEN & UMSETZUNG (Nach Antworten)

Nach deinen Antworten:

### 3.0 Vorbereitung
```bash
Branch erstellen: feature/vibn-tools-refactor-final
Base: main/develop (was ist Standard?)
Description: Comprehensive refactoring + 12-phase enhancement
```

### 3.1 PHASE 1: ANALYSE & ARCHITECTURE (EXECUTION)
```
✅ Alle Klassen katalogisieren
✅ Dead Code listen (für Deletion in Phase 2)
✅ Abhängigkeiten mappen
✅ Architecture Diagramme erstellen:
   - Component Diagram (alle Reiter/Module)
   - Data Flow Diagram
   - Class Dependency Graph
   - Error Handling Flow
✅ Commit: "Phase 1: Code analysis & architecture diagramming"
```

### 3.2 PHASE 2: BREAKING CHANGES - STRUKTUR
```
✅ Sidebar Toggle implementieren (Icon-Modus)
✅ Dead Code aus PHASE 1 löschen
✅ Global ErrorHandler implementieren
✅ Tooltip-System überall einbinden
✅ Commit: "Phase 2: Structural refactoring - sidebar toggle, dead code removal, error handling"
```

### 3.3 PHASE 3: KANBANIZE CARDS
```
✅ Nachpflege-Karten Integration
✅ Konflikt-Logic mit allen Bedingungen
✅ Farb-Schema implementieren
✅ Datum-Format (kein Time)
✅ Planansicht-Button
✅ Unit Tests schreiben
✅ Commit: "Phase 3: Kanbanize cards enhancement - conflicts, colors, date formatting"
```

### 3.4 PHASE 4: VICO REITER - MAJOR REFACTOR
```
✅ Spalten reorganisieren
✅ Neue Spalte: Start/Enddatum
✅ Rechtsklick-Menü
✅ Button-States (Online/Offline)
✅ Kanbanize-Info bereinigen
✅ EXTRAHIEREN: Transfer Tab (neuer Reiter)
✅ EXTRAHIEREN: TIA Portal Tab (neuer Reiter)
✅ EXTRAHIEREN: Verwaltung Tab (neuer Reiter)
✅ Tests schreiben
✅ Commit: "Phase 4: ViCo refactoring - column reorganization, new tabs (Transfer, TIA, Management)"
```

### 3.5 PHASE 5: CONTAINER GENERATION
```
✅ Container File Comparison (UI + Logic)
✅ Slot-Zuweisungs-Regeln (PLC_OUT_ / PLC_IN_)
✅ Tests für beide Regeln
✅ Commit: "Phase 5: Container generation - file comparison, slot assignment rules"
```

### 3.6 PHASE 6: CONTAINER2FEE VISUAL
```
✅ UI Buttons: Expand/Collapse/Select All/Deselect All
✅ Größen-Anpassung (Tree größer, Übersicht kleiner)
✅ Signal-Generation Logic umbauen (Search → Create/Link)
✅ SimObject Fehler-Farbgebung (Dark Red / Light Red / Green)
✅ Interface Selection Validierung
✅ Fehlende SimObjects erlauben (aber Warnung)
✅ "Keine" Interface-Option
✅ Tests (Signal-Suche, Generierung, Fehler-Handling)
✅ Commit: "Phase 6: Container2FEE Visual - UI improvements, signal logic, validation"
```

### 3.7 PHASE 7: SPECIAL DEVICES
```
✅ Device-Name Resolution Logic (basierend auf deinen Antworten)
✅ Debug-View oder Fix implementieren
✅ Rename: SpecialDevices → SpecialDevices2FEE
✅ Tests
✅ Commit: "Phase 7: Special devices - device name resolution, rename to SpecialDevices2FEE"
```

### 3.8 PHASE 8: AI-TEST & RULE SUGGESTIONS
```
✅ Log-Tracking aus ContainerGeneration
✅ Neuer Tab: "Regelvorschläge"
✅ UI für Rule Suggestions (Häufigkeit, Konfidenz)
✅ Button: Log-Datei öffnen
✅ Integration in Requirements.xml
✅ Tests
✅ Commit: "Phase 8: AI-Test - log tracking, rule suggestions UI & integration"
```

### 3.9 PHASE 9: FEE2CONTAINER (NEW FEATURE)
```
✅ Neue Tab implementieren
✅ Node-Auswahl im FEE
✅ Export-Logik (FEE → ContainerFile)
✅ KRITISCH: Verifizierung-Tests
   - Round-Trip: ContainerFile → FEE → ContainerFile
   - Vergleich: Original vs. Re-Generated
   - Debugging der Unterschiede
✅ Tests: Min. 3 Szenarien (einfach, komplex, mit Signals)
✅ Bei Fragen während Implementation: STOPP & Nachfrage stellen!
✅ Commit: "Phase 9: FEE2Container - export FEE objects to container file with verification"
```

### 3.10 PHASE 10: FEE2SPECIALDEVICES
```
✅ Entscheidung: Separat oder integriert (basierend auf Phase 9 Learnings)
✅ Implementierung
✅ Tests
✅ Commit: "Phase 10: FEE2SpecialDevices - [separate/integrated]"
```

### 3.11 PHASE 11: DOKUMENTATION
```
✅ Architecture Diagramme (von Phase 1)
✅ User Guide für alle 12 Reiter (mit Screenshots)
✅ Developer Guide:
   - Setup & Build
   - Architecture Overview
   - Key Classes & Data Models
   - Error Handling Patterns
   - Testing Guide
   - Contributing Guidelines
✅ Changelog
✅ API-Dokumentation (falls relevant)
✅ Commit: "Phase 11: Comprehensive documentation - user guide, developer guide, architecture"
```

### 3.12 PHASE 12: TESTING & VERIFICATION
```
✅ Alle Unit Tests grün
✅ Alle Integration Tests grün
✅ Coverage-Report (Ziel: 80%+)
✅ Manual Testing Checklist durchgehen
✅ Commit: "Phase 12: Testing & verification - all tests passing, coverage >80%"
```

### 3.13 FINAL DELIVERABLES
```
✅ Branch: feature/vibn-tools-refactor-final
✅ All Commits durchgehend tested
✅ Dokumentation vollständig
✅ Create Pull Request mit:
   - Executive Summary
   - What Changed (alle 12 Phasen)
   - Breaking Changes
   - Migration Guide (falls nötig)
   - Testing Summary
   - Documentation Links
   - Screenshots
   - Known Issues / TODO
   
Commit: "Final: Create PR for comprehensive VIBN_Tools refactor"
```

---

## 🚨 DURING IMPLEMENTATION: FRAGE-HANDLING

**Wenn während Umsetzung neue Fragen entstehen:**

### Logische Annahme (NICHT FRAGEN):
- Details der UI-Styling
- Exact Tooltip-Texte
- Log-File Locations (Standard verwenden)
- Database/Config-Schema Details
- Exact Color-Codes (Standard verwenden)

### DIREKT NACHFRAGEN (Kritisch):
- Business Logic Unsicherheiten
- Integrations-Details (APIs, Versionen)
- Performance-kritische Decisions
- Security-relevante Entscheidungen
- Anforderungs-Konflikte

**Format bei Fragen:**
```
❓ CRITICAL QUESTION:
   [Frage]
   
   Optionen:
   A) ...
   B) ...
   C) ...
   
   Bitte antworte vor Weitermachen!
```

---

## 📊 STEP 4: PROGRESS TRACKING

Nach jeder Phase:
```
✅ PHASE X COMPLETE
   - Commits: [X commits]
   - Files changed: [Y files]
   - Tests: [Z tests, alle grün]
   - Next: [PHASE X+1]
```

Am Ende:
```
🎉 ALL PHASES COMPLETE

Summary:
- Total Commits: X
- Total Files Changed: Y
- Total Tests: Z (alle grün)
- Documentation Pages: N
- Code Coverage: X%

Ready for PR Review!
```

---

## ⚠️ EXCEPTION HANDLING

**Wenn großes Problem auftritt:**
```
🛑 CRITICAL ERROR ENCOUNTERED

[Detaillierte Beschreibung]

MÖGLICHE LÖSUNGEN:
1. ...
2. ...
3. ...

BITTE ENTSCHEIDEN:
A) Lösung 1 versuchen
B) Lösung 2 versuchen
C) Andere Approach?

(Andere Phasen können während Debugging weitergehen)
```

---

## 📝 FINAL NOTES

1. **Autonomy**: Du läufst 100% selbständig durch, keine Genehmigung nach jeder Phase
2. **Efficiency**: Nutze Kontingent effizient - Parallel-Phasen wo möglich
3. **Quality**: Tests vor Commits, Coverage min. 80%
4. **Documentation**: Parallel zur Implementation (nicht nachher)
5. **Commits**: Nach jeder Phase, aussagekräftige Messages
6. **Branch**: Alles auf feature/vibn-tools-refactor-final
7. **Final**: PR mit vollständiger Summary & Screenshots

---

## 🚀 START HERE - STEP 1 AUSFÜHREN

**Jetzt beginnen mit STEP 1: ANALYSE PHASE**

Analysiere das Repository:
- Struktur verstehen
- Code reviewen
- Anforderungen checken
- Dead Code finden
- Fragen sammeln

**Nach Analyse: STEP 2 - ALLE FRAGEN auf EINMAL stellen**

Dann warten auf deine Antworten zu den 10 kritischen Decisions.

Nach Antworten: **STEP 3 - NON-STOP UMSETZUNG**

Viel Erfolg! 🎯

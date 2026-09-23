# Architektur- und Datenflussdiagramme

## Komponenten

```mermaid
flowchart LR
    UI[WPF Views] --> VM[Application ViewModels]
    VM --> CORE[VIBN_Tools.Core]
    VM --> INFRA[VIBN_Tools.Infrastructure]
    VM --> FEE[FEE / FS SDK]
    VM --> TC[VIBN_Tools.Tia.Client]
    INFRA --> CORE
    INFRA --> KB[Businessmap/Kanbanize API]
    INFRA --> WIN[Windows: RDP, Dateisystem, Sitzungen]
    TC -->|Named Pipe / JSON| TB[VIBN_Tools.TiaBridge net48]
    TB --> TIA[Siemens.Engineering / TIA Portal]
    IBN[IBN Remote WPF] --> CORE
    IBN --> IBNINFRA[IBN Read/RDP Infrastructure]
    IBNINFRA --> KB
    IBNINFRA --> WIN
```

## Projektabhängigkeiten

```mermaid
flowchart TD
    APP[VIBN_Tools net8.0-windows] --> CORE[VIBN_Tools.Core net8.0]
    APP --> INFRA[VIBN_Tools.Infrastructure net8.0]
    APP --> CLIENT[VIBN_Tools.Tia.Client net8.0]
    INFRA --> CORE
    CLIENT --> CONTRACTS[VIBN_Tools.Tia.Contracts netstandard2.0]
    BRIDGE[VIBN_Tools.TiaBridge net48] --> CONTRACTS
    IBNAPP[VIBN_Tools.IbnRemote net8.0-windows] --> CORE
    IBNAPP --> IBNINFRA[VIBN_Tools.IbnRemote.Infrastructure net8.0]
    IBNINFRA --> CORE
```

## Container-Generator-Klassen

```mermaid
classDiagram
    MvvmBase <|-- ContainerGenerationPageVM
    ContainerGenerationPageVM --> ContainerGenerator
    ContainerGenerationPageVM --> XmlHandler
    ContainerGenerationPageVM --> GenerationWorkspaceSnapshot
    class ContainerGenerationPageVM {
      +ContainerList
      +UnassignedEntries
      +FilteredEntries
      +Settings
      +ActivityLog
      +ICommand bindings
      +Drag/drop handlers
      +Grid filters
      +Open_InterfaceFile()
      +Generate_Containers()
      +Validate_Workspace()
      +Undo_LastAction()
    }
```

Die große ViewModel-Klasse ist eine bewusst dokumentierte Übergangsausnahme: Eine frühere Aufteilung wurde wegen einer ZULI-Regression zurückgenommen. `Interface5.xlsx` und `Interface7.xlsx` sichern inzwischen Import und Generatorübergabe; vor einer erneuten Zerlegung wird zusätzlich ein Golden Master aus Requirements und erwarteter vollständiger Ausgabe benötigt.

## TIA-Hardwaredatenfluss

```mermaid
sequenceDiagram
    participant U as Benutzer
    participant W as WPF ViewModel
    participant C as TIA Client
    participant B as net48 Bridge
    participant T as TIA Portal
    U->>W: Hardware auslesen
    W->>C: ListHardwareAsync
    C->>B: Named-Pipe Request
    B->>T: Root-, Gruppen- und Ungrouped-Geräte lesen
    B->>T: DeviceItems rekursiv traversieren
    B->>T: Addresses, NetworkInterface, GSD Services lesen
    T-->>B: Read-only Openness-Objekte
    B-->>C: TiaHardwareModuleInfo[]
    C-->>W: typisierte DTOs
    W-->>U: Tabelle und optionale Special-Device-Zuordnung
```

## Container2FEE-Visual-Datenfluss

```mermaid
flowchart LR
    XML[Container XML, nur lesen] --> PARSER[Visual Plan Parser]
    PARSER --> PLAN[VisualPlan: Knoten und Kanten]
    PLAN <--> VM[Visual Page VM / Drag-and-drop]
    VM <--> SIDE[JSON-Sidecar mit SHA-256]
    FEE[FEE SimObjects] --> DISC[Discovery Adapter]
    DISC --> VM
    PLAN --> BIND[gemeinsamer Runtime Visual Plan Binder]
    BIND --> EXEC[Legacy Generation Adapter]
    BIND --> LINK[Existing SimObject Link Adapter]
    EXEC --> LEGACY[bestehende Containerklassen und ContainerToFeeService]
    LINK --> FEELOGIC[vorhandene FeeLogic aus Model Validation]
    LEGACY --> FEE
    FEELOGIC --> FEE
```

Der gemeinsame Binder überträgt nur typgeprüfte SimObject-Zuordnungen, Erzeugungswünsche und die Auswahl vollständiger Container. Signal-/Slot-Kanten sind sichtbar, bleiben aber Eigentum des bestehenden Executors. Link-only erzeugt keine neuen Modellobjekte.

## ViCo/Kanbanize-Datenfluss

```mermaid
flowchart LR
    KB[Kanbanize Boards] --> REF[Refresh Adapter]
    REF --> CACHE[Lokaler atomarer Cache]
    CACHE --> CAT[Workstation Catalog]
    CAT --> SEARCH[ViCo Übersicht/Suche]
    SEARCH --> RDP[Windows RDP]
    SEARCH --> PATH[Projektpfade]
    SEARCH --> CFG[KONFIGURATION bearbeiten]
    CFG --> KB
```

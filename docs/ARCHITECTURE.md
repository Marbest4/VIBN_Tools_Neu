# Architecture and data sources

## Startup path and lazy tabs

`MainWindow` constructs only the initially visible **Project Settings** page directly. Every other main tab uses `LazyPageHost`, which creates its concrete `UserControl` on the first visible selection and retains that instance afterwards. View-model state therefore survives tab changes while constructors for Container, TIA, Rockwell, AI and FEE analysis no longer burden cold startup. The secondary view inside Rechnerübersicht is lazy as well.

After `ContentRendered`, `MainWindowVM.InitializeAsync` loads roles and the workstation directory. Project Settings performs local automation-installation discovery on a background task and then refreshes reachable FEE computers. The shared FEE `CoreApi` lifecycle in `Services.Initialize` deliberately remains unchanged because the verified connection workflow and selected SDK assemblies depend on it. `App.StartupElapsed` and the **Anwendungsstart** log entry measure the elapsed time including central service initialization up to the first rendered main window.

## Boundaries

- `Application`: WPF views, view models, composition root and application log.
- `VIBN_Tools.Core`: platform-neutral ViCo/Kanbanize models, policies and interfaces.
- `VIBN_Tools.Infrastructure`: filesystem, cache, HTTP, Windows RDP/session and JSON role adapters.
- `VIBN_Tools.ContainerGeneration`: Container import, requirements, matching, reimport, XML schemas and AI helpers.
- `VIBN_Tools.SharedWpf`: shared observable base, commands and WPF binding behaviors.
- `VIBN_Tools.Tia.Contracts`: serializable protocol DTOs.
- `VIBN_Tools.Tia.Client`: typed named-pipe client.
- `VIBN_Tools.TiaBridge`: isolated Siemens Openness process.
- `VIBN_Tools.Quality`: platform-neutral project profiles, findings/evidence, stable signal identities, generated simulation scenarios, adapter contracts and generation manifests.

## Source of truth matrix

| Data | Source | Rule |
| --- | --- | --- |
| Workstations and user assignment | Kanbanize workstation cache | `KONFIGURATION / USER` overrides older card text |
| Header FEE station | connected computer + Rechnerübersicht cache | `localhost` stays literal; otherwise show distinct projects from the computer's **In Arbeit** cards |
| Workstation configuration | `KONFIGURATION` card and card-level subtasks endpoint | update existing standard subtasks; explicitly create missing subtask/card |
| Online state | bounded ICMP ping | offline suppresses remote/path actions |
| Remote session / last logon | read-only `quser` | lack of permission means “Not available”, not offline |
| Workplace card schedule | VIBN source + single VIBN template deadline | source −14 days, template +56 days |
| Authorization | central `roles.json` | `lutzma` is Level9; at least two Level9 users on save |
| TIA hardware | all project devices via Openness; selected PLC is sorted first | read-only device/module tree, GSD/network metadata, slot/subslot and byte address data before FEE creation |
| ViCo refresh/display preferences | `%LOCALAPPDATA%/GROB/VIBN_Tools/ViCo/user-preferences.json` | 1–1440 minutes plus optional-column visibility; atomic local write |
| FEE/Kanbanize/RDP configuration | current Windows user's Credential Manager | UI writes/deletes generic credentials; live adapters resolve values only for the action |
| Navigation width | `%LOCALAPPDATA%/GROB/VIBN_Tools/navigation-preferences.json` | expanded/collapsed boolean only; atomic local write |
| Quality profiles/evidence/signal identities/manifests | `%LOCALAPPDATA%/VIBN_Tools/quality` | atomic JSON; conflicts never partially overwrite the signal registry |

## Reliability and performance

- Core policies are testable without live services.
- ContainerGeneration owns its package and schema dependencies instead of being wildcard-compiled by the executable.
- Shared WPF commands and collection replacement rules are implemented once and reused by both desktop applications.
- Cache files and role files are written atomically.
- Workstation ping and remote-session queries have separate bounded concurrency.
- Kanbanize synchronization is idempotent through source `custom_id` and uses narrow payloads.
- TIA stays outside the WPF process and bridge failures are caught at view-model boundaries.
- TIA compile results cross the same typed pipe boundary and are persisted as explicit evidence; compile never implies project save.
- External simulation adapters must distinguish filesystem readiness from a live manufacturer-API verification.
- WPF grids use virtualization and deferred tab templates are covered by a UI startup test.
- Global list/grid/tree scrolling uses pixel units; the main window normalizes wheel input to small vertical or horizontal increments without disabling item virtualization.
- The main window uses practical minimum dimensions; data grids keep their own virtualization/scrolling and detail panels scroll independently.
- `MainWindowVM` owns the navigation-width state; only TabItem header text is collapsed, while icons, content and role visibility remain intact.

## Container2FEE visual plan persistence

The original Container XML remains immutable. Slot overrides, extra/removed signals, assignments, generation choices and signal-only container type overrides are stored in the fingerprint-bound visual sidecar. Sidecar schema 10 adds `ContainerTypeOverrides`. `RuntimeVisualPlanBinder` creates the effective document in memory, applies the selected known container type and adds the corresponding logic/technical-helper/SimObject targets to the plan. Export recomputes stable container identities from that effective document so a type change cannot accidentally filter the container out of the exported XML.

The public FEE SDK currently exposes selected-object reads but no public operation to select an object in the FEE tree. UI navigation therefore synchronizes the VIBN lists/tree by stable plan IDs and keeps SDK object GUIDs in technical diagnostics only; it does not claim to control the external FEE selection.

Cancellation is cooperative at every tool-owned boundary. A vendor call that is already blocked inside the shared in-process SDK cannot be terminated safely by killing its thread. The visual page detaches such a task from the UI, bounds the subsequent disconnect attempt to two seconds and prevents another FEE operation until the call returns. Application shutdown uses the same bounded disconnect before process exit. A genuinely killable per-call boundary would require moving ownership of the complete FEE session and all runtime objects into a separately supervised worker process; a thread abort or a second in-process client would risk corrupted shared SDK state.

## Remote Desktop credential boundary

The `.rdp` profile contains only host, Kanbanize-selected user, monitor selection and prompt mode. Project Settings in the full application stores FEE credentials, the API key and RDP password as generic entries in the signed-in user's Windows Credential Manager. The separately published IBN test executable instead receives deliberately invalid embedded placeholders and has no credential editor. The automatic action reads the password through the credential-service abstraction, creates `TERMSRV/<host>` through `cmdkey`, launches `mstsc`, and removes only that transient RDP entry after 20 seconds. Former `VIBN_VICO_KANBANIZE_API_KEY`, `VIBN_RDP_PASSWORD`, `VIBN_FEE_USERNAME` and `VIBN_FEE_PASSWORD` user variables are migrated by the full tool on first successful read and then deleted. Values are never logged or exposed as bindable status. Embedded IBN values are explicitly non-secret and must not be replaced with production credentials.

# Architecture and data sources

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
- The main window uses practical minimum dimensions; data grids keep their own virtualization/scrolling and detail panels scroll independently.
- `MainWindowVM` owns the navigation-width state; only TabItem header text is collapsed, while icons, content and role visibility remain intact.

## Remote Desktop credential boundary

The `.rdp` profile contains only host, Kanbanize-selected user, monitor selection and prompt mode. Project Settings in the full application stores FEE credentials, the API key and RDP password as generic entries in the signed-in user's Windows Credential Manager. The separately published IBN test executable instead receives deliberately invalid embedded placeholders and has no credential editor. The automatic action reads the password through the credential-service abstraction, creates `TERMSRV/<host>` through `cmdkey`, launches `mstsc`, and removes only that transient RDP entry after 20 seconds. Former `VIBN_VICO_KANBANIZE_API_KEY`, `VIBN_RDP_PASSWORD`, `VIBN_FEE_USERNAME` and `VIBN_FEE_PASSWORD` user variables are migrated by the full tool on first successful read and then deleted. Values are never logged or exposed as bindable status. Embedded IBN values are explicitly non-secret and must not be replaced with production credentials.

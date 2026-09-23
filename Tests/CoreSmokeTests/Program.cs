using VIBN_Tools.Core.ViCo;
using VIBN_Tools.Core.Kanbanize;
using VIBN_Tools.Infrastructure.Kanbanize;
using VIBN_Tools.Infrastructure.ViCo;
using VIBN_Tools.Tia.Client;
using VIBN_Tools.Tia.Contracts;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text.Json;

var temporaryRoot = Path.Combine(Path.GetTempPath(), $"vibn-vico-tests-{Guid.NewGuid():N}");

try
{
    Directory.CreateDirectory(temporaryRoot);
    Console.WriteLine("Running project catalog and search smoke test...");
    await VerifyProjectCatalogAndSearchAsync(temporaryRoot);
    Console.WriteLine("Running favorites compatibility smoke test...");
    await VerifyFavoritesCompatibilityAsync(temporaryRoot);
    Console.WriteLine("Running bounded file copy smoke test...");
    await VerifyFileCopyAsync(temporaryRoot);
    Console.WriteLine("Running legacy workstation catalog smoke test...");
    await VerifyLegacyWorkstationCatalogAsync(temporaryRoot);
    Console.WriteLine("Running workstation occupancy and unified search smoke test...");
    VerifyWorkstationOccupancyAndUnifiedSearch();
    Console.WriteLine("Running shared workstation directory smoke test...");
    await VerifyWorkstationDirectoryAsync();
    Console.WriteLine("Running per-user credential configuration smoke test...");
    VerifyUserCredentialConfiguration();
    Console.WriteLine("Running ViCo auto-refresh preference smoke test...");
    await VerifyAutoRefreshPreferencesAsync(temporaryRoot);
    Console.WriteLine("Running ViCo project identity and path smoke test...");
    VerifyProjectIdentityAndPaths(temporaryRoot);
    Console.WriteLine("Running Remote Desktop profile smoke test...");
    VerifyRemoteDesktopProfile();
    Console.WriteLine("Running Level9 role policy smoke test...");
    VerifyRoleAdministrationPolicy();
    Console.WriteLine("Running Kanbanize card draft policy smoke test...");
    VerifyKanbanizeCardDraftPolicy();
    Console.WriteLine("Running idempotent VIBN workplace synchronization smoke test...");
    await VerifyVibnWorkplaceSynchronizationAsync();
    Console.WriteLine("Running narrow Kanbanize HTTP write-scope smoke test...");
    await VerifyKanbanizeHttpWriteScopeAsync();
    Console.WriteLine("Running Kanbanize Team-Aufgaben workflow scope smoke test...");
    await VerifyKanbanizeWorkflowStructureAsync();
    Console.WriteLine("Running workstation KONFIGURATION write-scope smoke test...");
    await VerifyWorkstationConfigurationWriteScopeAsync();
    Console.WriteLine("Running Kanbanize refresh/subtask API smoke test...");
    await VerifyKanbanizeRefreshApiAsync(temporaryRoot);
    Console.WriteLine("Running Kanbanize invalid-refresh cache protection smoke test...");
    await VerifyKanbanizeInvalidRefreshProtectionAsync(temporaryRoot);
    Console.WriteLine("Running role store and update smoke test...");
    await VerifyRoleStoreAndUpdateAsync(temporaryRoot);
    Console.WriteLine("Running administration identity smoke test...");
    await VerifyAdministrationIdentityAsync();
    Console.WriteLine("Running TIA library workflow smoke test...");
    await VerifyTiaLibraryWorkflowAsync(temporaryRoot);
    Console.WriteLine("Running TIA axis selection and result smoke test...");
    VerifyTiaAxisSelectionModel();
    Console.WriteLine("Running typed TIA pipe protocol smoke test...");
    await VerifyTypedTiaPipeProtocolAsync();
    Console.WriteLine("Running typed TIA pipe timeout diagnostic smoke test...");
    await VerifyTypedTiaPipeTimeoutDiagnosticAsync();
    Console.WriteLine("All ViCo core smoke tests passed.");
    return 0;
}
finally
{
    if (Directory.Exists(temporaryRoot))
        Directory.Delete(temporaryRoot, recursive: true);
}

static async Task VerifyProjectCatalogAndSearchAsync(string temporaryRoot)
{
    var projectPath = Path.Combine(temporaryRoot, "Area", "GM1234_Line", "05-130");
    Directory.CreateDirectory(projectPath);

    var options = new ViCoPathsOptions(temporaryRoot, Path.Combine(temporaryRoot, "favorites.txt"));
    var catalog = await new FileSystemProjectCatalogService(options).LoadAsync();

    Assert(catalog.Projects.Count == 1, "The project catalog should contain one project.");
    Assert(catalog.Projects[0].DisplayName == "GM1234/05-130", "Unexpected display name.");

    var results = new ProjectSearchService().Search(catalog.Projects, "gm1234/05130");
    Assert(results.Count == 1 && results[0].FullPath == projectPath, "Normalized search failed.");
}

static async Task VerifyFavoritesCompatibilityAsync(string temporaryRoot)
{
    var favoritesPath = Path.Combine(temporaryRoot, "favorites", "Favorites_2.txt");
    var repository = new LegacyTextFavoritesRepository(favoritesPath);
    var expected = new[] { new FavoriteEntry("Project", @"C:\Projects\Project") };

    await repository.SaveAsync(expected);
    var actual = await repository.LoadAsync();

    Assert(actual.SequenceEqual(expected), "Legacy favorites roundtrip failed.");
}

static async Task VerifyFileCopyAsync(string temporaryRoot)
{
    var sourceDirectory = Path.Combine(temporaryRoot, "copy-source");
    var destinationDirectory = Path.Combine(temporaryRoot, "copy-destination");
    Directory.CreateDirectory(sourceDirectory);
    await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "data.txt"), "VIBN");

    var service = new BoundedFileCopyService();
    await service.CopyAsync(new[] { new FileCopyItem(sourceDirectory, destinationDirectory) });

    Assert(
        await File.ReadAllTextAsync(Path.Combine(destinationDirectory, "data.txt")) == "VIBN",
        "File copy failed.");
}

static async Task VerifyLegacyWorkstationCatalogAsync(string temporaryRoot)
{
    var cache = Path.Combine(temporaryRoot, "server-cache");
    Directory.CreateDirectory(cache);
    await File.WriteAllLinesAsync(
        Path.Combine(cache, "AllPCLaneInfosWithChilds.txt"),
        new[] { "lane-1", "GM12345 Tool PC" });
    await File.WriteAllLinesAsync(
        Path.Combine(cache, "AllCardsOfPCsV2.txt"),
        new[]
        {
            "#Working#[GM9000/01-001] Demo", "lane-1",
            "Remote user: ZKDS-Simulation-P01", "lane-1",
            "TIA V18", "lane-1",
            "Beckhoff TwinCAT 3 angegeben", "lane-1",
            "Rockwell Studio 5000 V35 installiert", "lane-1",
            "FEE 5.0", "lane-1",
            "LAN Industrial", "lane-1"
        });
    await File.WriteAllLinesAsync(
        Path.Combine(cache, "AllRobyCards.txt"),
        new[] { "[GM9000/01-001][R01] Software Robotik", "column-working" });
    await File.WriteAllLinesAsync(
        Path.Combine(cache, "AllRobyCardsRobyName.txt"),
        new[] { "R01", "column-working" });
    await File.WriteAllLinesAsync(
        Path.Combine(cache, "AllRobyColumns.txt"),
        new[] { "column-working", "In Arbeit" });
    await File.WriteAllTextAsync(
        Path.Combine(cache, "WorkstationBoardCache.json"),
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            lanes = Array.Empty<object>(),
            cards = new object[]
            {
                new
                {
                    id = 901,
                    laneId = "lane-1",
                    columnId = "column-config",
                    title = "KONFIGURATION",
                    subtasks = new[]
                    {
                        new { id = 911, description = "USER: zkds-config-priority" },
                        new { id = 912, description = "STANDORT: Werk 2" },
                        new { id = 913, description = "SW: TIA V18; Beckhoff TwinCAT 3" },
                        new { id = 914, description = "PROJEKT-IP: 10.20.30.40" },
                        new { id = 915, description = "SONSTIGES: Wartungsfenster Freitag" }
                    }
                },
                new
                {
                    id = 902,
                    laneId = "lane-1",
                    columnId = "29375",
                    title = "[GM9000/01-001] Demo",
                    startDate = "2026-09-01T00:00:00+00:00",
                    deadline = "2026-09-30T00:00:00+00:00",
                    subtasks = Array.Empty<object>()
                }
            }
        }));

    var snapshot = await new LegacyWorkstationCatalog(cache).LoadAsync();
    Assert(snapshot.Workstations.Count == 1, "Legacy workstation catalog should contain one workstation.");
    var workstation = snapshot.Workstations[0];
    Assert(workstation.PcName == "GM12345", "Workstation name parsing failed.");
    Assert(workstation.UserName == "zkds-config-priority", "The KONFIGURATION USER must take precedence over older card text.");
    Assert(workstation.WorkstationConfiguration.CardId == 901 &&
           workstation.WorkstationConfiguration.ProjectIp.Value == "10.20.30.40" &&
           workstation.WorkstationConfiguration.Other.Value.Contains("Freitag", StringComparison.Ordinal),
        "KONFIGURATION fields and subtask identities were not retained for safe editing.");
    Assert(workstation.Status == "Belegt", "Active Kanbanize cards must mark the workstation as occupied.");
    Assert(workstation.Projects.Count == 1 && workstation.Projects[0].Contains("GM9000", StringComparison.Ordinal) &&
           workstation.Details.Any(card => card.Contains("FEE 5.0", StringComparison.Ordinal)),
        "The compact project column must contain only planning/working cards while full details remain available.");
    Assert(workstation.AutomationSoftware.Count == 2,
        "Software must be detected only from the KONFIGURATION/SW subtask, never from older lane cards.");
    Assert(workstation.SoftwareInformation.Contains("TwinCAT", StringComparison.OrdinalIgnoreCase),
        "Beckhoff software information is missing.");
    Assert(workstation.RobotCount == 1 && workstation.RobotDetails[0].Name == "R01",
        "Robot name, status or deduplication failed.");
    Assert(workstation.ProjectCardDetails.Count == 1 &&
           workstation.ProjectCardDetails[0].StartDate?.Day == 1 &&
           workstation.ProjectCardDetails[0].Deadline?.Day == 30,
        "Structured project start/deadline data was not retained from the workstation cache.");
    Assert(new ViCoWorkstationSearch().Search(snapshot.Workstations, "GM9000", ViCoSearchMode.Project).Count == 1,
        "Project-oriented workstation search failed.");
    Assert(new ViCoWorkstationSearch()
            .SearchWithMatches(snapshot.Workstations, "GM9000", ViCoSearchMode.All)
            .All(hit => !hit.MatchedColumns.Contains("Robotik", StringComparer.OrdinalIgnoreCase)),
        "Robot metadata must not add the internal 'Robotik' label to ViCo search results.");
}

static void VerifyWorkstationOccupancyAndUnifiedSearch()
{
    var free = new ViCoWorkstation(
        "GM10001 Free PC",
        "GM10001",
        "zkds-free",
        string.Empty,
        string.Empty,
        string.Empty,
        new[] { "[B] GM1000/01-001", "[D] GM1000/01-002" },
        new[] { "[B] GM1000/01-001", "[D] GM1000/01-002" });
    var occupied = new ViCoWorkstation(
        "GM10002 Busy PC",
        "GM10002",
        "zkds-busy",
        string.Empty,
        string.Empty,
        string.Empty,
        new[] { "[P] GM2000/01-001", "[D] GM2000/01-002" },
        new[] { "[P] GM2000/01-001", "[D] GM2000/01-002" });

    Assert(free.Status == "Frei", "Backlog/done-only cards must mark a workstation as free.");
    Assert(occupied.Status == "Belegt", "Planning must take precedence over done cards.");

    var search = new ViCoWorkstationSearch();
    Assert(search.Search(new[] { free, occupied }, "zkds-busy", ViCoSearchMode.All).Single() == occupied,
        "Unified search must find a Kanbanize user without selecting a separate mode.");
    Assert(search.Search(new[] { free, occupied }, "GM1000/01-001", ViCoSearchMode.All).Single() == free,
        "Unified search must continue to find project numbers.");

    var configuration = new ViCoWorkstationConfiguration(
        701,
        new ViCoConfigurationField("USER", "zkds-busy", 711),
        new ViCoConfigurationField("STANDORT", "Werk München", 712),
        new ViCoConfigurationField("SW", "TIA V20", 713),
        new ViCoConfigurationField("PROJEKT-IP", "10.25.30.40", 714),
        new ViCoConfigurationField("SONSTIGES", "Prüfplatz Nord", 715));
    var configured = occupied with
    {
        SoftwareInformation = "TIA Portal V20",
        Details = occupied.Details.Concat(new[] { "nur-in-kanbanize-details" }).ToArray(),
        Configuration = configuration
    };
    foreach (var visibleValue in new[]
             {
                 "GM10002", "GM2000/01-001", "TIA V20", "München", "10.25.30.40",
                 "Prüfplatz", "zkds-busy"
             })
    {
        Assert(search.Search(new[] { configured }, visibleValue, ViCoSearchMode.All).Count == 1,
            $"Overview search did not include visible field '{visibleValue}'.");
    }
    var hiddenHit = search.SearchWithMatches(
        new[] { configured },
        "nur-in-kanbanize-details",
        ViCoSearchMode.All).Single();
    Assert(hiddenHit.MatchedColumns.Contains("Kanbanize-Details"),
        "Hidden Kanbanize details must be searchable and identify the matching logical column.");
    Assert(search.SearchWithMatches(
               new[] { configured },
               "nur-in-kanbanize-details",
               ViCoSearchMode.All,
               new[] { "PC", "Projekt Planung", "Projekt In Arbeit" }).Count == 0,
        "Visible-column-only search must exclude values from hidden logical columns.");
    Assert(search.Search(new[] { configured }, "GM2000, TIA V20", ViCoSearchMode.All).Single() == configured,
        "Comma-separated positive terms must use AND semantics across all searchable columns.");
    Assert(search.Search(new[] { configured }, "GM2000, !Sensor, !Alt", ViCoSearchMode.All).Single() == configured,
        "Negative search terms must exclude only rows containing one of the forbidden terms.");
    Assert(search.Search(new[] { configured }, "GM2000, !Prüfplatz", ViCoSearchMode.All).Count == 0,
        "Negative terms must also search hidden/configuration fields.");
}

static async Task VerifyWorkstationDirectoryAsync()
{
    var workstation = new ViCoWorkstation(
        "GM12345 Tool PC",
        "GM12345",
        "kanbanize-user",
        "TIA Portal V18",
        string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>());
    var directory = new WorkstationDirectory(new SnapshotCatalog(workstation));
    await directory.RefreshAsync();

    Assert(directory.PcNames.SequenceEqual(new[] { "localhost", "GM12345" }),
        "The shared workstation directory did not expose the dynamic PC list.");
    Assert(directory.FindUser("gm12345") == "kanbanize-user",
        "Kanbanize user priority in the shared workstation directory failed.");
}

static void VerifyUserCredentialConfiguration()
{
    var variables = new Dictionary<(string Name, EnvironmentVariableTarget Target), string?>();
    var service = new UserEnvironmentCredentialConfigurationService(
        (name, target) => variables.GetValueOrDefault((name, target)),
        (name, value, target) => variables[(name, target)] = value);

    Assert(!service.ReadStatus().HasKanbanizeApiKey &&
           !service.ReadStatus().HasRemoteDesktopPassword &&
           !service.ReadStatus().HasFeeCredentials,
        "A fresh user credential configuration must report all values as missing.");

    service.SaveKanbanizeApiKey("  test-api-key  ");
    service.SaveRemoteDesktopPassword(" test password ");
    service.SaveFeeCredentials(" fee-user ", "fee-password");
    var configured = service.ReadStatus();
    Assert(configured.HasKanbanizeApiKey && configured.HasRemoteDesktopPassword && configured.HasFeeCredentials,
        "Saved per-user credentials were not detected.");
    Assert(service.GetKanbanizeApiKey() == "test-api-key",
        "The API key provider did not return the current persisted value.");
    Assert(service.GetRemoteDesktopPassword() == " test password ",
        "The RDP password provider must preserve significant whitespace.");
    Assert(service.GetFeeUsername() == "fee-user" && service.GetFeePassword() == "fee-password",
        "The FEE credential provider did not preserve the configured pair.");
    Assert(variables[(UserEnvironmentCredentialConfigurationService.RemoteDesktopPasswordVariable,
            EnvironmentVariableTarget.Process)] == " test password ",
        "The RDP password must be available immediately without trimming or restarting the app.");

    service.DeleteKanbanizeApiKey();
    service.DeleteRemoteDesktopPassword();
    service.DeleteFeeCredentials();
    Assert(!service.ReadStatus().HasKanbanizeApiKey &&
           !service.ReadStatus().HasRemoteDesktopPassword &&
           !service.ReadStatus().HasFeeCredentials,
        "Deleted credentials still appear configured.");

    service.SaveKanbanizeApiKey("legacy-key");
    service.SaveRemoteDesktopPassword("legacy-password");
    service.SaveFeeCredentials("legacy-fee-user", "legacy-fee-password");
    var protectedStore = new MemoryUserSecretStore();
    var protectedService = new SecureUserCredentialConfigurationService(protectedStore, service);
    var protectedStatus = protectedService.ReadStatus();
    Assert(protectedStatus.HasKanbanizeApiKey && protectedStatus.HasRemoteDesktopPassword && protectedStatus.HasFeeCredentials &&
           protectedStore.Values[SecureUserCredentialConfigurationService.KanbanizeTarget] == "legacy-key" &&
           protectedStore.Values[SecureUserCredentialConfigurationService.RemoteDesktopTarget] == "legacy-password" &&
           protectedStore.Values[SecureUserCredentialConfigurationService.FeeUsernameTarget] == "legacy-fee-user" &&
           protectedStore.Values[SecureUserCredentialConfigurationService.FeePasswordTarget] == "legacy-fee-password",
        "Legacy user environment credentials were not migrated into the protected store.");
    Assert(service.GetKanbanizeApiKey() is null && service.GetRemoteDesktopPassword() is null &&
           service.GetFeeUsername() is null && service.GetFeePassword() is null,
        "Plain environment credentials must be removed after successful migration.");
    protectedService.SaveKanbanizeApiKey("replacement-key");
    Assert(protectedService.GetKanbanizeApiKey() == "replacement-key",
        "The protected credential store did not replace the API key.");
    protectedService.DeleteKanbanizeApiKey();
    protectedService.DeleteRemoteDesktopPassword();
    protectedService.DeleteFeeCredentials();
    Assert(!protectedService.ReadStatus().HasKanbanizeApiKey &&
           !protectedService.ReadStatus().HasRemoteDesktopPassword &&
           !protectedService.ReadStatus().HasFeeCredentials,
        "Protected credentials still appear configured after deletion.");

    service.SaveKanbanizeApiKey("retained-legacy-key");
    var unavailableStore = new MemoryUserSecretStore { FailWrites = true };
    var fallbackService = new SecureUserCredentialConfigurationService(unavailableStore, service);
    Assert(fallbackService.GetKanbanizeApiKey() == "retained-legacy-key" &&
           service.GetKanbanizeApiKey() == "retained-legacy-key",
        "A failed protected-store migration must retain and return the only legacy value.");
    service.DeleteKanbanizeApiKey();

    string? dynamicApiKey = null;
    using var httpClient = new HttpClient();
    var dynamicAdapter = new KanbanizeCardApiService(
        httpClient,
        () => dynamicApiKey,
        "https://example.test/api/v2");
    Assert(!dynamicAdapter.IsConfigured, "A missing dynamic API key was accepted.");
    dynamicApiKey = "configured-later";
    Assert(dynamicAdapter.IsConfigured,
        "A Kanbanize adapter did not observe an API key configured after construction.");
}

static async Task VerifyAutoRefreshPreferencesAsync(string temporaryRoot)
{
    var file = Path.Combine(temporaryRoot, "preferences", "vico.json");
    var store = new JsonViCoAutoRefreshSettingsStore(file);
    await store.SaveAsync(new ViCoAutoRefreshSettings(0, true, ["pc", "planning", "projectIp"]));
    var normalized = await store.LoadAsync();
    Assert(normalized.IntervalMinutes == ViCoAutoRefreshPolicy.MinimumIntervalMinutes,
        "An invalid auto-refresh interval was not normalized before persistence.");
    Assert(normalized.ShowExtendedInformation,
        "The optional ViCo column preference was not persisted.");
    Assert(normalized.VisibleColumns?.SequenceEqual(["pc", "planning", "projectIp"]) == true,
        "The per-column ViCo visibility preference was not persisted.");

    await File.WriteAllTextAsync(file, "not-json");
    var recovered = await store.LoadAsync();
    Assert(recovered == ViCoAutoRefreshSettings.Default,
        "A corrupt preference file must fall back to the documented default.");
}

static void VerifyProjectIdentityAndPaths(string temporaryRoot)
{
    var simulationRoot = Path.Combine(temporaryRoot, "simulation");
    var simulationPath = Path.Combine(simulationRoot, "Area", "GM_GU1660_Line", "05-130");
    Directory.CreateDirectory(simulationPath);

    var resolver = new ViCoRelatedPathResolver(
        simulationRoot,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GM_GU1660/05-130"] = simulationPath
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GU1660"] = @"\\server\plc\GU1660"
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer_GU1660_Planning"] = @"\\server\planning\GU1660"
        });
    var workstation = new ViCoWorkstation(
        "GM12345 Tool PC",
        "GM12345",
        "zkds-simulation-p01",
        "TIA V18",
        "FEE 5",
        "LAN",
        new[] { "[P] GM_GU1660/05-130 Planning", "[W] GM_GU1660/05-140 Working" },
        Array.Empty<string>(),
        ProjectCards:
        [
            new ViCoProjectCardInfo(101, "[P] GM_GU1660/05-130 Planning", "Planung", new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero), null),
            new ViCoProjectCardInfo(102, "[W] GM_GU1660/05-140 Working", "In Arbeit", null, new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero))
        ]);

    Assert(workstation.PlanningProjects.SequenceEqual(["GM_GU1660/05-130 Planning"]) &&
           workstation.WorkingProjects.SequenceEqual(["GM_GU1660/05-140 Working"]) &&
           workstation.HasActiveProjects,
        "Planning and working Kanbanize cards were not split without legacy status markers.");

    var projectCard = workstation.Projects[0];
    Assert(resolver.Resolve(workstation, projectCard, ViCoRelatedPathKind.Simulation) == simulationPath,
        "Status-tolerant simulation path resolution failed.");
    Assert(resolver.Resolve(workstation, projectCard, ViCoRelatedPathKind.Commissioning) == @"\\server\plc\GU1660",
        "Commissioning path resolution failed.");
    Assert(resolver.Resolve(workstation, projectCard, ViCoRelatedPathKind.Planning) == @"\\server\planning\GU1660",
        "Planning path resolution failed.");

    var workstationPath = resolver.Resolve(workstation, projectCard, ViCoRelatedPathKind.WorkstationProject);
    Assert(workstationPath == Path.Combine(@"\\GM12345\_Projekte$", "Area", "GM_GU1660_Line", "05-130"),
        "Workstation project path mapping failed.");

    var structureRoot = Path.Combine(temporaryRoot, "project-structure");
    new StandardProjectStructureService().EnsureCreated(structureRoot);
    Assert(Directory.Exists(Path.Combine(structureRoot, "02_SimulationProject")),
        "Standard project structure creation failed.");
}

static void VerifyRemoteDesktopProfile()
{
    var lines = RemoteDesktopProfileBuilder.Build(
        "GM12345",
        "zkds-simulation-p01",
        new[] { 0, 2 },
        3);
    Assert(lines.Contains("username:s:zkds-simulation-p01"),
        "The normalized Kanbanize user was not written to the RDP profile.");
    Assert(lines.Contains("prompt for credentials:i:0"),
        "The RDP profile must retain the automatic Windows credential behavior.");
    Assert(lines.Contains("redirectclipboard:i:1") &&
           lines.Contains("redirectprinters:i:0") &&
           lines.Contains("redirectcomports:i:0") &&
           lines.Contains("redirectsmartcards:i:0") &&
           lines.Contains("drivestoredirect:s:"),
        "The RDP profile must request only the predefined clipboard resource.");
    Assert(lines.Contains("selectedmonitors:s:0,2"),
        "Selected RDP monitors were not preserved.");
    Assert(!lines.Any(line => line.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("pass:", StringComparison.OrdinalIgnoreCase)),
        "RDP profiles must never contain credential material.");

    var promptedLines = RemoteDesktopProfileBuilder.Build(
        "GM12345",
        string.Empty,
        new[] { 0 },
        1,
        promptForCredentials: true);
    Assert(promptedLines.Contains("prompt for credentials:i:1"),
        "The separate RDP button must open the Windows credential dialog.");
    Assert(!promptedLines.Any(line => line.StartsWith("username:s:", StringComparison.OrdinalIgnoreCase)),
        "The prompted RDP profile must not inject an automatic user name.");
}

static async Task VerifyRoleStoreAndUpdateAsync(string temporaryRoot)
{
    var rolesFile = Path.Combine(temporaryRoot, "roles", "roles.json");
    var roles = new JsonViCoUserRoleStore(rolesFile);
    await roles.SaveAsync(new[]
    {
        new ViCoUserRole(@"grob\lutzma", "Level9", "test"),
        new ViCoUserRole(@"grob\user", "Level8", "test"),
        new ViCoUserRole("admin-b", "Level9", "test")
    });
    var entries = await roles.LoadAsync();
    Assert(entries.Count == 3 && entries.Single(entry => entry.UserName == "user").Level == "Level8",
        "The license-free role store roundtrip failed.");
    Assert(WindowsUserIdentity.Equals(@"grob\user", "user"),
        "Domain-qualified and short Windows users should identify the same role.");

    var rejected = false;
    try
    {
        await roles.SaveAsync(new[] { new ViCoUserRole("lutzma", "Level9", "test") });
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Assert(rejected, "The role store must reject a role set with only one Level9 administrator.");

    var version = Path.Combine(temporaryRoot, "versions", "V1.2.3", "publish");
    Directory.CreateDirectory(version);
    await File.WriteAllTextAsync(Path.Combine(version, "VICO_V2.exe"), string.Empty);
    var update = await new FileSystemViCoUpdateService(Path.Combine(temporaryRoot, "versions")).FindLatestAsync();
    Assert(update?.Version == "1.2.3", "ViCo update discovery failed.");
}

static void VerifyRoleAdministrationPolicy()
{
    var oneLevel9 = new[]
    {
        new ViCoUserRole(@"grob\lutzma", "Level9", "memory"),
        new ViCoUserRole("admin-b", "Level8", "memory")
    };
    var promotion = ViCoRolePolicy.PlanSave(oneLevel9.Select(role =>
        WindowsUserIdentity.Equals(role.UserName, "admin-b")
            ? role with { Level = "Level9" }
            : role));
    Assert(promotion.IsValid && promotion.Level9Users.Count == 2,
        "Promoting a second distinct Level9 user must be accepted.");

    var twoLevel9 = new[]
    {
        new ViCoUserRole(@"grob\lutzma", "Level9", "memory"),
        new ViCoUserRole("admin-a", "Level9", "memory"),
        new ViCoUserRole("admin-c", "Level8", "memory")
    };
    var unsafeDowngrade = ViCoRolePolicy.PlanSave(twoLevel9.Select(role =>
        WindowsUserIdentity.Equals(role.UserName, "admin-a")
            ? role with { Level = "Level8" }
            : role));
    Assert(!unsafeDowngrade.IsValid,
        "Downgrading to a single Level9 user must be rejected.");

    var safeReplacement = ViCoRolePolicy.PlanSave(twoLevel9.Select(role =>
        WindowsUserIdentity.Equals(role.UserName, "admin-a")
            ? role with { Level = "Level8" }
            : WindowsUserIdentity.Equals(role.UserName, "admin-c")
                ? role with { Level = "Level9" }
                : role));
    Assert(safeReplacement.IsValid && safeReplacement.Level9Users.Count == 2,
        "Replacing a Level9 user atomically must be accepted.");
    Assert(safeReplacement.Roles.Single(role => role.UserName == "admin-c").Level == "Level9",
        "The replacement promotion must be included in the atomically saved role set.");

    var duplicateIdentity = new[]
    {
        new ViCoUserRole(@"grob\lutzma", "Level9", "memory"),
        new ViCoUserRole("LUTZMA", "Level9", "memory")
    };
    var duplicatePlan = ViCoRolePolicy.PlanSave(duplicateIdentity);
    Assert(!duplicatePlan.IsValid,
        "Domain-qualified and short names of the same account must count only once.");

    Assert(ViCoRolePolicy.GetEffectiveLevel(@"grob\lutzma", null) == "Level9",
        "lutzma must be an effective Level9 administrator even before the compatible store is refreshed.");
    Assert(ViCoRolePolicy.HasMinimumLevel("Level9", 9),
        "Level9 must satisfy the administration navigation gate.");
    Assert(!ViCoRolePolicy.HasMinimumLevel("Level8", 9),
        "Level8 must not satisfy the administration navigation gate.");
    var mandatoryUserDowngrade = ViCoRolePolicy.PlanSave(twoLevel9.Select(role =>
        WindowsUserIdentity.Equals(role.UserName, "lutzma")
            ? role with { Level = "Level8" }
            : role));
    Assert(mandatoryUserDowngrade.IsValid &&
           mandatoryUserDowngrade.Roles.Single(role => role.UserName == "lutzma").Level == "Level9",
        "The mandatory lutzma Level9 assignment must remain Level9 in every saved role set.");
}

static void VerifyKanbanizeCardDraftPolicy()
{
    var valid = new KanbanizeCardDraft(1541, 28125, 29373, "Neue Karte", "Beschreibung", 3, "GM1234", null);
    Assert(KanbanizeCardDraftPolicy.Validate(valid) is null,
        "A complete Kanbanize card draft should be valid.");
    Assert(KanbanizeCardDraftPolicy.Validate(valid with { Title = " " }) is not null,
        "A Kanbanize card title must be required.");
    Assert(KanbanizeCardDraftPolicy.Validate(valid with { Priority = 5 }) is not null,
        "Kanbanize card priority must be bounded.");
}

static async Task VerifyVibnWorkplaceSynchronizationAsync()
{
    var sourceDeadline = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    var expectedStart = sourceDeadline.AddDays(-14);
    var expectedEnd = sourceDeadline.AddDays(56);
    var service = new MemoryKanbanizeCardService(
        new[]
        {
            new KanbanizeCardInfo(101, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM1000", null, sourceDeadline),
            new KanbanizeCardInfo(102, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM2000", null, sourceDeadline),
            new KanbanizeCardInfo(105, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM5000", null, sourceDeadline),
            new KanbanizeCardInfo(106, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM6000", null, sourceDeadline),
            new KanbanizeCardInfo(107, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM7000", null, sourceDeadline),
            new KanbanizeCardInfo(109, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM9000", null, sourceDeadline),
            new KanbanizeCardInfo(110, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM10000", null, sourceDeadline),
            new KanbanizeCardInfo(111, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM11000", null, sourceDeadline),
            new KanbanizeCardInfo(112, 1392, 10, 20, "[VIBN] Nachpflege GM12000", null, sourceDeadline),
            new KanbanizeCardInfo(113, 1392, 10, 20, "[VIBN] Grundinbetriebnahme GM13000", null, sourceDeadline),
            new KanbanizeCardInfo(114, 1392, 10, 20, "[VIBN] Grundinbetriebnahme Fremdworkflow", null, sourceDeadline, WorkflowId: 99),
            new KanbanizeCardInfo(108, 1392, 10, 25236, "[VIBN] Grundinbetriebnahme Archiv", null, sourceDeadline)
        },
        new[]
        {
            new KanbanizeCardInfo(201, 1541, 28125, 29373, "Bestehende Karte", "102", sourceDeadline.AddDays(-3), expectedStart.AddDays(-1)),
            new KanbanizeCardInfo(205, 1541, 28125, 29373, "Bereits aktuell", "105", expectedEnd.AddHours(4), expectedStart.AddHours(4)),
            new KanbanizeCardInfo(209, 1541, 28125, 29373, "*[Gen]* GM9000", null, expectedEnd, expectedStart),
            new KanbanizeCardInfo(206, 1541, 28125, 29373, "GM6000 *[Gen]* CORE", "106", expectedEnd, expectedStart),
            new KanbanizeCardInfo(207, 1541, 28125, 29373, "GM6000 - CLIENT", "106", expectedEnd, expectedStart),
            new KanbanizeCardInfo(210, 1541, 28125, 29373, "GM7000 *[Gen]*", "107", expectedEnd, expectedStart),
            new KanbanizeCardInfo(211, 1541, 28125, 29373, "GM7000 *[Gen]*", "107", expectedEnd.AddDays(1), expectedStart),
            new KanbanizeCardInfo(212, 1541, 28125, 29373, "GM10000 *[Gen]*", "110", expectedEnd, expectedStart),
            new KanbanizeCardInfo(213, 1541, 28125, 29373, "GM10000 *[Gen]*", "999", expectedEnd, expectedStart),
            new KanbanizeCardInfo(214, 1541, 28125, 29373, "GM11000 *[Gen]* CORE", "111", expectedEnd, expectedStart),
            new KanbanizeCardInfo(215, 1541, 28125, 29373, "GM11000 - CORE", "111", expectedEnd, expectedStart),
            new KanbanizeCardInfo(216, 1541, 28125, 29373, "GM12000 *[Gen]*", "112", expectedEnd, expectedStart),
            new KanbanizeCardInfo(217, 1541, 28125, 29373, "GM13000 *[Gen]*", "113", expectedEnd, expectedStart),
            new KanbanizeCardInfo(218, 1541, 28125, 29373, "GM13000 - CLIENT", "113", expectedEnd, expectedStart)
        });
    var synchronization = new VibnWorkplaceSynchronizationService(service);
    var settings = new VibnWorkplaceSynchronizationSettings(1392, 1541, 28125, 29373, 3, true);

    var preview = await synchronization.PreviewAsync(settings);
    Assert(preview.CreateCount == 1 && preview.DeadlineUpdateCount == 1 && preview.UnchangedCount == 3 &&
           preview.RelatedCardsCount == 1 && preview.TitleUpdateCount == 1,
        "The preview must distinguish missing, stale and already-current target schedules.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 109).Action == VibnWorkplaceSynchronizationAction.Unchanged,
        "A legacy generated title must prevent duplicates even if its custom source ID is absent.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 105).Action == VibnWorkplaceSynchronizationAction.Unchanged,
        "Different times on the same local calendar dates must not trigger a schedule update.");
    var localDate = new DateTime(2026, 8, 27);
    var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDate);
    Assert(VibnWorkplaceSynchronizationPolicy.HasEquivalentDeadline(
            new DateTimeOffset(2026, 8, 27, 8, 0, 0, localOffset),
            new DateTimeOffset(2026, 8, 27, 16, 30, 0, localOffset)) &&
           !VibnWorkplaceSynchronizationPolicy.HasEquivalentDeadline(
               new DateTimeOffset(2026, 8, 27, 16, 30, 0, localOffset),
               new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeZoneInfo.Local.GetUtcOffset(localDate.AddDays(1)))),
        "Kanbanize planning dates must be compared by local calendar date and ignore the time component.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 106).Action ==
               VibnWorkplaceSynchronizationAction.RelatedCards &&
           preview.Items.Single(item => item.SourceCard.Id == 106).RelatedTargetCards?.Count == 2,
        "Different CORE/CLIENT cards with one source ID must be grouped without a blanket conflict.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 107).Message.Contains("unterschiedliche", StringComparison.OrdinalIgnoreCase),
        "Equal names on the same lane with different schedules need a specific conflict reason.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 110).Message.Contains("unterschiedliche Quellkarten-IDs", StringComparison.OrdinalIgnoreCase),
        "Equal target names with different source IDs must be a conflict.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 111).Message.Contains("CORE", StringComparison.Ordinal),
        "Duplicate CORE roles for one source ID must be a conflict.");
    Assert(preview.Items.Single(item => item.SourceCard.Id == 113).Action ==
               VibnWorkplaceSynchronizationAction.UpdatePrimaryTitle &&
           preview.Items.Single(item => item.SourceCard.Id == 113).ProposedTitle == "GM13000 *[Gen]* CORE",
        "A copied generated card must propose the primary-card CORE suffix.");
    Assert(preview.ConflictCount == 3 && preview.ExcludedSourceCardCount == 2 &&
           preview.Items.All(item => item.SourceCard.Id != 114),
        "Only Team-Aufgaben source cards may be considered; archive and other workflows stay excluded.");
    Assert(VibnWorkplaceSynchronizationPolicy.IsEligibleSourceCard(
               new KanbanizeCardInfo(1, 1392, 1, 1, "[VIBN] Nachpflege Test", null, sourceDeadline)),
        "Active Nachpflege cards must be eligible sources.");
    var generatedTitle = VibnWorkplaceSynchronizationPolicy.GetGeneratedTitle(
        "[VIBN] Grundinbetriebnahme [GM7283/01-1030] Kunde - Ort");
    var primary = VibnWorkplaceSynchronizationPolicy.ParseGeneratedTitle(generatedTitle);
    var client = VibnWorkplaceSynchronizationPolicy.ParseGeneratedTitle(
        "[GM7283/01-1030] Kunde - Ort - CLIENT");
    Assert(generatedTitle == "[GM7283/01-1030] Kunde - Ort *[Gen]*" &&
           primary.IdentityKey == client.IdentityKey && client.Role == "CLIENT",
        "Generated and copied card titles must share a structured base identity and role parsing.");
    Assert(preview.Items.Where(item => item.SourceCard.Deadline is not null).All(item =>
            item.Schedule is null || item.Schedule.EndDate == item.SourceCard.Deadline!.Value.AddDays(56)),
        "Every VIBN card must derive its end date from its own deadline without requiring a template card.");

    var withoutDeadlineSync = await synchronization.PreviewAsync(settings with { SynchronizeDeadlines = false });
    Assert(withoutDeadlineSync.DeadlineUpdateCount == 0,
        "Schedule synchronization must be explicitly suppressible without affecting duplicate detection.");

    var createOnly = await synchronization.SynchronizeAsync(settings, new[] { 101 });
    Assert(createOnly.CreatedCount == 1 && createOnly.DeadlineUpdateCount == 0 &&
           service.ScheduleChanges.Count == 0,
        "Only explicitly selected preview rows may be synchronized.");
    Assert(service.GeneratedCards.Single().SourceCardId == 101 &&
           service.GeneratedCards.Single().Title == "GM1000 *[Gen]*" &&
           service.GeneratedCards.Single().StartDate == expectedStart &&
           service.GeneratedCards.Single().Deadline == expectedEnd,
        "A generated card must preserve its identity and receive the calculated start/end schedule.");

    var deadlineOnly = await synchronization.SynchronizeAsync(settings, new[] { 102 });
    Assert(deadlineOnly.CreatedCount == 0 && deadlineOnly.DeadlineUpdateCount == 1 &&
           deadlineOnly.Failures.Count == 0,
        "The separately selected stale schedule should be adjusted without creating another card.");
    Assert(service.ScheduleChanges.SequenceEqual(new[] { new ScheduleChange(201, expectedStart, expectedEnd) }),
        "Only the existing generated card schedule may be changed; no other target field is updated.");

    var renameOnly = await synchronization.SynchronizeAsync(settings, new[] { 113 });
    Assert(renameOnly.TitleUpdateCount == 1 && renameOnly.CreatedCount == 0 &&
           service.TitleChanges.SequenceEqual(new[] { new TitleChange(217, "GM13000 *[Gen]* CORE") }),
        "Only the generated primary card title may receive the CORE suffix after a role copy exists.");

    var repeatPreview = await synchronization.PreviewAsync(settings);
    var repeatSelection = repeatPreview.Items
        .Where(item => item.Action is VibnWorkplaceSynchronizationAction.Create or VibnWorkplaceSynchronizationAction.UpdateDeadline)
        .Select(item => item.SourceCard.Id)
        .ToArray();
    Assert(repeatSelection.Length == 0,
        "A repeated preview must not expose already applied changes for selection.");
    var repeat = new VibnWorkplaceSynchronizationResult(repeatPreview, 0, 0, 0, Array.Empty<string>());
    Assert(repeat.CreatedCount == 0 && repeat.DeadlineUpdateCount == 0,
        "A second synchronization must not create duplicates or repeat unchanged schedule updates.");
}

static async Task VerifyKanbanizeHttpWriteScopeAsync()
{
    var sourceDeadline = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    var start = sourceDeadline.AddDays(-14);
    var end = sourceDeadline.AddDays(56);
    using var handler = new RecordingHttpMessageHandler();
    handler.EnqueueJson("""
        {"data":{"data":[{"card_id":101,"board_id":1392,"lane_id":10,"column_id":20,"title":"[VIBN] Grundinbetriebnahme GM1000","custom_id":null,"deadline":"2026-09-15T12:00:00.0000000Z","custom_fields":[{"field_id":508,"value":"2026-09-01T12:00:00.0000000Z"}]}],"pagination":{"all_pages":1}}}
        """);
    handler.EnqueueJson("""
        {"data":{"card_id":9001,"title":"*[Gen]* GM1000"}}
        """);
    handler.EnqueueJson("{}");
    handler.EnqueueJson("{}");
    using var httpClient = new HttpClient(handler);
    var api = new KanbanizeCardApiService(httpClient, "test-only-key", "https://example.test/api/v2");

    var cards = await api.LoadCardsAsync(1392);
    await api.CreateGeneratedCardAsync(new KanbanizeGeneratedCardDraft(
        101,
        28125,
        29373,
        "GM1000 *[Gen]*",
        3,
        end,
        start));
    await api.UpdateGeneratedScheduleAsync(9001, start, end);
    await api.UpdateGeneratedTitleAsync(9001, "GM1000 *[Gen]* CORE");

    Assert(cards.Count == 1 && string.IsNullOrEmpty(cards[0].CustomId) && cards[0].StartDate == sourceDeadline.AddDays(-14),
        "The card reader must preserve the source card identity and workplace start date.");
    Assert(handler.Requests.Count == 4, "The API adapter should make one read and three narrowly scoped writes.");
    Assert(handler.Requests[0].RelativeUrl.Contains("per_page=1000", StringComparison.Ordinal) &&
           handler.Requests[0].RelativeUrl.Contains("expand=custom_fields", StringComparison.Ordinal) &&
           handler.Requests[0].RelativeUrl.Contains("fields=card_id,title,custom_id,deadline", StringComparison.Ordinal) &&
           !handler.Requests[0].RelativeUrl.Contains("column_id", StringComparison.Ordinal),
        "The synchronization reader must explicitly request deadline using only Businessmap-valid fields.");
    Assert(handler.Requests.All(request => request.ApiKey == "test-only-key"),
        "Every Kanbanize request must carry the configured API key.");

    using var createPayload = JsonDocument.Parse(handler.Requests[1].Body);
    var create = createPayload.RootElement;
    Assert(create.GetProperty("lane_id").GetInt32() == 28125 &&
           create.GetProperty("column_id").GetInt32() == 29373 &&
           create.GetProperty("custom_id").GetString() == "101",
        "A generated workplace card must retain the selected destination and source identity.");
    Assert(create.GetProperty("links_to_existing_cards_to_add_or_update")[0]
               .GetProperty("linked_card_id").GetInt32() == 101,
        "A generated workplace card must retain the parent link to its source card.");
    Assert(!create.TryGetProperty("actual_end_time", out _) &&
           !create.TryGetProperty("description", out _),
        "The synchronization must not add unrelated card fields when creating a workplace card.");
    Assert(create.GetProperty("deadline").GetString() == end.UtcDateTime.ToString("O") &&
           create.GetProperty("custom_fields_to_add_or_update")[0].GetProperty("field_id").GetInt32() == 508 &&
           create.GetProperty("custom_fields_to_add_or_update")[0].GetProperty("value").GetString() == start.UtcDateTime.ToString("O"),
        "The generated card must receive only the established workplace start field and calculated end deadline.");

    using var patchPayload = JsonDocument.Parse(handler.Requests[2].Body);
    var patchFields = patchPayload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    Assert(patchFields.SequenceEqual(new[] { "deadline", "custom_fields_to_add_or_update" }, StringComparer.Ordinal) &&
           patchPayload.RootElement.GetProperty("deadline").GetString() == end.UtcDateTime.ToString("O") &&
           patchPayload.RootElement.GetProperty("custom_fields_to_add_or_update")[0].GetProperty("field_id").GetInt32() == 508 &&
           patchPayload.RootElement.GetProperty("custom_fields_to_add_or_update")[0].GetProperty("value").GetString() == start.UtcDateTime.ToString("O"),
        "The schedule sync must PATCH only the generated start field and deadline of an existing target card.");

    using var titlePayload = JsonDocument.Parse(handler.Requests[3].Body);
    var titleFields = titlePayload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    Assert(handler.Requests[3].Method == HttpMethod.Patch &&
           handler.Requests[3].RelativeUrl == "/api/v2/cards/9001" &&
           titleFields.SequenceEqual(new[] { "title" }, StringComparer.Ordinal) &&
           titlePayload.RootElement.GetProperty("title").GetString() == "GM1000 *[Gen]* CORE",
        "The CORE rename must PATCH only the generated card title.");
}

static async Task VerifyKanbanizeWorkflowStructureAsync()
{
    using var handler = new RecordingHttpMessageHandler();
    handler.EnqueueJson("{\"data\":[{\"lane_id\":10,\"workflow_id\":42,\"name\":\"Planung\"}]}");
    handler.EnqueueJson("{\"data\":[{\"column_id\":20,\"workflow_id\":42,\"name\":\"Backlog\"}]}");
    handler.EnqueueJson("{\"data\":[{\"workflow_id\":42,\"name\":\"Team-Aufgaben\"},{\"workflow_id\":99,\"name\":\"Initiativen\"}]}");
    using var httpClient = new HttpClient(handler);
    var api = new KanbanizeCardApiService(httpClient, "test-only-key", "https://example.test/api/v2");

    var structure = await api.LoadBoardStructureAsync(1392);
    Assert(structure.Workflows.Count == 2 &&
           structure.Workflows.Single(workflow => workflow.Name == "Team-Aufgaben").Id == 42 &&
           structure.Lanes.Single().WorkflowId == 42 &&
           structure.Columns.Single().WorkflowId == 42,
        "Board structure must expose workflow names so the VIBN source can be restricted to Team-Aufgaben.");
    Assert(handler.Requests.Any(request => request.RelativeUrl == "/api/v2/boards/1392/workflows"),
        "The Kanbanize adapter did not request the workflow catalog.");
}

static async Task VerifyWorkstationConfigurationWriteScopeAsync()
{
    using var handler = new RecordingHttpMessageHandler();
    handler.EnqueueJson("{\"data\":[]}");
    handler.EnqueueJson("{}");
    handler.EnqueueJson("{}");
    using var httpClient = new HttpClient(handler);
    var service = new KanbanizeWorkstationConfigurationService(httpClient, "test-only-key");

    await service.SaveFieldsAsync(
        710,
        new[]
        {
            new ViCoConfigurationField("USER", "zkds-simulation-p01", 711),
            new ViCoConfigurationField("SONSTIGES", "neu anlegen", 0)
        });

    Assert(handler.Requests.Count == 3,
        "Missing IDs must first be checked, then an existing subtask patched and a genuinely missing one created.");
    Assert(handler.Requests[0].Method == HttpMethod.Get &&
           handler.Requests[0].RelativeUrl == "/api/v2/cards/710/subtasks",
        "The idempotency check must read the current card-level subtasks before creating a missing key.");
    var request = handler.Requests[1];
    Assert(request.Method == HttpMethod.Patch &&
           request.RelativeUrl == "/api/v2/cards/710/subtasks/711" &&
           request.ApiKey == "test-only-key",
        "The configuration editor must PATCH exactly its selected subtask with the configured API key.");
    using var payload = JsonDocument.Parse(request.Body);
    var fields = payload.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    Assert(fields.SequenceEqual(new[] { "description" }, StringComparer.Ordinal) &&
           payload.RootElement.GetProperty("description").GetString() == "USER: zkds-simulation-p01",
        "The configuration editor must update only the existing subtask description.");
    var createRequest = handler.Requests[2];
    Assert(createRequest.Method == HttpMethod.Post &&
           createRequest.RelativeUrl == "/api/v2/cards/710/subtasks",
        "A missing standardized configuration subtask must use the card-level subtasks endpoint.");
    using var createPayload = JsonDocument.Parse(createRequest.Body);
    Assert(createPayload.RootElement.GetProperty("description").GetString() == "SONSTIGES: neu anlegen",
        "The missing standard subtask description is incorrect.");

    using var staleHandler = new RecordingHttpMessageHandler();
    staleHandler.EnqueueJson(
        "{\"data\":{\"subtask_details\":{\"799\":{\"title\":{\"text\":\"SONSTIGES: bereits vorhanden\"}}}}}");
    staleHandler.EnqueueJson("{}");
    using var staleClient = new HttpClient(staleHandler);
    var staleService = new KanbanizeWorkstationConfigurationService(staleClient, "test-only-key");
    await staleService.SaveFieldsAsync(
        710,
        new[] { new ViCoConfigurationField("SONSTIGES", "aktualisiert", 0) });
    Assert(staleHandler.Requests[1].Method == HttpMethod.Patch &&
           staleHandler.Requests[1].RelativeUrl == "/api/v2/cards/710/subtasks/799",
        "A stale local ID must not create a duplicate subtask when the key already exists remotely.");

    using var existingCardHandler = new RecordingHttpMessageHandler();
    existingCardHandler.EnqueueJson("{\"data\":{\"data\":[{\"card_id\":720,\"title\":\"Arbeitsplatz KONFIGURATION\"}]}}");
    existingCardHandler.EnqueueJson("{\"data\":[{\"card_id\":801,\"description\":\"USER: alt\"}]}");
    existingCardHandler.EnqueueJson("{}");
    using var existingCardClient = new HttpClient(existingCardHandler);
    var existingCardService = new KanbanizeWorkstationConfigurationService(existingCardClient, "test-only-key");
    var resolvedCardId = await existingCardService.CreateStandardAsync(
        28125,
        29373,
        new[] { new ViCoConfigurationField("USER", "neu", 0) });
    Assert(resolvedCardId == 720 &&
           existingCardHandler.Requests.All(request =>
               request.Method != HttpMethod.Post || request.RelativeUrl != "/api/v2/cards") &&
           existingCardHandler.Requests[^1].RelativeUrl == "/api/v2/cards/720/subtasks/801",
        "Create must reuse and update a live KONFIGURATION card instead of creating a duplicate.");
}

static async Task VerifyKanbanizeRefreshApiAsync(string temporaryRoot)
{
    var cacheRoot = Path.Combine(temporaryRoot, "kanbanize-refresh");
    using var handler = new KanbanizeRefreshHttpMessageHandler();
    using var client = new HttpClient(handler);
    await new KanbanizeRefreshService(client, "test-only-key", cacheRoot).RefreshAsync();

    Assert(handler.Requests.Any(url =>
               url.StartsWith("/api/v2/cards?board_ids=1541", StringComparison.Ordinal) &&
               !url.Contains("fields=", StringComparison.OrdinalIgnoreCase)),
        "The workstation card query must retain lane/column data by avoiding the tenant-incompatible fields reduction.");
    Assert(handler.Requests.Any(url => url.Contains("expand=custom_fields", StringComparison.OrdinalIgnoreCase)) &&
           handler.Requests.Contains("/api/v2/cards/501/subtasks", StringComparer.Ordinal),
        "The workstation query must load project start fields while the authoritative card-level endpoint remains responsible for KONFIGURATION subtasks.");

    using var cache = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Combine(cacheRoot, "WorkstationBoardCache.json")));
    var cards = cache.RootElement.GetProperty("cards");
    Assert(cards.GetArrayLength() == 3,
        "All cards returned for the workstation lane must be retained in the structured cache.");
    var configuration = cards.EnumerateArray().Single(card => card.GetProperty("id").GetInt32() == 501);
    Assert(configuration.GetProperty("subtasks").GetArrayLength() == 2 &&
           configuration.GetProperty("subtasks").EnumerateArray().Any(subtask =>
               subtask.GetProperty("description").GetString() == "SW: TIA V20"),
        "Nested/dictionary KONFIGURATION subtasks from the direct card endpoint were not cached.");
    var project = cards.EnumerateArray().Single(card => card.GetProperty("id").GetInt32() == 502);
    Assert(project.GetProperty("startDate").GetDateTimeOffset().Day == 1 &&
           project.GetProperty("deadline").GetDateTimeOffset().Day == 30,
        "Kanbanize project dates were not retained in the structured workstation cache.");
    var detailProject = cards.EnumerateArray().Single(card => card.GetProperty("id").GetInt32() == 503);
    Assert(detailProject.GetProperty("deadline").GetDateTimeOffset().Day == 15 &&
           detailProject.GetProperty("laneId").GetString() == "28125" &&
           handler.Requests.Contains("/api/v2/cards/503", StringComparer.Ordinal) &&
           handler.Requests.Contains("/api/v2/cards/503?fields=card_id,deadline", StringComparer.Ordinal),
        "A position/deadline omitted by the list endpoint must be recovered from the card detail endpoints.");
}

static async Task VerifyKanbanizeInvalidRefreshProtectionAsync(string temporaryRoot)
{
    var cacheRoot = Path.Combine(temporaryRoot, "kanbanize-invalid-refresh");
    Directory.CreateDirectory(cacheRoot);
    var lanesPath = Path.Combine(cacheRoot, "AllPCLaneInfosWithChilds.txt");
    var cardsPath = Path.Combine(cacheRoot, "AllCardsOfPCsV2.txt");
    await File.WriteAllLinesAsync(lanesPath, new[] { "old-lane", "GM11111 Tool PC" });
    await File.WriteAllLinesAsync(cardsPath, new[] { "#Working#GM1000/01-001", "old-lane" });

    using var handler = new KanbanizeRefreshHttpMessageHandler(emptyWorkstationCards: true);
    using var client = new HttpClient(handler);
    var rejected = false;
    try
    {
        await new KanbanizeRefreshService(client, "test-only-key", cacheRoot).RefreshAsync();
    }
    catch (InvalidDataException exception)
    {
        rejected = exception.Message.Contains("nicht überschrieben", StringComparison.OrdinalIgnoreCase);
    }

    Assert(rejected, "An empty/unjoinable Businessmap response must be rejected before replacing the ViCo cache.");
    Assert((await File.ReadAllLinesAsync(lanesPath)).SequenceEqual(new[] { "old-lane", "GM11111 Tool PC" }) &&
           (await File.ReadAllLinesAsync(cardsPath)).SequenceEqual(new[] { "#Working#GM1000/01-001", "old-lane" }),
        "A rejected Kanbanize refresh replaced a previously usable cache.");
}

static async Task VerifyAdministrationIdentityAsync()
{
    var roles = new MemoryRoleStore(
        new ViCoUserRole(@"grob\lutzma", "Level9", "memory"),
        new ViCoUserRole(@"grob\user", "Level9", "memory"));
    var viewModel = new VIBN_Tools.Application.VM.ViCoAdministrationPageVM(
        roles,
        new EmptyMeetingService(),
        new EmptyUpdateService(),
        new NoOpPathLauncher(),
        "user");
    await viewModel.InitializeAsync();

    Assert(viewModel.CurrentLevel == "Level9" && viewModel.CanManageUsers,
        "A domain-qualified Level9 role should enable role administration for the short Windows user.");
    Assert(viewModel.RoleEntries.Any(role =>
            WindowsUserIdentity.Equals(role.UserName, "lutzma") &&
            string.Equals(role.Level, "Level9", StringComparison.OrdinalIgnoreCase)),
        "The mandatory lutzma Level9 role must be present in the administration view.");
}

static async Task VerifyTiaLibraryWorkflowAsync(string temporaryRoot)
{
    var library = Path.Combine(temporaryRoot, "library");
    var programFolder = Path.Combine(library, "_Programm", "VICOBIB", "Nested");
    var typeFolder = Path.Combine(library, "_Datatype", "VICOBIB");
    Directory.CreateDirectory(programFolder);
    Directory.CreateDirectory(typeFolder);
    await File.WriteAllTextAsync(Path.Combine(programFolder, "FB.xml"), "block");
    await File.WriteAllTextAsync(Path.Combine(programFolder, "FB_IDB.xml"), "idb");
    await File.WriteAllTextAsync(Path.Combine(typeFolder, "Type.xml"), "type");

    var client = new FakeTiaBridgeClient();
    var service = new TiaLibraryService(client);
    await service.ImportAsync(library, configureAxes: true, "V18");

    Assert(client.Saved, "TIA library import should save the project.");
    Assert(client.ConfiguredAxisIds.SequenceEqual(new[] { "Technology/AxisX" }),
        "TIA library import should explicitly configure all axes discovered by the read-only command.");
    Assert(client.ImportedBlocks.Count == 4, "TIA block and generated axis imports are incomplete.");
    Assert(client.ImportedBlocks[^1].File.EndsWith("FB_IDB.xml", StringComparison.OrdinalIgnoreCase),
        "TIA instance DB should be imported last.");
    Assert(client.ImportedDataTypes.Count == 1, "TIA data type import is incomplete.");

    client.Blocks.Items.Add(new TiaProgramItemInfo { Name = "FB", FolderPath = "VICOBIB/Nested" });
    client.DataTypes.Items.Add(new TiaProgramItemInfo { Name = "Type", FolderPath = "VICOBIB" });
    var exportPath = await service.ExportAsync("VICOBIB", Path.Combine(temporaryRoot, "export"), "V18");
    Assert(File.Exists(Path.Combine(exportPath, "_Programm", "VICOBIB", "Nested", "FB.xml")),
        "TIA block export structure is incorrect.");
    Assert(File.Exists(Path.Combine(exportPath, "_Datatype", "VICOBIB", "Type.xml")),
        "TIA data type export structure is incorrect.");
}

static void VerifyTiaAxisSelectionModel()
{
    var linearParameters = TiaAxisConfigurationPolicy.CreateParameterValues("AxisX");
    var rotaryParameters = TiaAxisConfigurationPolicy.CreateParameterValues("AxisA");
    Assert(linearParameters.Count == 10 &&
           linearParameters["_Properties.MotionType"] == 0 &&
           linearParameters["Sensor[1].MountingMode"] == 0 &&
           linearParameters["Simulation.Mode"] == 1 &&
           rotaryParameters["_Properties.MotionType"] == 1 &&
           rotaryParameters["Sensor[1].MountingMode"] == 1,
        "The documented TIA axis configuration values must remain complete and distinguish linear/rotary axes.");

    var row = new VIBN_Tools.Application.VM.TiaAxisSelectionRowVM(new TiaAxisInfo
    {
        Id = "Technology/Motion/AxisX",
        Name = "AxisX",
        TechnologyType = "PositioningAxis",
        GroupPath = "Technology/Motion"
    });

    Assert(row.IsSelected && row.Result == "Nur gelesen",
        "A discovered axis should be selected by default and clearly marked as read-only.");
    row.IsSelected = false;
    Assert(!row.IsSelected, "An axis must be individually deselectable before configuration.");

    row.ApplyConfigurationResult(new TiaAxisInfo
    {
        Id = row.Id,
        Name = row.Name,
        TechnologyType = row.TechnologyType,
        GroupPath = row.GroupPath,
        ParameterResults =
        [
            new TiaAxisParameterResult { Name = "Simulation.Mode", Value = "1", Success = true },
            new TiaAxisParameterResult { Name = "PositionControl.EnableDSC", Value = "0", Success = false, Error = "read-only" }
        ]
    });
    Assert(row.Result == "1 gesetzt, 1 fehlgeschlagen",
        "Axis configuration should expose exact success/failure counts in the UI model.");
}

static async Task VerifyTypedTiaPipeProtocolAsync()
{
    var pipeName = $"vibn-tia-test-{Guid.NewGuid():N}";
    var serverTask = Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        for (var requestIndex = 0; requestIndex < 5; requestIndex++)
        {
            var requestLine = await reader.ReadLineAsync();
            var request = JsonSerializer.Deserialize<TiaRequestEnvelope>(requestLine!);
            var expectedCommand = requestIndex switch
            {
                0 => TiaCommands.Ping,
                1 => TiaCommands.ListHardware,
                2 => TiaCommands.ListAxes,
                3 => TiaCommands.ConfigureAxes,
                _ => TiaCommands.Close
            };
            Assert(request?.Command == expectedCommand, $"Typed TIA pipe command '{expectedCommand}' was not received.");
            if (requestIndex == 3)
            {
                var configuration = JsonSerializer.Deserialize<TiaAxisConfigurationPayload>(request!.PayloadJson);
                Assert(configuration?.AxisIds.SequenceEqual(new[] { "Technology/Motion/AxisX" }) == true,
                    "The selected stable axis IDs must survive the typed pipe request.");
            }

            var response = new TiaResponseEnvelope
            {
                RequestId = request!.RequestId,
                Success = true,
                PayloadJson = requestIndex switch
                {
                    0 => JsonSerializer.Serialize("pong"),
                    1 => JsonSerializer.Serialize(new[]
                    {
                        new TiaHardwareModuleInfo
                        {
                            Slot = 2,
                            Subslot = 1,
                            DeviceName = "PLC test",
                            DeviceType = "ET 200 test",
                            Manufacturer = "Siemens",
                            OrderNumber = "6ES7 test",
                            GsdName = "GSDML-test.xml",
                            GsdType = "PROFINET IO",
                            ProfinetName = "test-device",
                            IpAddress = "192.168.0.10",
                            NetworkRole = "IO-Device",
                            ModuleName = "DI/DO test module",
                            ModulePath = "Head/Slot 2/DI-DO",
                            ModuleType = "Digital IO",
                            FirmwareVersion = "V1.0",
                            InputStartByte = 8,
                            InputLength = 12,
                            OutputStartByte = 12,
                            OutputLength = 6
                        }
                    }),
                    2 => JsonSerializer.Serialize(new[]
                    {
                        new TiaAxisInfo
                        {
                            Id = "Technology/Motion/AxisX",
                            Name = "AxisX",
                            GroupPath = "Technology/Motion",
                            TechnologyType = "PositioningAxis"
                        }
                    }),
                    3 => JsonSerializer.Serialize(new[]
                    {
                        new TiaAxisInfo
                        {
                            Id = "Technology/Motion/AxisX",
                            Name = "AxisX",
                            GroupPath = "Technology/Motion",
                            TechnologyType = "PositioningAxis",
                            ParameterResults =
                            [
                                new TiaAxisParameterResult { Name = "Simulation.Mode", Value = "1", Success = true }
                            ]
                        }
                    }),
                    _ => JsonSerializer.Serialize((object?)null)
                }
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
    });

    var options = new TiaBridgeClientOptions(
        pipeName,
        ConnectTimeout: TimeSpan.FromSeconds(5),
        RequestTimeout: TimeSpan.FromSeconds(5));
    var client = new NamedPipeTiaBridgeClient(options);
    try
    {
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Assert(await client.PingAsync(), "Typed TIA pipe response failed.");
        var hardware = await client.ListHardwareAsync();
        Assert(hardware.Count == 1 && hardware[0].InputStartByte == 8 && hardware[0].OutputStartByte == 12 &&
               hardware[0].DeviceName == "PLC test" && hardware[0].ModuleType == "Digital IO" &&
               hardware[0].DeviceType == "ET 200 test" && hardware[0].Manufacturer == "Siemens" &&
               hardware[0].OrderNumber == "6ES7 test" && hardware[0].GsdName == "GSDML-test.xml" &&
               hardware[0].ProfinetName == "test-device" && hardware[0].IpAddress == "192.168.0.10" &&
               hardware[0].Slot == 2 && hardware[0].Subslot == 1 &&
               hardware[0].ModulePath == "Head/Slot 2/DI-DO" &&
               hardware[0].FirmwareVersion == "V1.0" &&
               hardware[0].InputAddressRange == "8–19" && hardware[0].OutputAddressRange == "12–17",
            "TIA hardware configuration must survive the typed pipe boundary.");
        var axes = await client.ListAxesAsync();
        Assert(axes.Count == 1 && axes[0].Id == "Technology/Motion/AxisX",
            "The read-only TIA axis list must survive the typed pipe boundary.");
        var configuredAxes = await client.ConfigureAxesAsync(new[] { axes[0].Id });
        Assert(configuredAxes.Count == 1 && configuredAxes[0].ParameterResults.Single().Success,
            "Selective TIA axis configuration results must survive the typed pipe boundary.");
    }
    finally
    {
        await client.DisposeAsync();
    }

    await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
}

static async Task VerifyTypedTiaPipeTimeoutDiagnosticAsync()
{
    var pipeName = $"vibn-tia-timeout-test-{Guid.NewGuid():N}";
    var serverTask = Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    });

    var client = new NamedPipeTiaBridgeClient(new TiaBridgeClientOptions(
        pipeName,
        ConnectTimeout: TimeSpan.FromSeconds(5),
        RequestTimeout: TimeSpan.FromMilliseconds(100)));
    try
    {
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(6));
        try
        {
            _ = await client.PingAsync();
            throw new InvalidOperationException("A silent TIA bridge must time out.");
        }
        catch (TimeoutException exception)
        {
            Assert(exception.Message.Contains(TiaCommands.Ping, StringComparison.Ordinal) &&
                   exception.Message.Contains("Openness-Freigabedialog", StringComparison.Ordinal),
                "TIA request timeout must name the blocked command and the likely Openness dialog.");
        }
    }
    finally
    {
        await client.DisposeAsync();
    }

    await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class SnapshotCatalog(params ViCoWorkstation[] workstations) : IViCoWorkstationCatalog
{
    public Task<ViCoWorkstationSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ViCoWorkstationSnapshot(workstations, Array.Empty<string>()));
}

sealed record CapturedHttpRequest(HttpMethod Method, string RelativeUrl, string ApiKey, string Body);

/// <summary>
/// In-memory HTTP boundary for payload tests. It ensures the Kanbanize adapter
/// can be verified without any network call or mutation of a real board.
/// </summary>
sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<CapturedHttpRequest> Requests { get; } = new();

    public void EnqueueJson(string json) =>
        _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var apiKey = request.Headers.TryGetValues("apikey", out var values)
            ? values.SingleOrDefault() ?? string.Empty
            : string.Empty;
        Requests.Add(new CapturedHttpRequest(
            request.Method,
            request.RequestUri?.PathAndQuery ?? string.Empty,
            apiKey,
            body));

        if (_responses.Count == 0)
            throw new InvalidOperationException("No mocked Kanbanize response was provided.");
        return _responses.Dequeue();
    }
}

sealed class KanbanizeRefreshHttpMessageHandler(bool emptyWorkstationCards = false) : HttpMessageHandler
{
    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri?.PathAndQuery ?? string.Empty;
        Requests.Add(url);
        var json = url switch
        {
            "/api/v2/boards/1541/lanes" => "{\"data\":[{\"lane_id\":28125,\"name\":\"GM12345 Tool PC\"}]}",
            var value when value.StartsWith("/api/v2/cards?board_ids=1541", StringComparison.Ordinal) =>
                emptyWorkstationCards
                    ? "{\"data\":{\"data\":[],\"pagination\":{\"all_pages\":1}}}"
                    : "{\"data\":{\"data\":[{\"card_id\":501,\"lane_id\":28125,\"column_id\":29373,\"title\":\"Arbeitsplatz KONFIGURATION\",\"subtasks\":[{\"card_id\":601,\"description\":\"STANDORT: Werk 1\"}]},{\"card_id\":502,\"lane_id\":28125,\"column_id\":29375,\"title\":\"GM9000/01-001\",\"custom_fields\":[{\"field_id\":508,\"value\":\"2026-09-01T00:00:00Z\"}],\"deadline\":\"2026-09-30T00:00:00Z\"},{\"card_id\":503,\"title\":\"GM9000/01-002\",\"custom_fields\":[{\"field_id\":508,\"value\":\"2026-09-02T00:00:00Z\"}]}],\"pagination\":{\"all_pages\":1}}}",
            "/api/v2/cards/503" =>
                "{\"data\":{\"card_id\":503,\"title\":\"GM9000/01-002\",\"current_position\":{\"lane_id\":28125,\"column_id\":29374}}}",
            "/api/v2/cards/503?fields=card_id,deadline" =>
                "{\"data\":{\"card_id\":503,\"deadline\":{\"value\":\"2026-10-15T00:00:00Z\"}}}",
            "/api/v2/cards/501/subtasks" =>
                "{\"data\":{\"subtasks\":{\"601\":{\"subtask_id\":601,\"description\":\"STANDORT: Werk 1\"},\"602\":{\"description\":{\"text\":\"SW: TIA V20\"}}}}}",
            var value when value.StartsWith("/api/v2/cards?board_ids=846", StringComparison.Ordinal) =>
                "{\"data\":{\"data\":[],\"pagination\":{\"all_pages\":1}}}",
            var value when value.StartsWith("/api/v2/boards/846/columns", StringComparison.Ordinal) => "{\"data\":[]}",
            _ => throw new InvalidOperationException($"Unexpected Kanbanize refresh request: {url}")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

sealed record ScheduleChange(int CardId, DateTimeOffset StartDate, DateTimeOffset EndDate);

sealed record TitleChange(int CardId, string Title);

sealed class MemoryKanbanizeCardService : IKanbanizeCardService
{
    private readonly List<KanbanizeCardInfo> _sourceCards;
    private readonly List<KanbanizeCardInfo> _targetCards;
    private int _nextCardId = 9000;

    public MemoryKanbanizeCardService(
        IEnumerable<KanbanizeCardInfo> sourceCards,
        IEnumerable<KanbanizeCardInfo> targetCards)
    {
        _sourceCards = sourceCards
            .Select(card => card.WorkflowId == 0 ? card with { WorkflowId = 42 } : card)
            .ToList();
        _targetCards = targetCards.ToList();
    }

    public bool IsConfigured => true;

    public List<KanbanizeGeneratedCardDraft> GeneratedCards { get; } = new();

    public List<ScheduleChange> ScheduleChanges { get; } = new();

    public List<TitleChange> TitleChanges { get; } = new();

    public Task<IReadOnlyList<KanbanizeBoardInfo>> LoadBoardsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KanbanizeBoardInfo>>(Array.Empty<KanbanizeBoardInfo>());

    public Task<KanbanizeBoardStructure> LoadBoardStructureAsync(int boardId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new KanbanizeBoardStructure(
            Array.Empty<KanbanizeLaneInfo>(),
            Array.Empty<KanbanizeColumnInfo>(),
            [new KanbanizeWorkflowInfo(42, VibnWorkplaceSynchronizationPolicy.RequiredSourceWorkflowName)]));

    public Task<IReadOnlyList<KanbanizeCardInfo>> LoadCardsAsync(int boardId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KanbanizeCardInfo>>(
            (boardId == 1392 ? _sourceCards : _targetCards).ToArray());

    public Task<KanbanizeCreatedCard> CreateCardAsync(KanbanizeCardDraft draft, CancellationToken cancellationToken = default) =>
        Task.FromResult(new KanbanizeCreatedCard(++_nextCardId, draft.Title));

    public Task<KanbanizeCreatedCard> CreateGeneratedCardAsync(
        KanbanizeGeneratedCardDraft draft,
        CancellationToken cancellationToken = default)
    {
        GeneratedCards.Add(draft);
        var created = new KanbanizeCardInfo(
            ++_nextCardId,
            1541,
            draft.TargetLaneId,
            draft.TargetColumnId,
            draft.Title,
            draft.SourceCardId.ToString(),
            draft.Deadline,
            draft.StartDate);
        _targetCards.Add(created);
        return Task.FromResult(new KanbanizeCreatedCard(created.Id, created.Title));
    }

    public Task UpdateDeadlineAsync(int cardId, DateTimeOffset? deadline, CancellationToken cancellationToken = default)
    {
        var index = _targetCards.FindIndex(card => card.Id == cardId);
        if (index < 0)
            throw new InvalidOperationException("Target card not found.");
        _targetCards[index] = _targetCards[index] with { Deadline = deadline };
        return Task.CompletedTask;
    }

    public Task UpdateGeneratedScheduleAsync(
        int cardId,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var index = _targetCards.FindIndex(card => card.Id == cardId);
        if (index < 0)
            throw new InvalidOperationException("Target card not found.");

        _targetCards[index] = _targetCards[index] with
        {
            StartDate = startDate,
            Deadline = endDate
        };
        ScheduleChanges.Add(new ScheduleChange(cardId, startDate, endDate));
        return Task.CompletedTask;
    }

    public Task UpdateGeneratedTitleAsync(
        int cardId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var index = _targetCards.FindIndex(card => card.Id == cardId);
        if (index < 0)
            throw new InvalidOperationException("Target card not found.");
        _targetCards[index] = _targetCards[index] with { Title = title };
        TitleChanges.Add(new TitleChange(cardId, title));
        return Task.CompletedTask;
    }
}

sealed class MemoryRoleStore : IViCoUserRoleStore
{
    private IReadOnlyList<ViCoUserRole> _roles;

    public MemoryRoleStore(params ViCoUserRole[] roles)
    {
        _roles = roles;
    }

    public bool IsConfigured => true;

    public Task<IReadOnlyList<ViCoUserRole>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles);

    public Task SaveAsync(IReadOnlyCollection<ViCoUserRole> roles, CancellationToken cancellationToken = default)
    {
        var plan = ViCoRolePolicy.PlanSave(roles);
        if (!plan.IsValid)
            throw new InvalidOperationException(plan.Message);
        _roles = plan.Roles;
        return Task.CompletedTask;
    }
}

sealed class EmptyMeetingService : IUpcomingMeetingService
{
    public Task<IReadOnlyList<UpcomingMeeting>> LoadTodayAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UpcomingMeeting>>(Array.Empty<UpcomingMeeting>());
}

sealed class EmptyUpdateService : IViCoUpdateService
{
    public Task<ViCoUpdateInfo?> FindLatestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ViCoUpdateInfo?>(null);
}

sealed class NoOpPathLauncher : IExternalPathLauncher
{
    public void Open(string path)
    {
    }
}

sealed class MemoryUserSecretStore : IUserSecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public bool FailWrites { get; init; }

    public string? Read(string targetName) => Values.GetValueOrDefault(targetName);

    public void Write(string targetName, string secret)
    {
        if (FailWrites)
            throw new System.ComponentModel.Win32Exception(1312);
        Values[targetName] = secret;
    }

    public void Delete(string targetName) => Values.Remove(targetName);
}

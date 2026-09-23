using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VIBN_Tools.Application;
using VIBN_Tools.Application.View;
using VIBN_Tools.Application.VM;
using VIBN_Tools.Core.Kanbanize;
using VIBN_Tools.Core.ViCo;
using VIBN_Tools.ContainerGeneration.AI;
using VIBN_Tools.ContainerGeneration.Utils;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.Settings;
using VIBN_Tools.SharedWpf;
using VIBN_Tools.SpecialDevices;
using VIBN_Tools.Tia.Contracts;

namespace VIBN_Tools.UiStartup.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        _ = new System.Windows.Application();
        Services.Initialize();
        var bindingTrace = PresentationTraceSources.DataBindingSource;
        var bindingErrors = new BindingErrorTraceListener();
        bindingTrace.Switch.Level = SourceLevels.Error;
        bindingTrace.Listeners.Add(bindingErrors);

        try
        {
            if (NLog.LogManager.Configuration?.FindTargetByName<CustomLoggerTarget>("CustomLog") is null)
                throw new InvalidOperationException("ContainerGeneration custom NLog target was not loaded from its feature assembly.");

            var workspacePage = new ViCoWorkspacePage();
            ExerciseDeferredTemplates(workspacePage);
            var feeVersionInfo = new FeeVersionInfoProvider().Read();
            if (string.Equals(feeVersionInfo.UsedSdkVersion, "Nicht erkannt", StringComparison.Ordinal))
                throw new InvalidOperationException("The FEE SDK used by the running build must be visible in Project Settings.");
            VerifyInstalledFeeVersionRequiresSdk();
            VerifyAutomationInstallationDiscovery();
            VerifyPasswordBoxBinding();
            VerifyNavigationPreferencePersistence();
            VerifyConfigurationFieldAcceptsCreatedSubtask();
            VerifyExistingSignalReuseDoesNotCallUpdate();
            VerifySignalResolutionPlanner();
            var projectPage = new ViCoPage();
            var projectViewModel = (ViCoPageVM)projectPage.DataContext;
            projectViewModel.Projects.Add(new ProjectLocation("GM1234/05-130", @"C:\Projects\GM1234\05-130"));

            var searchPage = new ViCoSearchPage();
            var searchViewModel = (ViCoSearchPageVM)searchPage.DataContext;
            var workstation = new ViCoWorkstation(
                "GM12345 Tool PC",
                "GM12345",
                "zkds-simulation-p01",
                "TIA Portal V19 | Beckhoff TwinCAT 3",
                "FEE 5",
                "LAN Industrial",
                new[] { "[W] GM1234/05-130 Demo" },
                new[] { "[W] GM1234/05-130 Demo", "TIA Portal V19", "Beckhoff TwinCAT 3", "Robot: R01 – In Arbeit" },
                new[]
                {
                    new AutomationSoftwareInfo(AutomationPlatform.SiemensTiaPortal, "TIA Portal V19", "TIA Portal V19"),
                    new AutomationSoftwareInfo(AutomationPlatform.BeckhoffTwinCat, "Beckhoff TwinCAT 3", "Beckhoff TwinCAT 3")
                },
                new[] { new ViCoRobotInfo("R01", "In Arbeit", "Robot card") },
                new ViCoWorkstationConfiguration(
                    710,
                    new ViCoConfigurationField("USER", "zkds-simulation-p01", 711),
                    new ViCoConfigurationField("STANDORT", "Werk 2", 712),
                    new ViCoConfigurationField("SW", "TIA V19 / Beckhoff TwinCAT 3", 713),
                    new ViCoConfigurationField("PROJEKT-IP", "10.20.30.40", 714),
                    new ViCoConfigurationField("SONSTIGES", "Testdaten für die Anleitung", 715)),
                ProjectCards: new[]
                {
                    new ViCoProjectCardInfo(
                        901,
                        "GM1234/05-130 Demo",
                        "In Arbeit",
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero))
                });
            var workstationRow = new ViCoWorkstationRowVM(workstation);
            if (workstationRow.WorkingStartSummary != "01.08.2026" ||
                workstationRow.WorkingEndSummary != "30.09.2026" ||
                workstationRow.WorkingStartSummary.Contains("Karte", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ViCo project date columns must show only the resolved dates.");
            }
            if (ExportFileNamePolicy.Create("A/B", "fallback") == "A/B" ||
                ExportFileNamePolicy.Create("", "fallback") != "fallback")
            {
                throw new InvalidOperationException("Shared export file-name policy is not applied consistently.");
            }
            workstationRow.SetOnline(true);
            workstationRow.SetRemoteSession(new ViCoRemoteSessionInfo(
                true,
                "grob\\operator",
                "grob\\operator",
                new DateTimeOffset(2026, 8, 25, 8, 30, 0, TimeSpan.Zero)));
            searchViewModel.Results.Add(workstationRow);
            searchViewModel.SelectedWorkstation = workstationRow;
            if (searchViewModel.SelectedProjectStart != "01.08.2026" ||
                searchViewModel.SelectedProjectEnd != "30.09.2026")
            {
                throw new InvalidOperationException("ViCo project dates must be displayed without a time component.");
            }

            var administrationPage = new ViCoAdministrationPage();
            var administrationViewModel = (ViCoAdministrationPageVM)administrationPage.DataContext;
            administrationViewModel.RoleEntries.Add(new ViCoUserRole(@"grob\user", "Level9", "test"));

            var containerGenerationPage = new ContainerGenerationPage();
            var containerGenerationViewModel =
                (ContainerGenerationPageVM)containerGenerationPage.DataContext;
            if (containerGenerationViewModel.CanLoadData)
                throw new InvalidOperationException("Load Data must require a loaded Requirements XML.");
            if (containerGenerationViewModel.CanCompareContainerFile)
                throw new InvalidOperationException("ContainerFile comparison must require an active workspace.");

            var specialDevicePage = new SpecialDevicePage();
            var specialDeviceViewModel = (SpecialDevicePageVM)specialDevicePage.DataContext;
            specialDeviceViewModel.SelectedManufacturer = DeviceCatalog.DeviceManufacturer.Keyence;
            specialDeviceViewModel.SelectedDevice = DeviceCatalog.KeyenceDeviceTypes.SR2000;
            specialDeviceViewModel.DevicePrefix = "SCN_TEST";
            specialDeviceViewModel.DeviceAddressInput = 12;
            specialDeviceViewModel.DeviceAddressOutput = 34;
            specialDeviceViewModel.AddSpecialDeviceCommand.Execute(null);
            var queuedManualDevice = specialDeviceViewModel.SpecialDevices.Single();
            if (queuedManualDevice.DeviceAddresses.Input != 12 || queuedManualDevice.DeviceAddresses.Output != 34)
                throw new InvalidOperationException("Manual Special Device E-/A-byte values were swapped.");
            queuedManualDevice.QueueInputByte = 56;
            queuedManualDevice.QueueOutputByte = 78;
            queuedManualDevice.QueuePrefix = "SCN_CHANGED";
            if (queuedManualDevice.DeviceAddresses.Input != 56 || queuedManualDevice.DeviceAddresses.Output != 78 ||
                queuedManualDevice.DevicePrefix != "SCN_CHANGED")
            {
                throw new InvalidOperationException("Editable Special Device queue values were not applied to the device model.");
            }
            specialDeviceViewModel.DeleteAllDevicesCommand.Execute(null);
            var hardwareWithoutLogic = new TiaHardwareDeviceRowVM(
                new TiaHardwareModuleInfo
                {
                    Slot = 3,
                    DeviceName = "PLC_1",
                    ModuleName = "Cognex Testmodul",
                    ModuleType = "PROFINET IO device",
                    TypeIdentifier = "TEST-COGNEX",
                    FirmwareVersion = "V2.1",
                    InputStartByte = 20,
                    InputLength = 4,
                    OutputStartByte = 40,
                    OutputLength = 4
                });
            hardwareWithoutLogic.Include = true;
            hardwareWithoutLogic.SelectedLogic = SpecialDeviceLogicOption.None;
            if (hardwareWithoutLogic.TryCreate(out _, out var emptyLogicError) || emptyLogicError.Length != 0)
                throw new InvalidOperationException("A selected TIA hardware row without logic must be skipped intentionally.");
            specialDeviceViewModel.TiaHardwareRows.Add(hardwareWithoutLogic);
            specialDeviceViewModel.AddSelectedHardwareDevicesCommand.Execute(null);
            if (specialDeviceViewModel.SpecialDevices.Count != 0 ||
                !specialDeviceViewModel.StatusText.Contains("bewusst übersprungen", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A checked TIA hardware row without logic entered the Special Device queue.");
            }
            VerifyTiaHardwareMappingPersistence();

            var visualPlanService = VerifyContainerToFeeVisualPlan();
            var visualContainerViewModel = new ContainerToFeeVisualPageVM(visualPlanService);
            visualContainerViewModel.SelectedTreeNode = visualContainerViewModel.TreeRoots
                .SelectMany(root => root.SelfAndDescendants())
                .First(node => node.Kind == VisualNodeKind.Container);
            if (visualContainerViewModel.SelectedTreeNode.StateBackground != "#FFEF9A9A" ||
                visualContainerViewModel.AvailableFeeInterfaces.All(item => !item.IsNone))
            {
                throw new InvalidOperationException(
                    "Visual container status or explicit no-interface selection is incorrect.");
            }
            visualContainerViewModel.CollapseAllCommand.Execute(null);
            if (visualContainerViewModel.TreeRoots
                .SelectMany(root => root.SelfAndDescendants())
                .Any(node => node.IsExpanded))
            {
                throw new InvalidOperationException("Visual collapse-all command left expanded nodes.");
            }
            visualContainerViewModel.ExpandAllCommand.Execute(null);
            if (visualContainerViewModel.TreeRoots
                .SelectMany(root => root.SelfAndDescendants())
                .Any(node => !node.IsExpanded))
            {
                throw new InvalidOperationException("Visual expand-all command left collapsed nodes.");
            }
            var visualContainerPage = new ContainerToFeeVisualPage
            {
                DataContext = visualContainerViewModel
            };
            var fee2ContainerPage = new Fee2ContainerPage();
            var fee2ContainerViewModel = (Fee2ContainerPageVM)fee2ContainerPage.DataContext;
            if (fee2ContainerViewModel.CanExport ||
                string.IsNullOrWhiteSpace(fee2ContainerViewModel.ExportUnavailableReason))
            {
                throw new InvalidOperationException(
                    "FEE2Container export must explain why no root can be exported.");
            }
            var fee2SpecialDevicesPage = new Fee2SpecialDevicesPage();
            var fee2SpecialDevicesViewModel =
                (Fee2SpecialDevicesPageVM)fee2SpecialDevicesPage.DataContext;
            if (fee2SpecialDevicesViewModel.CanExport ||
                string.IsNullOrWhiteSpace(fee2SpecialDevicesViewModel.ExportUnavailableReason))
            {
                throw new InvalidOperationException(
                    "FEE2SpecialDevices export must explain why no root can be exported.");
            }
            var aiTrainingPage = new AITrainingTestPage();
            var aiTrainingViewModel = (AITrainingTestPageVM)aiTrainingPage.DataContext;
            aiTrainingViewModel.RuleSuggestions.Add(new RuleSuggestion(
                "test-rule",
                "Testregel für WPF-Bindings",
                "Cylinder",
                "Ready",
                "Slot",
                "PLC_IN_Old",
                "PLC_IN_New",
                2,
                3,
                2d / 3d,
                RuleSuggestionStatus.Pending));

            var kanbanizeCardPage = new KanbanizeCardPage();
            var kanbanizeViewModel = (KanbanizeCardPageVM)kanbanizeCardPage.DataContext;
            if (!Uri.TryCreate(KanbanizeCardPageVM.PlanViewUrl, UriKind.Absolute, out var planUri) ||
                planUri.Scheme != Uri.UriSchemeHttps || planUri.AbsolutePath != "/ctrl_plan/1541/")
            {
                throw new InvalidOperationException("Kanbanize plan-view URL is invalid.");
            }
            // Populate the deferred DataGrid template as well: this catches
            // bindings in the coloured synchronization preview before release.
            var sourceCard = new KanbanizeCardInfo(
                4711,
                1392,
                1,
                2,
                "[VIBN] Grundinbetriebnahme UI-Prüfung",
                null,
                DateTimeOffset.UtcNow);
            var schedule = new VibnWorkplaceSchedule(
                DateTimeOffset.UtcNow.AddDays(-14),
                DateTimeOffset.UtcNow.AddDays(56));
            var createRow = new VibnWorkplaceSynchronizationRowVM(
                new VibnWorkplaceSynchronizationItem(
                    VibnWorkplaceSynchronizationAction.Create,
                    sourceCard,
                    null,
                    "UI-Prüfdatensatz ohne externen Schreibzugriff.",
                    schedule));
            var deadlineRow = new VibnWorkplaceSynchronizationRowVM(
                new VibnWorkplaceSynchronizationItem(
                    VibnWorkplaceSynchronizationAction.UpdateDeadline,
                    sourceCard with { Id = 4712 },
                    sourceCard with { Id = 5712, BoardId = 1541 },
                    "UI-Prüfung einer vorhandenen Karte.",
                    schedule));
            var relatedTargets = new[]
            {
                sourceCard with { Id = 6001, BoardId = 1541, CustomId = "4711", Title = "UI-Prüfung *[Gen]* CORE" },
                sourceCard with { Id = 6002, BoardId = 1541, CustomId = "4711", Title = "UI-Prüfung - CLIENT" }
            };
            var relatedRow = new VibnWorkplaceSynchronizationRowVM(
                new VibnWorkplaceSynchronizationItem(
                    VibnWorkplaceSynchronizationAction.RelatedCards,
                    sourceCard,
                    relatedTargets[0],
                    "Zwei Rollen gefunden.",
                    schedule,
                    relatedTargets));
            if (!createRow.IsSelected || deadlineRow.IsSelected)
                throw new InvalidOperationException("Only new Kanbanize cards must be selected by default.");
            if (createRow.SourceDeadline.Contains(':') ||
                relatedRow.ActionText != "2 Karten gefunden" ||
                relatedRow.ClientCount != 1 || relatedRow.CoreCount != 1 ||
                relatedRow.ActionBackground != "#FF70AD47")
            {
                throw new InvalidOperationException("Kanbanize date or structured role presentation is incorrect.");
            }

            kanbanizeViewModel.WorkplaceSynchronization.PreviewItems.Add(createRow);
            kanbanizeViewModel.WorkplaceSynchronization.PreviewItems.Add(deadlineRow);
            kanbanizeViewModel.WorkplaceSynchronization.PreviewItems.Add(relatedRow);
            kanbanizeViewModel.WorkplaceSynchronization.SelectAllCommand.Execute(null);
            if (kanbanizeViewModel.WorkplaceSynchronization.PreviewItems.Any(item => item.CanSynchronize && !item.IsSelected))
                throw new InvalidOperationException("Selecting all Kanbanize preview rows failed.");
            kanbanizeViewModel.WorkplaceSynchronization.DeselectAllCommand.Execute(null);
            if (kanbanizeViewModel.WorkplaceSynchronization.PreviewItems.Any(item => item.IsSelected))
                throw new InvalidOperationException("Deselecting all Kanbanize preview rows failed.");
            // Restore the documented initial state for the generated handbook preview.
            createRow.IsSelected = true;

            var tiaPortalPage = new TiaPortalPage();
            var tiaPortalViewModel = (TiaPortalPageVM)tiaPortalPage.DataContext;
            if (!tiaPortalViewModel.LibraryOperationInfo.Contains("überschrieben", StringComparison.OrdinalIgnoreCase) ||
                !tiaPortalViewModel.LibraryOperationInfo.Contains("automatisch gespeichert", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The TIA ViCo library help does not disclose its write and save effects.");
            }
            tiaPortalViewModel.ToggleLibraryOperationInfoCommand.Execute(null);
            if (!tiaPortalViewModel.IsLibraryOperationInfoVisible)
                throw new InvalidOperationException("The TIA ViCo library explanation cannot be expanded.");

            var settingsPage = new SettingsPage();
            var settingsViewModel = (SettingsPageVM)settingsPage.DataContext;
            var interfaceOperationPage = new InterfaceOperationPage();
            var interfaceOperationViewModel =
                (InterfaceOperationPageVM)interfaceOperationPage.DataContext;
            if (interfaceOperationViewModel.CanReloadFeeData)
                throw new InvalidOperationException("Interface reload must remain disabled before an explicit FEE connection.");
            var logEntriesBeforeInterfaceLoad = ApplicationLogService.Instance.Entries.Count;
            interfaceOperationPage.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            if (ApplicationLogService.Instance.Entries
                .Skip(logEntriesBeforeInterfaceLoad)
                .Any(entry => string.Equals(entry.Area, "Interface Operation", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Opening Interface Operation attempted a FEE operation before Connect.");
            }

            FrameworkElement[] integratedViews =
            [
                projectPage,
                searchPage,
                new ViCoCopyPage(),
                tiaPortalPage,
                administrationPage,
                kanbanizeCardPage,
                specialDevicePage,
                visualContainerPage,
                fee2ContainerPage,
                aiTrainingPage,
                settingsPage,
                interfaceOperationPage,
                new DiagnosticsPanel()
            ];

            foreach (var view in integratedViews)
            {
                if (view.DataContext is null)
                    throw new InvalidOperationException($"{view.GetType().Name} has no view model.");
                ExerciseDeferredTemplates(view);
            }

            // Manual form, queue and TIA hardware grid now share one page.
            // A populated row catches its ComboBox and converter bindings.
            ExerciseDeferredTemplates(specialDevicePage);

            if (Environment.GetEnvironmentVariable("VIBN_CAPTURE_UI_PREVIEW") == "1")
            {
                SavePreview(searchPage, Path.Combine(AppContext.BaseDirectory, "vico-search-preview.png"));
                SavePreview(projectPage, Path.Combine(AppContext.BaseDirectory, "vico-projects-preview.png"));
                SavePreview(workspacePage, Path.Combine(AppContext.BaseDirectory, "vico-workspace-preview.png"));
                SavePreview(kanbanizeCardPage, Path.Combine(AppContext.BaseDirectory, "kanbanize-cards-preview.png"));
                SavePreview(specialDevicePage, Path.Combine(AppContext.BaseDirectory, "special-devices-preview.png"));
                SavePreview(visualContainerPage, Path.Combine(AppContext.BaseDirectory, "container2fee-visual-preview.png"));
            }

            Dispatcher.CurrentDispatcher.Invoke(
                static () => { },
                DispatcherPriority.ContextIdle);
            PumpDispatcher(TimeSpan.FromMilliseconds(700));
            if (!string.Equals(settingsViewModel.SelectedServer, "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The asynchronous server refresh removed the local FEE target.");
            if (ApplicationLogService.Instance.Entries.Any(entry =>
                    entry.Details.Contains("CollectionView-Typ", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Project Settings modified its bound server list outside the UI dispatcher.");
            }

            if (bindingErrors.Messages.Count > 0)
            {
                throw new InvalidOperationException(
                    "WPF binding errors were detected:" + Environment.NewLine +
                    string.Join(Environment.NewLine, bindingErrors.Messages));
            }

            Console.WriteLine("All integrated WPF views initialized without binding errors.");
            return 0;
        }
        finally
        {
            bindingTrace.Listeners.Remove(bindingErrors);
        }
    }

    private static void ExerciseDeferredTemplates(FrameworkElement view)
    {
        var size = new Size(1600, 900);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();

        foreach (var dataGrid in FindVisualChildren<DataGrid>(view))
        {
            if (dataGrid.Items.Count == 0)
                continue;
            dataGrid.SelectedIndex = 0;
            dataGrid.ScrollIntoView(dataGrid.Items[0]);
            dataGrid.UpdateLayout();
        }

        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void VerifyTiaHardwareMappingPersistence()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vibn-tia-hardware-mapping-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonTiaHardwareMappingStore(path);
            var expected = new TiaHardwareMapping(
                "Device|pn-device|Module/Slot3|3|1",
                true,
                "SafeCoupler",
                62,
                70,
                "Grob",
                "SafePnPn",
                string.Empty);
            store.SaveAsync(new[] { expected }).GetAwaiter().GetResult();
            var restored = store.LoadAsync().GetAwaiter().GetResult();
            if (!restored.TryGetValue(expected.Key, out var actual) || actual != expected)
                throw new InvalidOperationException("Persisted TIA hardware mapping was not restored unchanged.");

            var legacyFirstArea = new TiaHardwareDeviceRowVM(new TiaHardwareModuleInfo
            {
                DeviceName = "Device",
                ProfinetName = "pn-device",
                ModulePath = "Module/Slot3",
                Slot = 3,
                Subslot = 1,
                AddressSetIndex = 0,
                InputStartByte = 62,
                InputLengthBits = 96,
                InputLength = 12,
                OutputStartByte = 62,
                OutputLengthBits = 48,
                OutputLength = 6,
            });
            var secondArea = new TiaHardwareDeviceRowVM(new TiaHardwareModuleInfo
            {
                DeviceName = "Device",
                ProfinetName = "pn-device",
                ModulePath = "Module/Slot3",
                Slot = 3,
                Subslot = 1,
                AddressSetIndex = 1,
                InputStartByte = 74,
                InputLengthBits = 48,
                InputLength = 6,
                OutputStartByte = 68,
                OutputLengthBits = 96,
                OutputLength = 12,
            });
            if (!legacyFirstArea.ApplyMapping(expected) || secondArea.ApplyMapping(expected))
                throw new InvalidOperationException("Legacy mapping keys must migrate only to the first address area.");

            var rackRow = new TiaHardwareDeviceRowVM(new TiaHardwareModuleInfo
            {
                DeviceName = "Baugruppenträger",
                ProfinetName = "PN-PN-Coupler_X2",
                ModuleName = "PROFIsafe IN/OUT",
            });
            if (!string.Equals(rackRow.Prefix, "PN_PN_Coupler_X2", StringComparison.Ordinal))
                throw new InvalidOperationException("The physical device name was not used as Special Device prefix.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void VerifyExistingSignalReuseDoesNotCallUpdate()
    {
        var existingGuid = Guid.NewGuid();
        var existingInterface = new FeeInterface { Name = "Existing Interface" };
        var signal = new FeeInterfaceSignal
        {
            Guid = existingGuid,
            Tag = "PLC_IN_PartPresent",
            ParentInterface = existingInterface,
            ReuseExistingWithoutUpdate = true,
        };

        if (!signal.CreateSignalAsync(existingInterface).GetAwaiter().GetResult() ||
            signal.Guid != existingGuid)
        {
            throw new InvalidOperationException("Existing FEE signals were not reused unchanged.");
        }
    }

    private static void VerifySignalResolutionPlanner()
    {
        var generationInterface = new FeeInterface
        {
            Name = GrobGenerationInterfaceResolver.InterfaceName,
            ProviderGuid = Defines.GrobGenerationInterfaceProviderGuid,
            ProviderName = GrobGenerationInterfaceResolver.ProviderName,
            Signals = []
        };
        var existingInterface = new FeeInterface
        {
            Name = "PLC Interface",
            ProviderName = "Other.Provider",
            Signals =
            [
                new FeeInterfaceSignal
                {
                    Guid = Guid.NewGuid(),
                    Tag = "Ready",
                    Address = "%I1.0"
                }
            ]
        };
        existingInterface.Signals[0].ParentInterface = existingInterface;
        var ready = new FeeInterfaceSignal { Tag = "Ready", Address = "%I1.0" };
        var missing = new FeeInterfaceSignal { Tag = "Missing", Address = "%I1.1" };
        var duplicateMissing = new FeeInterfaceSignal { Tag = "Missing", Address = "%I1.1" };
        var plan = SignalResolutionPlanner.Build(
            [
                new SignalResolutionRequest("container-1", "Sensor 1", ready),
                new SignalResolutionRequest("container-2", "Sensor 2", missing),
                new SignalResolutionRequest("container-3", "Sensor 3", duplicateMissing)
            ],
            [generationInterface, existingInterface]);
        if (!plan.IsValid || plan.ExistingBindings.Count != 1 ||
            plan.MissingSignals.Count != 1 || plan.MissingAliases.Count != 1)
            throw new InvalidOperationException("Resolve-or-create signal planning is not deterministic.");

        plan.ApplyExistingBindings();
        if (!ready.ReuseExistingWithoutUpdate || ready.Guid != existingInterface.Signals[0].Guid ||
            !ReferenceEquals(ready.ParentInterface, existingInterface))
        {
            throw new InvalidOperationException("Resolved signal identity or provenance was not retained.");
        }

        var conflict = SignalResolutionPlanner.Build(
            [new SignalResolutionRequest(
                "container-3",
                "Sensor 3",
                new FeeInterfaceSignal { Tag = "Ready", Address = "%I9.9" })],
            [existingInterface]);
        if (conflict.IsValid || conflict.Issues.Single().Code != "EXISTING_SIGNAL_IDENTITY_CONFLICT")
            throw new InvalidOperationException("Conflicting tag/address identity must block before FEE writes.");

        var explicitlyMapped = new FeeInterfaceSignal { Tag = "Ready", Address = "%I9.9" };
        var manualPlan = SignalResolutionPlanner.Build(
            [new SignalResolutionRequest("container-3", "Sensor 3", explicitlyMapped, "signal-node-3")],
            [existingInterface],
            [new VisualSignalAssignment(
                "signal-node-3",
                existingInterface.Signals[0].Guid.ToString("D"),
                "Ready",
                existingInterface.Name)]);
        manualPlan.ApplyExistingBindings();
        if (!manualPlan.IsValid || explicitlyMapped.Guid != existingInterface.Signals[0].Guid)
        {
            throw new InvalidOperationException(
                "An explicit drag/drop signal assignment must resolve a reviewed identity conflict deterministically.");
        }

        var generationResolution = GrobGenerationInterfaceResolver.Resolve(
            [generationInterface, existingInterface]);
        if (!generationResolution.IsValid ||
            !ReferenceEquals(generationResolution.Interface, generationInterface))
        {
            throw new InvalidOperationException("Legacy Grob Generation Interface identity was not resolved strictly.");
        }


        var providerResolution = GrobGenerationInterfaceResolver.ResolveProvider(
            [new GrobGenerationProviderIdentity(
                Defines.GrobGenerationInterfaceProviderGuid,
                "localized or version-dependent provider name")]);
        if (!providerResolution.IsValid ||
            providerResolution.Provider?.ProviderGuid != Defines.GrobGenerationInterfaceProviderGuid)
        {
            throw new InvalidOperationException(
                "Grob Generation provider must be resolved by its stable provider GUID.");
        }

        var inconsistentProvider = GrobGenerationInterfaceResolver.ResolveProvider(
            [new GrobGenerationProviderIdentity(Guid.NewGuid(), GrobGenerationInterfaceResolver.ProviderName)]);
        if (inconsistentProvider.IsValid ||
            inconsistentProvider.Issue?.Code != "GROB_GENERATION_PROVIDER_INCONSISTENT")
        {
            throw new InvalidOperationException(
                "A provider-name match with a foreign GUID must not authorize FEE writes.");
        }
    }

    private static ContainerToFeeVisualPlanService VerifyContainerToFeeVisualPlan()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"vibn-container2fee-visual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var xmlPath = Path.Combine(directory, "Container.xml");
        try
        {
            File.WriteAllText(
                xmlPath,
                """
                <AutoCreate>
                  <Container id="sensor-1">
                    <Component>Sensor_1</Component>
                    <Type>Sensor</Type>
                    <Entries>
                      <Entry>
                        <Slot>PLC_IN_PartPresent</Slot>
                        <Signal>Sensor_1_Present</Signal>
                        <Address>%I10.0</Address>
                        <DataType>Bool</DataType>
                        <ID>1</ID>
                      </Entry>
                    </Entries>
                  </Container>
                </AutoCreate>
                """);

            var service = new ContainerToFeeVisualPlanService();
            var loaded = service.LoadXmlAsync(xmlPath).GetAwaiter().GetResult();
            if (!loaded.Success || loaded.Plan is null)
                throw new InvalidOperationException($"Visual plan could not be loaded: {loaded.Message}");
            if (loaded.Plan.Targets.Count != 1 ||
                loaded.Plan.Nodes.Count(node => node.Kind == VisualNodeKind.Signal) != 1 ||
                loaded.Plan.Edges.Count == 0)
                throw new InvalidOperationException("Visual plan does not contain the expected target, signal and edges.");

            var container = loaded.Plan.Nodes.Single(node => node.Kind == VisualNodeKind.Container);
            var signalNode = loaded.Plan.Nodes.Single(node => node.Kind == VisualNodeKind.Signal);
            if (!service.SetSlotOverride(signalNode.Id, "PLC_IN_PartPresent_Ch1") ||
                service.CurrentPlan!.GetEffectiveSlot(signalNode) != "PLC_IN_PartPresent_Ch1")
                throw new InvalidOperationException("A valid visual slot override could not be applied.");
            if (!service.Undo() || service.CurrentPlan.GetEffectiveSlot(signalNode) != "PLC_IN_PartPresent")
                throw new InvalidOperationException("Visual slot-override undo failed.");
            if (!service.Redo() || service.CurrentPlan.GetEffectiveSlot(signalNode) != "PLC_IN_PartPresent_Ch1")
                throw new InvalidOperationException("Visual slot-override redo failed.");
            if (!loaded.Plan.IsGenerationSelected(container.Id) ||
                !service.SetGenerationSelected(container.Id, false) ||
                service.CurrentPlan!.IsGenerationSelected(container.Id))
                throw new InvalidOperationException("Visual container selection could not be disabled.");
            if (!service.Undo() || !service.CurrentPlan!.IsGenerationSelected(container.Id))
                throw new InvalidOperationException("Visual container-selection undo failed.");
            if (!service.Redo() || service.CurrentPlan!.IsGenerationSelected(container.Id))
                throw new InvalidOperationException("Visual container-selection redo failed.");
            if (!container.SupportsCreation || !service.CurrentPlan!.IsCreationRequested(container.Id))
                throw new InvalidOperationException("Missing SimObjects must be created by default.");
            if (!service.SetCreationRequested(container.Id, false))
                throw new InvalidOperationException("Visual creation request could not be disabled.");
            if (!service.Undo() || !service.CurrentPlan!.IsCreationRequested(container.Id))
                throw new InvalidOperationException("Visual creation-request undo failed.");
            if (!service.Redo() || service.CurrentPlan!.IsCreationRequested(container.Id))
                throw new InvalidOperationException("Visual creation-request redo failed.");
            if (service.SetAllCreationRequested(true) != 1 ||
                !service.CurrentPlan.IsCreationRequested(container.Id) ||
                service.SetAllCreationRequested(false) != 1 ||
                service.CurrentPlan.IsCreationRequested(container.Id))
            {
                throw new InvalidOperationException("Visual all/none creation selection is inconsistent.");
            }
            service.SetGenerationSelected(container.Id, true);
            var blocked = service.Validate();
            if (!blocked.Issues.Any(issue => issue.Code == "SIM_OBJECT_TARGET_UNASSIGNED"))
                throw new InvalidOperationException("A selected, unassigned target with creation disabled must block generation.");
            service.SetCreationRequested(container.Id, true);
            var creatable = service.Validate();
            if (creatable.Issues.Any(issue => issue.Code == "SIM_OBJECT_TARGET_UNASSIGNED"))
                throw new InvalidOperationException("A selected target with automatic creation enabled must not block generation.");
            service.SetGenerationSelected(container.Id, false);
            service.SetCreationRequested(container.Id, false);
            var selectedInterface = new VisualFeeInterface(
                Guid.NewGuid().ToString("D"),
                "Existing PLC Interface",
                "Test Provider",
                1);
            if (!service.SetExistingInterface(selectedInterface))
                throw new InvalidOperationException("Visual existing-interface selection could not be stored.");

            service.SaveSidecarAsync().GetAwaiter().GetResult();
            var restored = new ContainerToFeeVisualPlanService();
            var restoredResult = restored.LoadSidecarAsync(service.CurrentPlan.SidecarPath)
                .GetAwaiter()
                .GetResult();
            if (!restoredResult.Success ||
                restored.CurrentPlan?.IsCreationRequested(container.Id) != false ||
                restored.CurrentPlan.IsGenerationSelected(container.Id) ||
                restored.CurrentPlan.GetEffectiveSlot(signalNode) != "PLC_IN_PartPresent_Ch1" ||
                restored.CurrentPlan.ExistingInterfaceSelection?.InterfaceGuid != selectedInterface.GuidString)
                throw new InvalidOperationException("Visual sidecar was not restored correctly.");

            return restored;
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyInstalledFeeVersionRequiresSdk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-fee-version-{Guid.NewGuid():N}");
        var complete = Path.Combine(directory, "5.0.9.12345");
        var incompleteNewer = Path.Combine(directory, "5.0.99.99999");
        Directory.CreateDirectory(Path.Combine(complete, "Bin"));
        Directory.CreateDirectory(incompleteNewer);
        try
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "FS.SDK.dll"),
                Path.Combine(complete, "Bin", "FS.SDK.dll"));
            var discovered = new FeeVersionInfoProvider([directory]).Read();
            if (!string.Equals(discovered.InstalledFeeVersion, "V5.0.9.12345", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Incomplete FEE folders must be ignored; got {discovered.InstalledFeeVersion}.");
            }

            var incompleteOnly = new FeeVersionInfoProvider([incompleteNewer]).Read();
            if (!string.Equals(incompleteOnly.InstalledFeeVersion, "Nicht erkannt", StringComparison.Ordinal))
                throw new InvalidOperationException("A FEE folder without Bin/FS.SDK.dll was accepted.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyAutomationInstallationDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-installation-discovery-{Guid.NewGuid():N}");
        var openness = Path.Combine(
            directory,
            "Siemens",
            "Automation",
            "Portal V20",
            "PublicAPI",
            "V20");
        var twinCat = Path.Combine(directory, "Beckhoff", "TwinCAT", "3.1");
        Directory.CreateDirectory(openness);
        Directory.CreateDirectory(twinCat);
        try
        {
            File.WriteAllText(Path.Combine(openness, "Siemens.Engineering.dll"), "fixture");
            var discovery = new AutomationInstallationDiscovery(
                [directory],
                () =>
                (
                    [
                        new InstalledProductEvidence(
                            "SIMATIC WinCC Unified Runtime",
                            "20.0.1",
                            @"C:\Program Files\Siemens\WinCC",
                            "fixture:wincc"),
                        new InstalledProductEvidence(
                            "SIMATIC WinCC Unified Runtime",
                            "20.0.1",
                            string.Empty,
                            "fixture:wincc-duplicate-32bit"),
                        new InstalledProductEvidence(
                            "Siemens Safety Advanced V20",
                            "20.0",
                            @"C:\Program Files\Siemens\Safety",
                            "fixture:safety"),
                        new InstalledProductEvidence(
                            "SIMATIC STEP 7 Professional V20",
                            "V20.0",
                            @"C:\Program Files\Siemens\Automation\Portal V20",
                            "fixture:tia-registry-duplicate")
                    ],
                    []));
            var inventory = discovery.Discover();
            if (!inventory.TiaVersions.SequenceEqual(["V20"]) ||
                !inventory.Components.Any(component => component.Kind == AutomationComponentKind.TiaPortal) ||
                !inventory.Components.Any(component => component.Kind == AutomationComponentKind.TiaOpenness) ||
                !inventory.Components.Any(component => component.Kind == AutomationComponentKind.WinCc) ||
                !inventory.Components.Any(component => component.Kind == AutomationComponentKind.SiemensExtension) ||
                !inventory.Components.Any(component => component.Kind == AutomationComponentKind.TwinCat))
            {
                throw new InvalidOperationException("Dynamic automation installation discovery missed fixture evidence.");
            }
            if (inventory.Components.Count(component =>
                    component.Product == "SIMATIC WinCC Unified Runtime" &&
                    component.Version == "20.0.1") != 1)
            {
                throw new InvalidOperationException("Duplicate 32-/64-bit automation product entries were not collapsed.");
            }
            if (inventory.Components.Count(component =>
                    component.Kind == AutomationComponentKind.TiaPortal &&
                    component.Version.Contains("20", StringComparison.OrdinalIgnoreCase)) != 1)
            {
                throw new InvalidOperationException("Folder and registry evidence for the same TIA Portal version were not collapsed.");
            }
            if (AutomationInstallationDiscovery.ClassifyInstalledProduct("Unrelated Editor") is not null)
                throw new InvalidOperationException("An unrelated installed product was classified as automation software.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyPasswordBoxBinding()
    {
        var probe = new PasswordBindingProbe();
        var passwordBox = new PasswordBox();
        BindingOperations.SetBinding(
            passwordBox,
            PasswordBoxBindingBehavior.PasswordProperty,
            new Binding(nameof(PasswordBindingProbe.Value))
            {
                Source = probe,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

        passwordBox.Password = "typed-secret";
        if (probe.Value != "typed-secret" ||
            BindingOperations.GetBindingExpression(
                passwordBox,
                PasswordBoxBindingBehavior.PasswordProperty) is null)
        {
            throw new InvalidOperationException("PasswordBox typing did not update the bound view model or destroyed its binding.");
        }

        probe.Value = "view-model-secret";
        if (passwordBox.Password != "view-model-secret")
            throw new InvalidOperationException("A PasswordBox did not accept a value updated by its view model.");
    }

    private static void VerifyNavigationPreferencePersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-navigation-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "navigation.json");
        try
        {
            var store = new JsonNavigationPreferenceStore(path);
            if (!store.LoadExpanded())
                throw new InvalidOperationException("A new navigation preference must default to expanded.");
            store.SaveExpanded(false);
            var reloaded = new JsonNavigationPreferenceStore(path);
            if (reloaded.LoadExpanded())
                throw new InvalidOperationException("The collapsed navigation preference was not persisted.");

            var viewModel = new MainWindowVM(reloaded);
            if (viewModel.IsNavigationExpanded)
                throw new InvalidOperationException("MainWindowVM did not load the collapsed navigation preference.");
            viewModel.ToggleNavigationCommand.Execute(null);
            if (!viewModel.IsNavigationExpanded || !new JsonNavigationPreferenceStore(path).LoadExpanded())
                throw new InvalidOperationException("The navigation toggle did not persist its updated state.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyConfigurationFieldAcceptsCreatedSubtask()
    {
        var field = new ViCoConfigurationFieldVM(
            new ViCoConfigurationField("SW", "TIA V20", SubtaskId: 0));
        if (field.CanSave)
            throw new InvalidOperationException("A missing configuration subtask was treated as existing.");

        field.Value = "TIA V20 / FEE";
        field.AcceptSavedValue();
        if (!field.CanSave || field.IsChanged)
        {
            throw new InvalidOperationException(
                "A successfully created configuration subtask would be posted again on Enter.");
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void SavePreview(FrameworkElement view, string path)
    {
        var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32);
        var canvas = new DrawingVisual();
        using (var drawing = canvas.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1600, 900));
            drawing.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, 1600, 900));
        }
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class BindingErrorTraceListener : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Messages.Add(message);
        }

        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class PasswordBindingProbe : INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

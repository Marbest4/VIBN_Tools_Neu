using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using SixLabors.Fonts;
using VIBN_Tools.ContainerGeneration.BusinessLogic;
using VIBN_Tools.ContainerGeneration.AI;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ZuLiData;
using VIBN_Tools.ContainerGeneration.Models;
using VIBN_Tools.ContainerGeneration.BusinessLogic.ContainerData;
using VIBN_Tools.ContainerGeneration.BusinessLogic.RequirementsXml;
using VIBN_Tools.ContainerGeneration.Utils;
using VIBN_Tools.Application.VM;
using VIBN_Tools.ContainerToFee;
using VIBN_Tools.ContainerToFee.GrobStandard;
using VIBN_Tools.ContainerToFeeVisual;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.ModelValidation;
using VIBN_Tools.SpecialDevices;
using static VIBN_Tools.GlobalClasses.FeeObjects.FeeLogic;

namespace VIBN_Tools.ContainerGeneration.SmokeTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var files = args.Length > 0
            ? args.Select(Path.GetFullPath).ToArray()
            : new[]
            {
                Path.Combine(AppContext.BaseDirectory, "TestData", "Interface5.xlsx"),
                Path.Combine(AppContext.BaseDirectory, "TestData", "Interface7.xlsx")
            };

        var fontsAssembly = typeof(Font).Assembly;
        var fontsVersion = FileVersionInfo.GetVersionInfo(fontsAssembly.Location).FileVersion;
        if (!string.Equals(fontsVersion, "1.0.1.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SixLabors.Fonts 1.0.1.0 erwartet, aber {fontsVersion ?? "keine Version"} aus " +
                $"'{fontsAssembly.Location}' geladen.");
        }

        foreach (var file in files)
            await ValidateImportAndGenerationAsync(file);

        await ValidateGoldenMasterCorpusAsync();
        await ValidateSensorXAndSlotValidationAsync();

        ValidateWorkspacePersistenceAndAutoSaveSettings();
        ValidateWorkspaceBlockingMarker();
        ValidateSlotMultiplicityPolicy();
        await ValidateContainerToFeeModelContractsAsync();
        await ValidateVisualMotionJointReuseAsync();
        await ValidateVisualFeeSignalStatusAsync();
        ValidateFee2ContainerSelectionHighlighting();
        ValidatePlcInputFanInParsing();
        ValidateContainerFileComparison();
        await ValidateFee2ContainerProvenanceRoundTripAsync();
        await ValidateFee2ContainerVisualTypeCoverageAsync();
        ValidateFee2ContainerLiveReconstruction();
        ValidateTopLevelBasicFrameSelection();
        ValidateFee2SpecialDevicesProvenanceRoundTrip();
        ValidateRuleSuggestionWorkflow();
        await ValidateRequirementsRulePatchWorkflowAsync();

        Console.WriteLine(
            $"Container-Generation-Smoke-Test erfolgreich; SixLabors.Fonts {fontsVersion}.");
        return 0;
    }

    private static async Task ValidateContainerToFeeModelContractsAsync()
    {
        static void RequireScale(string propertyName, float x, float y, float z, string name)
        {
            var property = typeof(ContainerGeneratedObjectDefaults).GetProperty(propertyName)
                ?? throw new InvalidOperationException($"Größenvorgabe {propertyName} fehlt.");
            var scale = property.GetValue(null)
                ?? throw new InvalidOperationException($"Größenvorgabe {propertyName} ist leer.");
            var scaleType = scale.GetType();
            float ReadComponent(string component) =>
                (float)(scaleType.GetProperty(component)?.GetValue(scale) ??
                        scaleType.GetField(component)?.GetValue(scale) ??
                        float.NaN);
            var actualX = ReadComponent("X");
            var actualY = ReadComponent("Y");
            var actualZ = ReadComponent("Z");
            if (actualX != x || actualY != y || actualZ != z)
                throw new InvalidOperationException($"Unerwartete ursprüngliche Container2FEE-Größe für {name}: {scale}.");
        }

        RequireScale("StopFloorScale", 0.01f, 0.2f, 0.05f, "Stop/Floor");
        RequireScale("SensorScale", 0.01f, 0.03f, 0.01f, "Sensor");
        RequireScale("ConveyorSurfaceScale", 2f, 0.5f, 0.05f, "Conveyor/Surface");
        RequireScale("MotionJointScale", 0.5f, 0.5f, 0.5f, "MotionJoint");
        RequireScale("PickAndPlaceScale", 0.1f, 0.1f, 0.1f, "PickAndPlace");
        RequireScale("ButtonScale", 0.5f, 0.5f, 0.5f, "Button");
        if (ContainerGeneratedObjectDefaults.MotionOperationTime <= 0f ||
            ContainerGeneratedObjectDefaults.MotionHomePosition == ContainerGeneratedObjectDefaults.MotionWorkPosition ||
            ContainerGeneratedObjectDefaults.GripperUnclampedPosition == ContainerGeneratedObjectDefaults.GripperClampedPosition)
        {
            throw new InvalidOperationException("Die ModelValidation-Fallbackparameter sind nicht plausibel.");
        }

        var logicGuid = Guid.NewGuid();
        var floorGuid = Guid.NewGuid();
        var additionalFloorGuid = Guid.NewGuid();
        var actualLinks = new[]
        {
            (floorGuid.ToString(), new[] { "Collision" }),
            (additionalFloorGuid.ToString(), new[] { "collision" }),
        };
        if (!ContainerSlotLinkService.ContainsAllEndpoints(
                actualLinks,
                [(floorGuid, "Collision"), (additionalFloorGuid, "Collision")]) ||
            ContainerSlotLinkService.ContainsAllEndpoints(
                actualLinks,
                [(floorGuid, "Collision"), (Guid.NewGuid(), "Collision")]))
        {
            throw new InvalidOperationException("Die Slot-Link-Rückleseprüfung erkennt vollständige bzw. fehlende Endpunkte nicht korrekt.");
        }
        if (!ContainerSlotLinkService.ContainsVariableEndpoint(
                [(logicGuid, new[] { "PLC_IN_Opened" })],
                logicGuid,
                "plc_in_opened"))
        {
            throw new InvalidOperationException("Die Variablen-Link-Rückleseprüfung ist nicht case-insensitive.");
        }

        var completeStop = new FeeLogic
        {
            Slots = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                [LogicsStandard.Grob_Stop.Slots.Open] = Guid.NewGuid(),
                [LogicsStandard.Grob_Stop.Slots.Opened] = Guid.NewGuid(),
                [LogicsStandard.Grob_Stop.Slots.Collision] = floorGuid,
            }
        };
        if ((await new StopValidator().ValidateAsync(completeStop)).Any(issue => issue.Severity == Severity.Error))
            throw new InvalidOperationException("Ein vollständig verbundener Stopper wird von ModelValidation abgelehnt.");

        completeStop.Slots.Remove(LogicsStandard.Grob_Stop.Slots.Opened);
        var incompleteStopIssues = (await new StopValidator().ValidateAsync(completeStop)).ToArray();
        if (!incompleteStopIssues.Any(issue => issue.Message.Contains("Status Slots", StringComparison.Ordinal)))
            throw new InvalidOperationException("ModelValidation erkennt einen fehlenden Opened/Closed-Status nicht.");

        var belt = new FeeLogic
        {
            Slots = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                [LogicsStandard.Grob_BeltControl.Slots.AxisValue] = Guid.NewGuid(),
                [LogicsStandard.Grob_BeltControl.Slots.BeltControlState] = Guid.NewGuid(),
            }
        };
        if ((await new BeltControlValidator().ValidateAsync(belt)).Any(issue => issue.Severity == Severity.Error))
            throw new InvalidOperationException("Eine vollständig verbundene BeltControl-Logik wird fälschlich abgelehnt.");

        var stopContainer = new GrobStop_Container
        {
            Signal_Open = new FeeInterfaceSignal(),
            Signal_Opened = new FeeInterfaceSignal(),
            IsCreationRequested = true,
        };
        if (ContainerModelValidationPreflight.Validate(stopContainer)
            .Any(issue => issue.Severity == ContainerPreflightSeverity.Error))
        {
            throw new InvalidOperationException("Der Stopper-Preflight lehnt eine vollständige Erzeugung ab.");
        }
        stopContainer.Signal_Opened = null!;
        if (!ContainerModelValidationPreflight.Validate(stopContainer)
            .Any(issue => issue.Code == "STOP_STATUS_MISSING"))
        {
            throw new InvalidOperationException("Der Stopper-Preflight erkennt die fehlende Rückmeldung nicht.");
        }
    }

    private static async Task ValidateGoldenMasterCorpusAsync()
    {
        // These are regression floors for the single user-approved V17 rule file.
        // The reference exports were created with several DE/EN rule versions;
        // therefore they are evaluation truth, not byte-identical output truth.
        var regressionFloor = new Dictionary<int, (int Assigned, int ExactSlots)>
        {
            [1] = (51, 17),
            [2] = (70, 13),
            [3] = (66, 16),
            [4] = (404, 0),
            [5] = (408, 3),
            [6] = (136, 134),
            [7] = (102, 0)
        };
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "GoldenMaster");
        var requirementsPath = Path.Combine(fixtureDirectory, "DE_AutoCreate_Master_V17.xml");
        if (!File.Exists(requirementsPath))
            throw new FileNotFoundException("Golden-Master-Requirements fehlen.", requirementsPath);

        var requirements = XDocument.Load(requirementsPath);
        for (var index = 1; index <= 7; index++)
        {
            var interfacePath = Path.Combine(fixtureDirectory, $"Interface{index}.xlsx");
            var expectedPath = Path.Combine(fixtureDirectory, $"Container{index}.xml");
            var zuli = new ZuLiDefault();
            var import = await zuli.ReadFromFileAsync(interfacePath);
            if (!import.IsSuccess)
                throw new InvalidOperationException($"Golden-Master Interface{index} konnte nicht gelesen werden: {import.ErrorMessage}");

            var result = await new ContainerGenerator().GenerateAsync(new ContainerGenerationRequest(
                import.Value,
                requirements,
                [
                    new GroupingRule { TargetField = match => match.ContainerName, GroupOrder = 0 },
                    new GroupingRule { TargetField = match => match.ComponentType, GroupOrder = 1 }
                ],
                null,
                IgnoreCase: true,
                UseFilterList: true));
            var expected = ContainerFileWorkspaceReader.Read(expectedPath);
            var actualAssigned = result.Containers.SelectMany(container => container.DataList).ToArray();
            var expectedAssigned = expected.Containers.SelectMany(container => container.DataList).ToArray();
            var exactExpected = expectedAssigned
                .GroupBy(EntryIdentity)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var exactMatches = actualAssigned.Count(actual =>
                exactExpected.TryGetValue(EntryIdentity(actual), out var expectedEntry) &&
                string.Equals(actual.Slot, expectedEntry.Slot, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine(
                $"GoldenMaster {index}: Input={import.Value.Count}, erwartet zugeordnet={expectedAssigned.Length}, " +
                $"generiert zugeordnet={actualAssigned.Length}, Slot-Treffer={exactMatches}, " +
                $"offen={result.UnassignedSignals.Count}, gefiltert={result.FilteredSignals.Count}.");
            var accountedSignals = actualAssigned.Length + result.UnassignedSignals.Count + result.FilteredSignals.Count;
            var floor = regressionFloor[index];
            if (result.Statistics.TotalSignals != import.Value.Count ||
                accountedSignals != import.Value.Count ||
                actualAssigned.Length < floor.Assigned ||
                exactMatches < floor.ExactSlots)
            {
                throw new InvalidOperationException(
                    $"Golden-Master {index} unterschreitet die verifizierte Generatorbasis: " +
                    $"Input={import.Value.Count}, bilanziert={accountedSignals}, " +
                    $"Zuordnung={actualAssigned.Length}/{floor.Assigned}, Slot-Treffer={exactMatches}/{floor.ExactSlots}.");
            }
        }
    }

    private static async Task ValidateSensorXAndSlotValidationAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "GoldenMaster");
        var requirements = new RequirementsXml();
        var loadResult = await requirements.ReadFromFileAsync(
            Path.Combine(fixtureDirectory, "DE_AutoCreate_Master_V17.xml"));
        if (!loadResult.IsSuccess)
            throw new InvalidOperationException($"Requirements für Slotprüfung konnten nicht geladen werden: {loadResult.ErrorMessage}");

        var orientation = new ContainerData
        {
            Id = "sensor-x",
            Component = "Werkstücklage",
            Type = "SensorX",
            DataList = new System.Collections.ObjectModel.ObservableCollection<ContainerEntry>
            {
                new() { ID = "1", Signal = "Lage i.O.", Slot = "PLC_orientation_OK", DataType = "Bool" },
                new() { ID = "2", Signal = "Lage n.i.O.", Slot = "PLC_orientation_nOK", DataType = "Bool" }
            }
        };
        var valid = WorkspaceValidationAnalyzer.Analyze([orientation], [], [], requirements);
        if (valid.UnknownSlots != 0 || valid.HasBlockingIssues)
            throw new InvalidOperationException("SensorX-Slots aus AutoCreate_Master.xml werden fälschlich abgelehnt.");

        orientation.DataList[1].Slot = "PLC_orientation_does_not_exist";
        var invalid = WorkspaceValidationAnalyzer.Analyze([orientation], [], [], requirements);
        if (invalid.UnknownSlots != 1 || !invalid.HasBlockingIssues ||
            !orientation.DataList[1].ValidationError.Contains("existiert", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ein nicht mehr vorhandener Slot blockiert den Containerexport nicht eindeutig.");
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"vibn-sensorx-{Guid.NewGuid():N}.xml");
        try
        {
            new XDocument(
                new XElement("ContainerFile",
                    new XElement("Container",
                        new XElement("Component", "Werkstücklage"),
                        new XElement("Type", "SensorX"),
                        new XElement("DataList",
                            new XElement("Entry",
                                new XElement("ID", "1"),
                                new XElement("Address", "%I1.0"),
                                new XElement("DataType", "Bool"),
                                new XElement("Signal", "Lage i.O."),
                                new XElement("Slot", "PLC_orientation_OK"),
                                new XElement("Note", string.Empty)))))).Save(xmlPath);
            var parsed = ContainerToFeeService.ReadInContainerXmlData(xmlPath);
            if (parsed.Item1.SingleOrDefault() is not VIBN_Tools.ContainerToFee.General.PartOrientation_Container ||
                parsed.Item2.Count != 0)
            {
                throw new InvalidOperationException("SensorX wird in Container2FEE nicht als bekannter Container erkannt.");
            }
        }
        finally
        {
            if (File.Exists(xmlPath))
                File.Delete(xmlPath);
        }
    }

    private static string EntryIdentity(ContainerEntry entry) =>
        $"{entry.ID.Trim()}|{entry.Signal.Trim()}|{entry.Address.Trim()}";

    private static void ValidateFee2SpecialDevicesProvenanceRoundTrip()
    {
        var coverage = new Fee2SpecialDeviceSignalCoverage(3, 2, 4, 3, ["PLC_IN_Missing", "PLC_OUT_Missing"]);
        var coverageRoot = new Fee2SpecialDeviceRoot(
            Guid.NewGuid(),
            "coverage",
            new FeeSpecialDeviceSnapshot(
                FeeSpecialDeviceProvenanceCodec.CurrentSchema,
                "coverage",
                "Keyence",
                "SR2000",
                null,
                0,
                0,
                []),
            0,
            0,
            true,
            coverage);
        if (coverageRoot.InputSignalCount != 3 || coverageRoot.ConnectedInputSignalCount != 2 ||
            coverageRoot.OutputSignalCount != 4 || coverageRoot.ConnectedOutputSignalCount != 3 ||
            !coverageRoot.MissingPlcSlots.Contains("PLC_OUT_Missing", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FEE2SpecialDevices PLC_IN/PLC_OUT coverage projection is inconsistent.");
        }

        var variableGuid = Guid.NewGuid();
        var snapshot = new FeeSpecialDeviceSnapshot(
            FeeSpecialDeviceProvenanceCodec.CurrentSchema,
            "SCN01",
            "Keyence",
            "SR2000",
            null,
            100,
            200,
            new[]
            {
                new FeeSpecialDeviceSignalSnapshot(
                    variableGuid,
                    "SCN01_Ready",
                    "E100.0",
                    "Write",
                    "Bool",
                    "Bereit")
            });
        var tags = FeeSpecialDeviceProvenanceCodec.Encode(snapshot);
        if (!FeeSpecialDeviceProvenanceCodec.TryRead(tags, out var decoded, out var error) ||
            decoded is null || decoded.Prefix != "SCN01" ||
            decoded.Signals.Single().VariableGuid != variableGuid)
        {
            throw new InvalidOperationException($"Special-device provenance round-trip failed: {error}");
        }

        var exportPath = Path.Combine(Path.GetTempPath(), $"vibn-{Guid.NewGuid():N}.specialdevice.json");
        try
        {
            FeeSpecialDeviceProvenanceCodec.SaveAtomically(decoded, exportPath);
            if (!FeeSpecialDeviceProvenanceCodec.TryLoadFile(exportPath, out var exported, out var loadError) ||
                exported?.InputByte != 100 || exported.OutputByte != 200 || exported.Signals.Count != 1)
                throw new InvalidOperationException("Special-device reverse export lost domain data.");
            var import = SpecialDeviceSnapshotImporter.Create(exported);
            if (!import.Success || import.Device?.DevicePrefix != "SCN01" ||
                import.Device.DeviceAddresses.Input != 100 || import.Device.DeviceAddresses.Output != 200 ||
                import.Warnings.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Special-device queue import failed or did not flag the intentionally reduced signal snapshot: {loadError} {import.Error}");
            }
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }

        var damaged = new Dictionary<string, string>(tags, StringComparer.Ordinal)
        {
            [FeeSpecialDeviceProvenanceCodec.HashKey] = new string('0', 64)
        };
        if (FeeSpecialDeviceProvenanceCodec.TryRead(damaged, out _, out _))
            throw new InvalidOperationException("Damaged special-device provenance was accepted.");
    }

    private static void ValidateFee2ContainerLiveReconstruction()
    {
        var rootGuid = Guid.NewGuid();
        var sensorGuid = Guid.NewGuid();
        var buttonGuid = Guid.NewGuid();
        var notGuid = Guid.NewGuid();
        var ignoredGuid = Guid.NewGuid();
        var unassignedGuid = Guid.NewGuid();
        var sensorSignal1 = Guid.NewGuid();
        var sensorSignal2 = Guid.NewGuid();
        var buttonSignal = Guid.NewGuid();
        var returnSignal = Guid.NewGuid();
        var switchGuid = Guid.NewGuid();
        var fuseGuid = Guid.NewGuid();
        var switchSignal = Guid.NewGuid();
        var fuseSignal = Guid.NewGuid();
        var result = FeeContainerLiveReconstructor.Reconstruct(
            rootGuid,
            "Existing main frame",
            [
                new FeeContainerLiveObject(sensorGuid, "Sensor_1", "LogicObject", "Grob_Sensor"),
                new FeeContainerLiveObject(buttonGuid, "Button_1", "Button"),
                new FeeContainerLiveObject(notGuid, "Return_1", "BoolNot"),
                new FeeContainerLiveObject(unassignedGuid, "Air_1", "VersionedLogic", "Grob_PneumaticSupply"),
                new FeeContainerLiveObject(switchGuid, "SwitchRaw;%I13.0", "FS.SDK.Scene.Objects.CabinetElement",
                    CabinetDefinition: @"Definitions\Grob_2PositionSwitch.xml", Label: "Selector_1"),
                new FeeContainerLiveObject(fuseGuid, "FuseRaw;%I14.0", "CabinetElement",
                    CabinetDefinition: @"definitions/Grob_Fuse.XML", Label: "Fuse_1"),
                new FeeContainerLiveObject(ignoredGuid, "Unrelated", "Decoration"),
            ],
            [
                new FeeContainerLiveVariable(sensorSignal1, "Sensor A", "%I10.0", "", "Bool", "S1"),
                new FeeContainerLiveVariable(sensorSignal2, "Sensor B", "%I10.1", "", "Bool", "S2"),
                new FeeContainerLiveVariable(buttonSignal, "Button NO", "%I11.0", "", "Bool", "B1"),
                new FeeContainerLiveVariable(returnSignal, "Return", "%Q12.0", "", "Bool", "R1"),
                new FeeContainerLiveVariable(switchSignal, "Selector NO", "%I13.0", "", "Bool", "SW1"),
                new FeeContainerLiveVariable(fuseSignal, "Fuse NC", "%I14.0", "", "Bool", "F1"),
            ],
            [
                new FeeContainerLiveAssignment(sensorSignal1, sensorGuid, "PLC_IN_PartPresent_Ch1"),
                new FeeContainerLiveAssignment(sensorSignal2, sensorGuid, "PLC_IN_PartPresent_Ch1"),
                new FeeContainerLiveAssignment(buttonSignal, buttonGuid, "Pressed"),
                new FeeContainerLiveAssignment(returnSignal, notGuid, "Input 01"),
                new FeeContainerLiveAssignment(switchSignal, switchGuid, "NO1"),
                new FeeContainerLiveAssignment(fuseSignal, fuseGuid, "NC"),
            ]);

        var containers = result.Snapshot.ContainerDocument.Descendants("Container").ToArray();
        if (result.Snapshot.ContainerCount != 6 || result.Snapshot.SignalCount != 6 ||
            result.IgnoredObjectCount != 1 || result.Issues.Count != 2 ||
            result.UnmappedObjects.Count != 1 ||
            result.UnmappedObjects.Single().Guid != ignoredGuid ||
            result.UnmappedObjects.Single().Name != "Unrelated" ||
            containers.Single(item => item.Element("Type")?.Value == "Sensor")
                .Descendants("Entry").Count() != 2 ||
            containers.Single(item => item.Element("Type")?.Value == "Button")
                .Descendants("Slot").Single().Value != "PLC_IN_NO" ||
            containers.Single(item => item.Element("Type")?.Value == "ReturnCircuit")
                .Descendants("Slot").Single().Value != "PLC_OUT_Signal" ||
            containers.Single(item => item.Element("Type")?.Value == "PneumaticSupply")
                .Descendants("Note").Single().Value.Contains("PRÜFEN", StringComparison.Ordinal) == false ||
            containers.Single(item => item.Element("Type")?.Value == "Switch")
                .Element("Component")?.Value != "Selector_1" ||
            containers.Single(item => item.Element("Type")?.Value == "Switch")
                .Descendants("Slot").Single().Value != "PLC_IN_NO1" ||
            containers.Single(item => item.Element("Type")?.Value == "Fuse")
                .Descendants("Slot").Single().Value != "PLC_IN_NC")
        {
            throw new InvalidOperationException(
                "Existing FEE BasicFrame reconstruction lost a supported container, fan-in, or slot mapping.");
        }

        var root = result.Snapshot.ContainerDocument.Root
            ?? throw new InvalidOperationException("Reconstructed ContainerFile has no document root.");
        if (root.Attribute("autoCreateFile") is null || root.Attribute("zuli") is null ||
            root.Attribute("source") is not null || root.Attribute("feeRootGuid") is not null)
        {
            throw new InvalidOperationException("Reconstructed ContainerFile does not match the production CAAResult schema attributes.");
        }

        var editableRoot = new Fee2ContainerRoot(
            rootGuid,
            "Editable",
            result.Snapshot,
            0,
            0,
            0,
            0,
            UsesExactProvenance: false,
            result.InspectedObjectCount,
            result.IgnoredObjectCount,
            result.Issues,
            result.UnmappedObjects);
        var editor = new Fee2ContainerRootEditor(editableRoot);
        editor.Containers[0].IsIncluded = false;
        var unmapped = editor.NonContainerObjects.Single();
        unmapped.TargetComponent = "ManuallyReviewed";
        unmapped.TargetContainerType = "Sensor";
        var manualContainer = editor.AddObjectAsContainer(unmapped);
        var editedSnapshot = editor.CreateSnapshot();
        if (!manualContainer.Id.StartsWith("manual:", StringComparison.Ordinal) ||
            editedSnapshot.ContainerCount != result.Snapshot.ContainerCount ||
            !editedSnapshot.ContainerDocument.Descendants("Component")
                .Any(item => item.Value == "ManuallyReviewed") ||
            editedSnapshot.ContainerDocument.Descendants("Container")
                .Any(item => item.Attribute("id")?.Value == editor.Containers[0].Id))
        {
            throw new InvalidOperationException(
                "FEE2Container review edits were not projected into the exported snapshot.");
        }

        var versionedId = "container:versioned-sensor";
        var versioned = FeeContainerLiveReconstructor.Reconstruct(
            rootGuid,
            "Versioned",
            [
                new FeeContainerLiveObject(Guid.NewGuid(), "Sensor_V", "LogicObject", "Grob_Sensor",
                    ProvenanceContainerId: versionedId, ProvenanceContainerType: "Sensor"),
                new FeeContainerLiveObject(Guid.NewGuid(), "Sensor_V", "SafetySensor",
                    ProvenanceContainerId: versionedId, ProvenanceContainerType: "Sensor"),
            ],
            [],
            []);
        if (versioned.Snapshot.ContainerCount != 1)
            throw new InvalidOperationException("Property provenance did not collapse generated logic and SimObject to one container.");

        var legacySimObjects = FeeContainerLiveReconstructor.Reconstruct(
            rootGuid,
            "Legacy",
            [
                new FeeContainerLiveObject(Guid.NewGuid(), "LegacySensor", "SafetySensor"),
                new FeeContainerLiveObject(Guid.NewGuid(), "LegacyStop", "Floor"),
            ],
            [],
            []);
        var legacyTypes = legacySimObjects.Snapshot.ContainerDocument.Descendants("Type")
            .Select(element => element.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!legacyTypes.SetEquals(["Sensor", "Stop"]))
            throw new InvalidOperationException("Legacy Sensor/Floor objects were not exposed as reviewable containers.");

        var knownLogicTypes = FeeContainerLiveReconstructor.Reconstruct(
            rootGuid,
            "Known logics",
            [
                new(Guid.NewGuid(), "Belt", "LogicObject", "Grob_BeltControl"),
                new(Guid.NewGuid(), "Clamp", "LogicObject", "Grob_Clamping"),
                new(Guid.NewGuid(), "Conveyor", "LogicObject", "Grob_Conveyor"),
                new(Guid.NewGuid(), "Cylinder", "LogicObject", "Grob_Cylinder"),
                new(Guid.NewGuid(), "Gripper", "LogicObject", "Grob_GripperBasic"),
                new(Guid.NewGuid(), "Vacuum", "LogicObject", "Grob_GripperVacuum"),
                new(Guid.NewGuid(), "Lift", "LogicObject", "Grob_LiftUnit"),
                new(Guid.NewGuid(), "Air", "LogicObject", "Grob_PneumaticSupply"),
                new(Guid.NewGuid(), "Door", "LogicObject", "Grob_SafetyDoor"),
                new(Guid.NewGuid(), "Sensor", "LogicObject", "Grob_Sensor"),
                new(Guid.NewGuid(), "Stop", "LogicObject", "Grob_Stop"),
            ],
            [],
            []);
        var reconstructedLogicTypes = knownLogicTypes.Snapshot.ContainerDocument
            .Descendants("Type")
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedLogicTypes = new HashSet<string>(
            ["BeltControl", "Clamping", "Conveyor", "Cylinder", "GripperBasic", "GripperVacuum",
             "LiftUnit", "PneumaticSupply", "SafetyDoor", "Sensor", "Stop"],
            StringComparer.OrdinalIgnoreCase);
        if (!reconstructedLogicTypes.SetEquals(expectedLogicTypes))
        {
            throw new InvalidOperationException(
                "FEE2Container did not reconstruct every distinct catalogued Grob logic type.");
        }
    }

    private static async Task ValidateVisualMotionJointReuseAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-motion-reuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "motion.container.xml");
        try
        {
            File.WriteAllText(path, """
                <ContainerFile>
                  <Container id="motion">
                    <Component>Axis_1</Component><Type>Cylinder</Type><DataList>
                      <Entry><ID>A</ID><Address>%Q0.0</Address><DataType>Bool</DataType><Signal>Move</Signal><Slot>PLC_OUT_ToWorkPos</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);
            var service = new ContainerToFeeVisualPlanService();
            var loaded = await service.LoadXmlAsync(path);
            if (!loaded.Success || loaded.Plan is null)
                throw new InvalidOperationException("MotionJoint reuse plan could not be loaded.");

            var visualObjectConstructor = typeof(VisualFeeObject).GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Single();
            VisualFeeObject CreateJoint() => (VisualFeeObject)visualObjectConstructor.Invoke(
                [
                    $"fee:{Guid.NewGuid():D}",
                    Guid.NewGuid().ToString("D"),
                    "Axis_1",
                    typeof(FeeJoint).FullName!,
                    "MotionJoint",
                    new[] { nameof(FeeJoint), typeof(FeeJoint).FullName! },
                ]);
            var objects = new[] { CreateJoint(), CreateJoint() };
            typeof(ContainerToFeeVisualPlanService)
                .GetField("_feeObjects", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, objects);

            var added = service.AutoAssignMatches();
            var target = loaded.Plan.Targets.Single(item => item.AllowMultiSelect);
            if (added != 2 || loaded.Plan.Assignments.Count(item => item.TargetId == target.Id) != 2)
            {
                throw new InvalidOperationException(
                    "Existing same-name MotionJoints were not reused for a multi-select visual target.");
            }
            if (!service.RemoveAssignment(target.Id, objects[0].Id).Success ||
                loaded.Plan.Assignments.Count(item => item.TargetId == target.Id) != 1)
            {
                throw new InvalidOperationException(
                    "Removing one object from a multi-select target removed more than that assignment.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ValidateVisualFeeSignalStatusAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-signal-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "signals.container.xml");
        try
        {
            File.WriteAllText(path, """
                <ContainerFile>
                  <Container id="sensor">
                    <Component>Sensor_1</Component><Type>Sensor</Type><DataList>
                      <Entry><ID>A</ID><Address>%I0.0</Address><DataType>Bool</DataType><Signal>DetectedA</Signal><Slot>PLC_IN_PartPresent_Ch1</Slot></Entry>
                      <Entry><ID>B</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>DetectedB</Signal><Slot>PLC_IN_PartPresent_Ch1</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);
            var service = new ContainerToFeeVisualPlanService();
            var loaded = await service.LoadXmlAsync(path);
            if (!loaded.Success || loaded.Plan is null)
                throw new InvalidOperationException("Signal status plan could not be loaded.");

            var sharedGuid = Guid.NewGuid().ToString("D");
            var duplicateGuid = Guid.NewGuid().ToString("D");
            var addedGuid = Guid.NewGuid().ToString("D");
            var interfaceGuid = Guid.NewGuid().ToString("D");
            var signals = new[]
            {
                new VisualFeeSignal(sharedGuid, interfaceGuid, "PLC", "Shared", "%I0.0", "", "Bool", "Input"),
                new VisualFeeSignal(duplicateGuid, interfaceGuid, "PLC", "Shared", "%I0.1", "", "Bool", "Input"),
                new VisualFeeSignal(addedGuid, interfaceGuid, "PLC", "Added", "%I0.2", "", "Bool", "Input"),
            };
            typeof(ContainerToFeeVisualPlanService)
                .GetField("_feeSignals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, signals);
            service.SetExistingInterface(new VisualFeeInterface(interfaceGuid, "PLC", "Test", signals.Length));
            var signalNodes = loaded.Plan.Nodes
                .Where(item => item.Kind == VisualNodeKind.Signal)
                .ToArray();
            foreach (var node in signalNodes)
            {
                if (!service.TryAssignSignal(node.Id, sharedGuid).Success)
                    throw new InvalidOperationException("Test signal could not be assigned.");
            }

            var logicGuid = Guid.NewGuid().ToString("D");
            typeof(ContainerToFeeVisualPlanService)
                .GetField("_feeContainerObjects", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, new[]
                {
                    new VisualFeeContainerObject(
                        logicGuid,
                        "Sensor_1",
                        VisualFeeContainerObjectKind.Logic,
                        "Grob_Sensor"),
                });
            typeof(ContainerToFeeVisualPlanService)
                .GetField("_feeSignalLinks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, new[]
                {
                    new VisualFeeSignalLink(
                        sharedGuid,
                        logicGuid,
                        "LogicObject",
                        "PLC_IN_PartPresent_Ch1",
                        false),
                });
            typeof(ContainerToFeeVisualPlanService)
                .GetField("_hasDiscoveredFeeSignalLinks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, true);
            var verifiedNodes = service.FindVerifiedSignalNodeIds(sharedGuid);
            if (verifiedNodes.Count != signalNodes.Length ||
                service.GetSignalConnectionState(signalNodes[0].Id, sharedGuid).Kind !=
                    VisualSignalConnectionKind.Linked)
            {
                throw new InvalidOperationException(
                    "Existing FEE signal-to-logic links were not recognized as verified visual assignments.");
            }

            var assigned = new ContainerToFeeVisualFeeSignalVM(
                signals[0],
                signals,
                loaded.Plan,
                verifiedNodes);
            var duplicate = new ContainerToFeeVisualFeeSignalVM(signals[1], signals, loaded.Plan);
            if (!assigned.IsAssigned || !assigned.HasDuplicateAssignment || !assigned.HasDuplicateName ||
                assigned.StateBackground != "#FFFFCDD2" || !duplicate.HasDuplicateName)
            {
                throw new InvalidOperationException(
                    "FEE signal assignment/duplicate state is not presented with the required error status. " +
                    $"Assigned={assigned.IsAssigned}, DuplicateAssignment={assigned.HasDuplicateAssignment}, " +
                    $"DuplicateName={assigned.HasDuplicateName}, Background={assigned.StateBackground}, " +
                    $"SecondDuplicateName={duplicate.HasDuplicateName}, Nodes={signalNodes.Length}, " +
                    $"NodeIds={string.Join(",", signalNodes.Select(item => item.Id))}, " +
                    $"Assignments={loaded.Plan.SignalAssignments.Count}.");
            }

            var container = loaded.Plan.Nodes.Single(item => item.Kind == VisualNodeKind.Container);
            var addedResult = service.AddSignals(container.Id, [addedGuid]);
            if (!addedResult.Success || loaded.Plan.AddedSignals.Count != 1 ||
                !service.Validate().Issues.Any(issue => issue.Code == "ADDED_SIGNAL_SLOT_REQUIRED"))
            {
                throw new InvalidOperationException(
                    "A drag/drop signal was not added as a visible, slot-required plan entry.");
            }
            var addedNode = loaded.Plan.FindNode(loaded.Plan.AddedSignals.Single().NodeId)!;
            if (!service.SetSlotOverride(addedNode.Id, "PLC_IN_PartPresent") ||
                service.Validate().Issues.Any(issue =>
                    issue.Code == "ADDED_SIGNAL_SLOT_REQUIRED" && issue.NodeId == addedNode.Id))
            {
                throw new InvalidOperationException(
                    "The added signal did not become valid after selecting an allowed PLC_IN slot.");
            }

            var effectivePath = Path.Combine(directory, "effective.container.xml");
            await service.SaveEffectiveContainerXmlAsync(effectivePath);
            var effectiveDocument = XDocument.Load(effectivePath);
            if (!effectiveDocument.Descendants("Signal").Any(item => item.Value == "Added"))
                throw new InvalidOperationException("Effective Container.xml export lost a dynamically added signal.");
            if (!service.RemoveSignal(addedNode.Id).Success || loaded.Plan.AddedSignals.Count != 0 ||
                loaded.Plan.FindNode(addedNode.Id) is not null)
            {
                throw new InvalidOperationException("Removing a dynamically added signal left stale plan state behind.");
            }
            var slotAssignment = service.AssignSignalsToSlot(
                container.Id,
                "PLC_IN_PartPresent",
                [addedGuid]);
            var slotAssignedNode = loaded.Plan.AddedSignals.SingleOrDefault();
            if (!slotAssignment.Success || slotAssignedNode is null ||
                !string.Equals(
                    loaded.Plan.GetEffectiveSlot(loaded.Plan.FindNode(slotAssignedNode.NodeId)!),
                    "PLC_IN_PartPresent",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Dropping an existing FEE signal onto an empty declared signal slot did not create a correctly slotted plan entry.");
            }
            var movedSlotAssignment = service.AssignSignalsToSlot(
                container.Id,
                "PLC_IN_PartPresent_Ch2",
                [addedGuid]);
            if (!movedSlotAssignment.Success || loaded.Plan.AddedSignals.Count != 1 ||
                loaded.Plan.AddedSignals[0].NodeId != slotAssignedNode.NodeId ||
                !string.Equals(
                    loaded.Plan.GetEffectiveSlot(loaded.Plan.FindNode(slotAssignedNode.NodeId)!),
                    "PLC_IN_PartPresent_Ch2",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Moving an already assigned FEE signal to another slot duplicated its plan entry.");
            }
            if (!service.RemoveSignal(slotAssignedNode.NodeId).Success)
                throw new InvalidOperationException("The signal-slot test entry could not be removed again.");
            if (!service.RemoveSignal(signalNodes[0].Id).Success ||
                !loaded.Plan.IsSignalRemoved(signalNodes[0].Id))
            {
                throw new InvalidOperationException("Removing an imported signal did not persist the effective deletion.");
            }
            await service.SaveEffectiveContainerXmlAsync(effectivePath);
            effectiveDocument = XDocument.Load(effectivePath);
            if (effectiveDocument.Descendants("Signal").Any(item => item.Value == signalNodes[0].Name))
                throw new InvalidOperationException("Effective Container.xml export still contains a removed imported signal.");
            var sidecarPath = Path.Combine(directory, "removed-signals.visual.json");
            await service.SaveSidecarAsync(sidecarPath);
            var reloadedService = new ContainerToFeeVisualPlanService();
            var reloaded = await reloadedService.LoadSidecarAsync(sidecarPath);
            if (!reloaded.Success || reloaded.Plan is null ||
                !reloaded.Plan.IsSignalRemoved(signalNodes[0].Id))
            {
                throw new InvalidOperationException("Sidecar schema did not restore an imported signal deletion.");
            }
            if (!service.Undo() || loaded.Plan.IsSignalRemoved(signalNodes[0].Id))
                throw new InvalidOperationException("Undo did not restore an imported signal removed from the visual plan.");

            var warning = FeeTagPropertyWriteResult.Unconfirmed(new InvalidOperationException("test"));
            if (warning.Confirmed || !warning.Warning.Contains("fortgesetzt", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "An unconfirmed TagComponent write no longer explicitly permits generation to continue.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateFee2ContainerSelectionHighlighting()
    {
        var viewModel = new Fee2ContainerPageVM();
        var firstContainer = new Fee2ContainerFoundContainerVM("c-1", "Cylinder_1", "Cylinder", 2);
        var secondContainer = new Fee2ContainerFoundContainerVM("c-2", "Sensor_1", "Sensor", 1);
        var firstSignal = new Fee2ContainerFoundSignalVM(
            "c-1", "Cylinder_1", "Cylinder", "Home", "PLC_IN_InHomePos",
            "%I0.0", "Bool", "A", string.Empty, null);
        var secondSignal = new Fee2ContainerFoundSignalVM(
            "c-2", "Sensor_1", "Sensor", "Detected", "PLC_IN_PartPresent",
            "%I0.1", "Bool", "B", string.Empty, null);
        viewModel.FoundContainers.Add(firstContainer);
        viewModel.FoundContainers.Add(secondContainer);
        viewModel.FoundSignals.Add(firstSignal);
        viewModel.FoundSignals.Add(secondSignal);

        viewModel.SelectedFoundContainer = firstContainer;
        if (!firstSignal.IsRelatedToSelection || secondSignal.IsRelatedToSelection)
            throw new InvalidOperationException("Container selection did not highlight exactly its FEE2Container signals.");

        viewModel.SelectedFoundSignal = secondSignal;
        if (!secondContainer.IsRelatedToSelection || firstContainer.IsRelatedToSelection ||
            viewModel.SelectedFoundContainer is not null)
        {
            throw new InvalidOperationException("Signal selection did not highlight exactly its FEE2Container container.");
        }
    }

    private static void ValidateTopLevelBasicFrameSelection()
    {
        var top = Guid.NewGuid();
        var nested = Guid.NewGuid();
        var secondTop = Guid.NewGuid();
        var selected = FeeTopLevelBasicFrameDiscovery.SelectTopLevel(
            [top, nested, secondTop],
            new Dictionary<Guid, IReadOnlySet<Guid>>
            {
                [top] = new HashSet<Guid> { nested },
                [nested] = new HashSet<Guid>(),
                [secondTop] = new HashSet<Guid>()
            });
        if (!selected.ToHashSet().SetEquals([top, secondTop]))
            throw new InvalidOperationException("Nested BasicFrames were offered as FEE2Container roots.");
    }

    private static void ValidateWorkspaceBlockingMarker()
    {
        var permittedFanIn = CreateContainerWithDuplicateSlot("PLC_IN_StatusWord");
        var permittedSummary = WorkspaceValidationAnalyzer.Analyze([permittedFanIn], [], []);
        if (permittedSummary.DuplicateSlots != 0 || permittedSummary.HasBlockingIssues)
            throw new InvalidOperationException("Permitted PLC_IN fan-in was incorrectly blocked by workspace validation.");

        var invalid = new ContainerData
        {
            Component = "Invalid",
            Type = "Sensor",
            DataList = new([
                new ContainerEntry { Signal = string.Empty, Slot = string.Empty, DataType = "Bool" }
            ])
        };
        var summary = WorkspaceValidationAnalyzer.Analyze([invalid], [], []);
        var marker = WorkspaceValidationOverrideMarker.Create(summary);
        if (!summary.HasBlockingIssues ||
            !invalid.DataList.Single().HasValidationError ||
            marker.Id != WorkspaceValidationOverrideMarker.ContainerId ||
            marker.DataList.Count == 0 ||
            marker.DataList.Any(entry => string.IsNullOrWhiteSpace(entry.Note)))
        {
            throw new InvalidOperationException("Invalid workspace entries or the explicit error export marker were not produced.");
        }

        var diagnosticPath = Path.Combine(Path.GetTempPath(), $"vibn-validation-{Guid.NewGuid():N}.xml");
        try
        {
            var writeResult = XmlHandler.WriteContainerXml([marker], diagnosticPath, "AutoCreate.xml", "Interface.xlsx");
            if (!writeResult.IsSuccess)
                throw new InvalidOperationException("The validation-error container is not schema-compatible.");
        }
        finally
        {
            if (File.Exists(diagnosticPath))
                File.Delete(diagnosticPath);
        }
    }

    private static void ValidateSlotMultiplicityPolicy()
    {
        var plcInput = CreateContainerWithDuplicateSlot("PLC_IN_StatusWord");
        plcInput.Validate();
        if (!plcInput.IsValid)
            throw new InvalidOperationException($"PLC_IN fan-in must be valid: {plcInput.ValidationError}");

        var plcOutput = CreateContainerWithDuplicateSlot("PLC_OUT_ControlWord");
        plcOutput.Validate();
        if (plcOutput.IsValid ||
            !plcOutput.ValidationError.Contains(
                "Ausgänge können nicht doppelt verschaltet werden",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Duplicate PLC_OUT slots must be rejected precisely.");
        }

        var other = CreateContainerWithDuplicateSlot("InternalSlot");
        other.Validate();
        if (other.IsValid ||
            !other.ValidationError.Contains("ausschließlich für PLC_IN_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Non-PLC duplicate slots must remain invalid.");
        }
    }

    private static ContainerData CreateContainerWithDuplicateSlot(string slot) => new()
    {
        Component = "MultiplicityTest",
        Type = "Conveyor",
        DataList = new([
            new ContainerEntry { Signal = "SignalA", Address = "%I0.0", DataType = "Bool", Slot = slot },
            new ContainerEntry { Signal = "SignalB", Address = "%I0.1", DataType = "Bool", Slot = slot }
        ])
    };

    private static void ValidatePlcInputFanInParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vibn-fanin-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, """
                <ContainerFile>
                  <Container id="1">
                    <Component>FanInConveyor</Component>
                    <Type>Conveyor</Type>
                    <DataList>
                      <Entry><ID>1</ID><Address>%I0.0</Address><DataType>Bool</DataType><Signal>ReadyA</Signal><Slot>PLC_IN_StatusWord</Slot></Entry>
                      <Entry><ID>2</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>ReadyB</Signal><Slot>PLC_IN_StatusWord</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);

            var (containers, unknownSignals) = ContainerToFeeService.ReadInContainerXmlData(path);
            var container = containers.Single();
            var fanIn = container.GetAdditionalInputFanIns().Single();
            if (unknownSignals.Count != 0 ||
                fanIn.SlotName != "PLC_IN_StatusWord" ||
                fanIn.Signals.Count != 2 ||
                container.CountNonNullSignals() != 2)
            {
                throw new InvalidOperationException("PLC_IN fan-in parsing lost one or more signals.");
            }

            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("PLC_IN_StatusWord", "PLC_OUT_ControlWord", StringComparison.Ordinal));
            try
            {
                _ = ContainerToFeeService.ReadInContainerXmlData(path);
                throw new InvalidOperationException("Duplicate PLC_OUT XML was not rejected.");
            }
            catch (InvalidDataException exception) when (
                exception.Message.Contains("Ausgänge können nicht doppelt verschaltet werden", StringComparison.Ordinal))
            {
            }

            File.WriteAllText(path, """
                <ContainerFile>
                  <Container id="1">
                    <Component>FanInCylinder</Component>
                    <Type>Cylinder</Type>
                    <DataList>
                      <Entry><ID>1</ID><Address>%I0.0</Address><DataType>Bool</DataType><Signal>HomeA</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                      <Entry><ID>2</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>HomeB</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);
            var (listMappedContainers, _) = ContainerToFeeService.ReadInContainerXmlData(path);
            var cylinder = (GrobCylinder_Container)listMappedContainers.Single();
            if (cylinder.Signals_InHomePos.Count != 2 || cylinder.GetAdditionalInputFanIns().Count != 0)
            {
                throw new InvalidOperationException(
                    "Existing PLC_IN list mapping must keep both signals without a duplicate fan-in pass.");
            }

            File.WriteAllText(path, """
                <ContainerFile>
                  <Container id="1">
                    <Component>FanInCylinderBothPositions</Component>
                    <Type>Cylinder</Type>
                    <DataList>
                      <Entry><ID>1</ID><Address>%Q0.0</Address><DataType>Bool</DataType><Signal>ToHome</Signal><Slot>PLC_OUT_ToHomePos</Slot></Entry>
                      <Entry><ID>2</ID><Address>%Q0.1</Address><DataType>Bool</DataType><Signal>ToWork</Signal><Slot>PLC_OUT_ToWorkPos</Slot></Entry>
                      <Entry><ID>3</ID><Address>%I0.0</Address><DataType>Bool</DataType><Signal>HomeA</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                      <Entry><ID>4</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>HomeB</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                      <Entry><ID>5</ID><Address>%I0.2</Address><DataType>Bool</DataType><Signal>WorkA</Signal><Slot>PLC_IN_InWorkPos</Slot></Entry>
                      <Entry><ID>6</ID><Address>%I0.3</Address><DataType>Bool</DataType><Signal>WorkB</Signal><Slot>PLC_IN_InWorkPos</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);
            var (completeCylinderContainers, _) = ContainerToFeeService.ReadInContainerXmlData(path);
            var completeCylinder = (GrobCylinder_Container)completeCylinderContainers.Single();
            completeCylinder.IsCreationRequested = true;
            var preflight = ContainerModelValidationPreflight.Validate(completeCylinder);
            if (preflight.Any(issue => issue.Code == "CYLINDER_STATUS_MISSING") ||
                completeCylinder.Signals_InHomePos.Count != 2 ||
                completeCylinder.Signals_InWorkPos.Count != 2)
            {
                throw new InvalidOperationException(
                    "Multiple InHomePos/InWorkPos inputs were incorrectly reported as an incomplete cylinder status mapping.");
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void ValidateWorkspacePersistenceAndAutoSaveSettings()
    {
        var settings = new ContainerGenerationSettings
        {
            AutoSaveEnabled = true,
            AutoSaveIntervalMinutes = 17,
        };
        var restoredSettings = new ContainerGenerationSettings();
        if (!restoredSettings.SetSettings(settings.GetSettings()) ||
            !restoredSettings.AutoSaveEnabled ||
            restoredSettings.AutoSaveIntervalMinutes != 17)
        {
            throw new InvalidOperationException("Container Generation autosave settings were not restored.");
        }

        var path = Path.Combine(Path.GetTempPath(), $"vibn-workspace-{Guid.NewGuid():N}.xml");
        try
        {
            var data = new SavedData { FilePath = path };
            data.CaptureEntryStates();
            data.SetSettings();
            var restored = SavedData.DeserializeProject(path);
            if (restored.ContainerList.Count != 0 || restored.FilePath != path)
                throw new InvalidOperationException("Container Generation workspace round-trip failed.");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void ValidateContainerFileComparison()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-container-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var baselinePath = Path.Combine(directory, "baseline.xml");
        var candidatePath = Path.Combine(directory, "candidate.xml");
        try
        {
            File.WriteAllText(baselinePath, BuildContainerFileXml("%I0.0", "PLC_IN_Old", includeRemoved: true, includeAdded: false));
            File.WriteAllText(candidatePath, BuildContainerFileXml("%I0.7", "PLC_IN_New", includeRemoved: false, includeAdded: true));

            var baseline = ContainerFileWorkspaceReader.Read(baselinePath);
            var candidate = ContainerFileWorkspaceReader.Read(candidatePath);
            if (baseline.Containers.Count != 1 || baseline.UnassignedSignals.Count != 1)
                throw new InvalidOperationException("ContainerFile reader did not separate the unknown container.");

            var candidateContainers = candidate.Containers.ToList();
            var candidateUnassigned = candidate.UnassignedSignals.ToList();
            var filtered = new List<ContainerEntry>();
            var summary = GenerationWorkspaceReconciler.Reconcile(
                GenerationWorkspaceReconciler.Capture(
                    baseline.Containers,
                    baseline.UnassignedSignals,
                    []),
                candidateContainers,
                candidateUnassigned,
                filtered,
                new ComparisonRequirements());

            var kinds = summary.Differences.Select(item => item.Kind).ToHashSet();
            if (!kinds.Contains(ReimportChangeKind.SourceChanged) ||
                !kinds.Contains(ReimportChangeKind.RuleSuggestionChanged) ||
                !kinds.Contains(ReimportChangeKind.NewFromSource) ||
                !kinds.Contains(ReimportChangeKind.RemovedFromSource))
            {
                throw new InvalidOperationException("ContainerFile comparison missed add/remove/source/slot changes.");
            }

            foreach (var difference in summary.Differences)
                difference.IsAccepted = difference.Kind is not ReimportChangeKind.RemovedFromSource;
            GenerationWorkspaceReconciler.ApplyDecisions(
                summary,
                candidateContainers,
                candidateUnassigned,
                filtered);

            var entries = candidateContainers.SelectMany(item => item.DataList).ToArray();
            var changed = entries.Single(item => item.ID == "A");
            if (changed.Address != "%I0.7" || changed.Slot != "PLC_IN_New" ||
                entries.All(item => item.ID != "B") || entries.All(item => item.ID != "C"))
            {
                throw new InvalidOperationException("Selective ContainerFile comparison decisions were not applied.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ValidateFee2ContainerProvenanceRoundTripAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-fee-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.xml");
        var exportedPath = Path.Combine(directory, "exported.xml");
        try
        {
            File.WriteAllText(sourcePath, """
                <ContainerFile>
                  <Container id="1">
                    <Component>FanInCylinder</Component><Type>Cylinder</Type><DataList>
                      <Entry><ID>A</ID><Address>%I0.0</Address><DataType>Bool</DataType><Signal>HomeA</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                      <Entry><ID>B</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>HomeB</Signal><Slot>PLC_IN_InHomePos</Slot></Entry>
                    </DataList>
                  </Container>
                  <Container id="2">
                    <Component>ExcludedSensor</Component><Type>Sensor</Type><DataList>
                      <Entry><ID>C</ID><Address>%I0.2</Address><DataType>Bool</DataType><Signal>Detected</Signal><Slot>PLC_IN_PartPresent_Ch1</Slot></Entry>
                    </DataList>
                  </Container>
                </ContainerFile>
                """);

            var planService = new ContainerToFeeVisualPlanService();
            var loaded = await planService.LoadXmlAsync(sourcePath);
            if (!loaded.Success || loaded.Plan is null)
                throw new InvalidOperationException(
                    $"Round-trip plan could not be parsed: {loaded.Message}; " +
                    string.Join(" | ", loaded.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
            var selectedId = loaded.Plan.Nodes
                .Single(node => node.Kind == VisualNodeKind.Container && node.Name == "FanInCylinder")
                .Id;
            var source = XDocument.Load(sourcePath);
            var variableA = Guid.NewGuid();
            var variableB = Guid.NewGuid();
            var encoded = FeeContainerProvenanceCodec.Create(
                source,
                new HashSet<string>(StringComparer.Ordinal) { selectedId },
                loaded.Plan.SourceFingerprint,
                new Dictionary<string, IReadOnlyList<FeeContainerSignalSource>>(StringComparer.Ordinal)
                {
                    [selectedId] =
                    [
                        new("A", "HomeA", "%I0.0", "Bool", variableA),
                        new("B", "HomeB", "%I0.1", "Bool", variableB),
                    ]
                });
            if (!FeeContainerProvenanceCodec.TryRead(encoded.Tags, out var decoded, out var error) ||
                decoded is null)
            {
                throw new InvalidOperationException($"Provenance could not be decoded: {error}");
            }
            if (decoded.ContainerCount != 1 || decoded.SignalCount != 2 ||
                decoded.SignalBindings.Count != 2 ||
                decoded.SourceFingerprint != loaded.Plan.SourceFingerprint)
            {
                throw new InvalidOperationException("Provenance selection or counters changed during round-trip.");
            }

            var projection = FeeContainerVariableProjector.Apply(
                decoded,
                [
                    new(variableA, "HomeA_Renamed", "%I7.0", string.Empty, "Bool", "A-NEW"),
                    new(variableB, "HomeB", string.Empty, "GVL_IO.HomeB", "Bool", "B"),
                ],
                new Dictionary<Guid, string>
                {
                    [variableA] = "PLC_IN_InWorkPos",
                    [variableB] = "PLC_IN_InWorkPos"
                });
            if (projection.UpdatedEntries != 2 || projection.MissingVariableGuids.Count != 0 ||
                projection.UpdatedSlots != 2 || projection.UnresolvedSlotVariableGuids.Count != 0)
                throw new InvalidOperationException("Current FEE variable values were not projected completely.");

            FeeContainerProvenanceCodec.SaveAtomically(projection.Snapshot, exportedPath);
            var (containers, unknownSignals) = ContainerToFeeService.ReadInContainerXmlData(exportedPath);
            var cylinder = (GrobCylinder_Container)containers.Single();
            if (unknownSignals.Count != 0 ||
                cylinder.ComponentName != "FanInCylinder" ||
                cylinder.Signals_InWorkPos.Count != 2 ||
                cylinder.Signals_InWorkPos[0].Tag != "HomeA_Renamed" ||
                cylinder.Signals_InWorkPos[0].Address != "%I7.0" ||
                cylinder.Signals_InWorkPos[0].Comment != "A-NEW" ||
                cylinder.Signals_InWorkPos[1].Path != "GVL_IO.HomeB")
            {
                throw new InvalidOperationException(
                    "Container → provenance → Container lost the selected container or PLC_IN fan-in.");
            }

            var combined = await new Fee2ContainerService().CreateCombinedExportAsync([
                new Fee2ContainerRoot(Guid.NewGuid(), "Root A", projection.Snapshot, 2, 0, 2, 0),
                new Fee2ContainerRoot(Guid.NewGuid(), "Root B", projection.Snapshot, 2, 0, 2, 0),
            ]);
            if (combined.Snapshot.ContainerCount != 2 ||
                combined.Snapshot.SignalBindings.Count != 4 ||
                combined.Snapshot.SignalBindings.Max(binding => binding.ContainerIndex) != 1)
            {
                throw new InvalidOperationException(
                    "Multi-root FEE2Container export did not merge only the selected root snapshots correctly.");
            }

            var damagedTags = encoded.Tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            damagedTags[FeeContainerProvenanceCodec.HashKey] = new string('0', 64);
            if (FeeContainerProvenanceCodec.TryRead(damagedTags, out _, out _))
                throw new InvalidOperationException("Damaged provenance checksum was accepted.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ValidateFee2ContainerVisualTypeCoverageAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-fee-types-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "all-visual-types.xml");
        try
        {
            var supportedTypes = FeeContainerLiveReconstructor.SupportedContainerTypes;
            var source = new XDocument(
                new XElement("ContainerFile",
                    supportedTypes.Select((type, index) =>
                        new XElement("Container",
                            new XAttribute("id", $"type-{index}"),
                            new XElement("Component", $"Component_{type}"),
                            new XElement("Type", type),
                            new XElement("DataList")))));
            source.Save(sourcePath);

            var planService = new ContainerToFeeVisualPlanService();
            var loaded = await planService.LoadXmlAsync(sourcePath);
            if (!loaded.Success || loaded.Plan is null)
                throw new InvalidOperationException(
                    $"The complete Container2FEE Visual type catalog could not be parsed: {loaded.Message}");
            var selectedIds = loaded.Plan.Nodes
                .Where(node => node.Kind == VisualNodeKind.Container)
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (selectedIds.Count != supportedTypes.Count ||
                loaded.Issues.Any(issue => issue.Code == "CONTAINER_TYPE_UNKNOWN"))
            {
                throw new InvalidOperationException(
                    "Container2FEE Visual and FEE2Container expose different supported type sets.");
            }

            var encoded = FeeContainerProvenanceCodec.Create(
                source,
                selectedIds,
                sourceFingerprint: "all-visual-types");
            if (!FeeContainerProvenanceCodec.TryRead(encoded.Tags, out var decoded, out var error) ||
                decoded is null)
            {
                throw new InvalidOperationException(
                    $"The all-type FEE2Container provenance could not be decoded: {error}");
            }

            var roundTrippedTypes = decoded.ContainerDocument.Descendants("Container")
                .Select(container => container.Element("Type")?.Value ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (decoded.ContainerCount != supportedTypes.Count ||
                !roundTrippedTypes.SetEquals(supportedTypes))
            {
                throw new InvalidOperationException(
                    "FEE2Container did not retain every type generated by Container2FEE Visual.");
            }

            var (runtimeContainers, unknownSignals) =
                ContainerToFeeService.ReadInContainerXmlData(decoded.ContainerDocument);
            if (runtimeContainers.Count != supportedTypes.Count || unknownSignals.Count != 0)
            {
                throw new InvalidOperationException(
                    "The shared Container2FEE runtime parser does not cover the complete visual type catalog.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateRuleSuggestionWorkflow()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-rule-suggestions-{Guid.NewGuid():N}");
        var reviewsPath = Path.Combine(directory, "reviews.json");
        try
        {
            var logger = new ActionLogger(directory);
            LogSlotCorrection(logger, "SIG-A", "Ready", "PLC_IN_Old", "PLC_IN_New", "source-1");
            LogSlotCorrection(logger, "SIG-B", "Ready", "PLC_IN_Old", "PLC_IN_New", "source-2");
            LogSlotCorrection(logger, "SIG-C", "Ready", "PLC_IN_Old", "PLC_IN_Alternative", "source-3");
            var other = new ContainerEntry
            {
                SignalId = "SIG-D",
                Signal = "Ready",
                Slot = "PLC_IN_New",
                Address = "%I0.0"
            };
            logger.LogPropertyChange(
                "Cylinder_1", "Cylinder", other, nameof(ContainerEntry.Address),
                "%I0.0", "%I0.7", "source-4");

            var analysis = new RuleSuggestionService().Analyze(
                Directory.GetFiles(directory, "*.jsonl"));
            var preferred = analysis.Suggestions.Single(suggestion => suggestion.NewValue == "PLC_IN_New");
            if (analysis.ParsedEvents != 4 || analysis.InvalidLines != 0 ||
                analysis.Suggestions.Count != 2 || preferred.Frequency != 2 ||
                preferred.RelevantCases != 3 || Math.Abs(preferred.Confidence - (2d / 3d)) > 0.0001)
            {
                throw new InvalidOperationException("Deterministic rule-suggestion confidence is incorrect.");
            }

            var store = new RuleSuggestionReviewStore(reviewsPath);
            store.Save(new Dictionary<string, RuleSuggestionStatus>
            {
                [preferred.Id] = RuleSuggestionStatus.Accepted
            });
            var reviewed = new RuleSuggestionService().Analyze(
                Directory.GetFiles(directory, "*.jsonl"), store.Load());
            if (reviewed.Suggestions.Single(item => item.Id == preferred.Id).Status !=
                RuleSuggestionStatus.Accepted)
            {
                throw new InvalidOperationException("Rule-suggestion review status was not persisted.");
            }

            var json = File.ReadAllText(Directory.GetFiles(directory, "*.jsonl").Single());
            if (!json.Contains("\"SchemaVersion\":2", StringComparison.Ordinal) ||
                !json.Contains("\"TimestampUtc\":", StringComparison.Ordinal) ||
                !json.Contains("\"SourceKey\":", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Structured action-log schema metadata is missing.");
            }

            var legacyPath = Path.Combine(directory, "legacy.log");
            File.WriteAllText(legacyPath, """
                {"SignalId":"LEGACY-1","SignalText":"LegacyReady","FromContainer":"C1","FromComponentType":"Cylinder","FromSlot":"PLC_IN_A","ToContainer":"C1","ComponentType":"Cylinder","ToSlot":"PLC_IN_B","RuleSuggestion":"","MlTop1":null,"MlTop1Score":0}
                """);
            var legacy = new RuleSuggestionService().Analyze([legacyPath]);
            if (legacy.ParsedEvents != 1 || legacy.Suggestions.Count != 1 ||
                legacy.Suggestions[0].PreviousValue != "PLC_IN_A" ||
                legacy.Suggestions[0].NewValue != "PLC_IN_B")
            {
                throw new InvalidOperationException("Schema-1 action logs are no longer readable.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void LogSlotCorrection(
        ActionLogger logger,
        string signalId,
        string signal,
        string oldSlot,
        string newSlot,
        string sourceKey)
    {
        var entry = new ContainerEntry
        {
            SignalId = signalId,
            Signal = signal,
            Slot = newSlot
        };
        logger.LogSlotChange(
            "Cylinder_1", "Cylinder", entry, oldSlot, null, null, sourceKey);
    }

    private static async Task ValidateRequirementsRulePatchWorkflowAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibn-requirements-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var requirementsPath = Path.Combine(directory, "AutoCreate.xml");
        try
        {
            File.WriteAllText(requirementsPath, """
                <AutoCreate>
                  <Components>
                    <Component name="Cylinder" type="Cylinder">
                      <Slots>
                        <Slot name="PLC_IN_Old">
                          <Keygroup type="required"><KeySet><Key keep="true">Ready</Key></KeySet></Keygroup>
                        </Slot>
                        <Slot name="PLC_IN_New">
                          <Keygroup type="required"><KeySet><Key keep="true">Target</Key></KeySet></Keygroup>
                        </Slot>
                      </Slots>
                    </Component>
                  </Components>
                  <FilterList />
                </AutoCreate>
                """);
            var suggestion = new RuleSuggestion(
                "0123456789abcdef",
                "Ready correction",
                "Cylinder",
                "Ready",
                "Slot",
                "PLC_IN_Old",
                "PLC_IN_New",
                3,
                3,
                1,
                RuleSuggestionStatus.Accepted);
            var service = new RequirementsRulePatchService();
            var plan = service.CreatePlan(requirementsPath, [suggestion]);
            if (!plan.UpdatedXml.Contains("match=\"exact\"", StringComparison.Ordinal) ||
                !plan.Preview.Contains("PLC_IN_Old' -> 'PLC_IN_New", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Requirements patch preview misses the exact override.");
            }

            var rejectedUnconfirmedWrite = false;
            try
            {
                service.Apply(plan, explicitlyConfirmed: false);
            }
            catch (InvalidOperationException)
            {
                rejectedUnconfirmedWrite = true;
            }
            if (!rejectedUnconfirmedWrite)
                throw new InvalidOperationException("An unconfirmed requirements patch was written.");

            var applyResult = service.Apply(plan, explicitlyConfirmed: true);
            if (!File.Exists(applyResult.BackupPath) || applyResult.AppliedRules != 1)
                throw new InvalidOperationException("Requirements patch backup was not created.");

            var requirements = new RequirementsXml();
            var read = requirements.ReadFromFile(requirementsPath);
            if (!read.IsSuccess)
                throw new InvalidOperationException("Patched requirements no longer validate.");
            var generator = new ContainerGenerator();
            var generated = await generator.GenerateAsync(new ContainerGenerationRequest(
                [new ContainerEntry { Signal = "Ready", SignalId = "exact-ready" }],
                read.Value,
                [new GroupingRule { TargetField = match => match.ContainerName, GroupOrder = 0 }],
                null,
                IgnoreCase: true,
                UseFilterList: true));
            var assigned = generated.Containers.SelectMany(container => container.DataList).Single();
            if (assigned.Slot != "PLC_IN_New" || generated.UnassignedSignals.Count != 0)
                throw new InvalidOperationException("Exact requirements override did not reroute the signal.");

            var revisedSuggestion = suggestion with
            {
                Id = "fedcba9876543210",
                NewValue = "PLC_IN_Alternative"
            };
            var revisedPlan = service.CreatePlan(requirementsPath, [revisedSuggestion]);
            service.Apply(revisedPlan, explicitlyConfirmed: true);
            var revisedDocument = XDocument.Load(requirementsPath);
            var generatedOverrides = revisedDocument.Descendants("Component")
                .Where(component => component.Attribute("name")?.Value
                    .StartsWith("VIBN AI exact override", StringComparison.Ordinal) == true)
                .ToArray();
            if (generatedOverrides.Length != 1 ||
                generatedOverrides[0].Descendants("Slot").Single().Attribute("name")?.Value !=
                    "PLC_IN_Alternative")
            {
                throw new InvalidOperationException("A revised exact override left a competing stale rule behind.");
            }

            var stalePlan = service.CreatePlan(requirementsPath, [revisedSuggestion]);
            File.AppendAllText(requirementsPath, Environment.NewLine);
            var rejectedStaleWrite = false;
            try
            {
                service.Apply(stalePlan, explicitlyConfirmed: true);
            }
            catch (InvalidOperationException)
            {
                rejectedStaleWrite = true;
            }
            if (!rejectedStaleWrite)
                throw new InvalidOperationException("A stale requirements preview overwrote a changed file.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string BuildContainerFileXml(
        string address,
        string slot,
        bool includeRemoved,
        bool includeAdded) => $"""
            <CAAMergeResult><ContainerList>
              <Container id="1"><Component>Sensor_1</Component><Type>Sensor</Type><DataList>
                <Entry><ID>A</ID><Address>{address}</Address><DataType>Bool</DataType><Signal>Ready</Signal><Slot>{slot}</Slot><Note /></Entry>
                {(includeRemoved ? "<Entry><ID>B</ID><Address>%I0.1</Address><DataType>Bool</DataType><Signal>Old</Signal><Slot>PLC_IN_Old</Slot><Note /></Entry>" : string.Empty)}
                {(includeAdded ? "<Entry><ID>C</ID><Address>%I0.2</Address><DataType>Bool</DataType><Signal>New</Signal><Slot>PLC_IN_New</Slot><Note /></Entry>" : string.Empty)}
              </DataList></Container>
              <Container id="unknown"><Component>unknown</Component><Type>unknown</Type><DataList>
                <Entry><ID>U</ID><Address>%I9.0</Address><DataType>Bool</DataType><Signal>Unknown</Signal><Slot /><Note /></Entry>
              </DataList></Container>
            </ContainerList></CAAMergeResult>
            """;

    private sealed class ComparisonRequirements : IRequirementsXml
    {
        public string XmlSchema => string.Empty;
        public XDocument Document { get; } = new();
        public bool IsInitialized => true;
        public Result<XDocument> ReadFromFile(string filePath) => Result<XDocument>.Failure("Not supported in test.");
        public Task<Result<XDocument>> ReadFromFileAsync(string filePath) => Task.FromResult(ReadFromFile(filePath));
        public int? GetMinSignals(string componentName) => null;
        public int? GetMaxSignals(string componentName) => null;
        public List<string> GetSlotNames(string componentName) => ["PLC_IN_Old", "PLC_IN_New"];
        public List<string> GetComponentTypes() => ["Sensor"];
    }

    private static async Task ValidateImportAndGenerationAsync(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("ZuLi-Testdatei fehlt.", file);

        var zuli = new ZuLiDefault();
        var import = await zuli.ReadFromFileAsync(file);
        if (!import.IsSuccess)
            throw new InvalidOperationException($"Import von '{file}' fehlgeschlagen: {import.ErrorMessage}");
        if (import.Value.Count == 0)
            throw new InvalidOperationException($"Import von '{file}' lieferte keine Signale.");

        // The empty requirements model validates the complete XLSX-to-generator
        // hand-off without pretending that a customer component mapping exists.
        var requirements = XDocument.Parse("<AutoCreate><FilterList /></AutoCreate>");
        var generator = new ContainerGenerator();
        var generation = await generator.GenerateAsync(
            new ContainerGenerationRequest(
                import.Value,
                requirements,
                Array.Empty<GroupingRule>(),
                null,
                IgnoreCase: true,
                UseFilterList: false));

        if (generation.Statistics.TotalSignals != import.Value.Count ||
            generation.UnassignedSignals.Count != import.Value.Count)
        {
            throw new InvalidOperationException(
                $"Generatorübergabe für '{file}' ist inkonsistent: " +
                $"Import={import.Value.Count}, Total={generation.Statistics.TotalSignals}, " +
                $"Unassigned={generation.UnassignedSignals.Count}.");
        }

        Console.WriteLine(
            $"{Path.GetFileName(file)}: {import.Value.Count} Signale erfolgreich eingelesen und verarbeitet.");
    }
}

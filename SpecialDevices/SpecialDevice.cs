using FS.SDK.Io;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Services;
using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.SpecialDevices
{
    public enum ExistingSpecialDeviceState
    {
        Missing,
        Unchanged,
        AddressesUpdated,
        RequiresReview
    }

    public sealed record ExistingSpecialDeviceSyncResult(
        ExistingSpecialDeviceState State,
        int UpdatedSignals,
        string Message);

    public abstract class SpecialDevice
    {

        // Special Device Properties


        public DeviceManufacturer DeviceManufacturer { get; set; }
        public Enum DeviceType { get; set; }
        public string DevicePrefix { get; set; }
        public SpecialDeviceAddresses DeviceAddresses { get; set; }
        public IReadOnlyList<FeeInterfaceSignal> DeviceSignals { get; set; }
        public IReadOnlyList<FeeInterfaceSignal> DeviceParameters { get; set; }

        public RobotType? RobotType { get; set; }

        /// <summary>Non-fatal persistence warning from the last completed creation.</summary>
        public string LastCreationWarning { get; private set; } = string.Empty;

        public string QueuePrefix
        {
            get => DevicePrefix;
            set => Reconfigure(value, DeviceAddresses.Input, DeviceAddresses.Output);
        }

        public int QueueInputByte
        {
            get => DeviceAddresses.Input;
            set => Reconfigure(DevicePrefix, value, DeviceAddresses.Output);
        }

        public int QueueOutputByte
        {
            get => DeviceAddresses.Output;
            set => Reconfigure(DevicePrefix, DeviceAddresses.Input, value);
        }


        public FeeLogic DeviceLogicObject { get; set; }
        public FeeBasicFrame DeviceBasicFrame { get; set; }

        public FeeInterface DeviceInterface { get; set; }

        //public Guid InterfaceGuid { get; set; }





        public SpecialDevice(string prefix, SpecialDeviceAddresses addresses, DeviceManufacturer manufacturer, Enum deviceType)
        {
            DevicePrefix = prefix;
            DeviceAddresses = addresses;
            DeviceManufacturer = manufacturer;
            DeviceType = deviceType;

        }


        public SpecialDevice(string prefix, SpecialDeviceAddresses addresses, DeviceManufacturer manufacturer, Enum deviceType, RobotType robotType)
            : this(prefix, addresses, manufacturer, deviceType)
        {
            RobotType = robotType;
        }






        // Calculate Signals from SignalDefinitions
        protected virtual IEnumerable<SignalDefinition> DefineSignals() => Enumerable.Empty<SignalDefinition>();

        protected void InitializeSignals()
        {
            DeviceSignals = DefineSignals()
                .Select(def => new FeeInterfaceSignal(
                    tag: GenerateTag(DevicePrefix, def.Name),
                    address: CalculateAddress(DeviceAddresses, def.Offset, def.Mode, def.Type),
                    usage: def.Mode.ToString(),
                    type: def.Type.ToString(),
                    comment: def.Comment
                ))
                .ToList();
        }

        private void Reconfigure(string? prefix, int input, int output)
        {
            var normalizedPrefix = (prefix ?? string.Empty).Trim();
            if (normalizedPrefix.Length == 0)
                return;
            DevicePrefix = normalizedPrefix;
            DeviceAddresses = new SpecialDeviceAddresses(input, output);
            // SimMode owns a symbolic, hand-written signal list and therefore
            // has no catalog definitions to rebuild.
            if (DefineSignals().Any())
                InitializeSignals();
            var objectName = DevicePrefix + " (" + DeviceType + ")";
            if (DeviceLogicObject is not null)
                DeviceLogicObject.Name = objectName;
            if (DeviceBasicFrame is not null &&
                !string.Equals(DeviceBasicFrame.Name, "SimMode", StringComparison.OrdinalIgnoreCase))
                DeviceBasicFrame.Name = objectName;
        }


        protected virtual string CalculateAddress(SpecialDeviceAddresses baseAddresses, double offset, IOMode ioMode, IOType ioType)
        {
            return PlcAddressCalculator.Calculate(baseAddresses, offset, ioMode, ioType);
        }

        protected static string GenerateTag(string Prefix, string Tag)
        {
            return Prefix + "_" + Tag;
        }




        // Create Special Device
        public async Task<bool> ExistsInFeeAsync()
        {
            var expectedName = DeviceBasicFrame?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(expectedName))
                return false;

            var guidTexts = (await ApiInstance.Object
                    .GetSceneObjectGuidsOfTypeAsync(nameof(FS.SDK.Scene.Objects.BasicFrame)))
                .ToArray();
            if (guidTexts.Length == 0)
                return false;

            var names = (await ApiInstance.Object.GetPropertiesAsync(
                    guidTexts,
                    nameof(FS.SDK.SceneObject.Name)))
                .Select(ApiInstance.XmlHelper.ConvertToString)
                .ToArray();
            var matchingRoots = guidTexts
                .Zip(names, (guid, name) => (GuidText: guid, Name: name))
                .Where(item => string.Equals(
                    item.Name?.Trim(),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => Guid.TryParse(item.GuidText, out _))
                .ToArray();
            if (matchingRoots.Length == 0)
                return false;

            var definitions = await ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync();
            var expectedDefinitionGuid = definitions
                .Where(definition => string.Equals(
                    definition.Name,
                    DeviceLogicObject?.LogicDefinitionName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(definition => Guid.TryParse(definition.Guid, out var guid) ? guid : Guid.Empty)
                .FirstOrDefault(guid => guid != Guid.Empty);

            foreach (var root in matchingRoots)
            {
                var rootGuid = Guid.Parse(root.GuidText);
                try
                {
                    var tags = await FeeTagPropertyStore.ReadAsync(rootGuid);
                    if (FeeSpecialDeviceProvenanceCodec.TryRead(tags, out _, out _))
                        return true;
                }
                catch
                {
                    // Older models do not necessarily have a TagComponent.
                    // Fall back to the known device logic below.
                }

                if (expectedDefinitionGuid == Guid.Empty)
                    continue;
                var children = (await ApiInstance.Object
                        .GetAllChildrenFromSceneObjectAsync(root.GuidText))
                    .Where(value => Guid.TryParse(value, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (children.Length == 0)
                    continue;
                var xmlTexts = await ApiInstance.Object.GetSceneObjectsAsXmlAsync(children);
                if (xmlTexts.Any(xml =>
                {
                    try
                    {
                        var value = System.Xml.Linq.XElement.Parse(xml)
                            .Element("Logic")?
                            .Element("PersistedLogicGuid")?
                            .Value;
                        return Guid.TryParse(value, out var guid) && guid == expectedDefinitionGuid;
                    }
                    catch (System.Xml.XmlException)
                    {
                        return false;
                    }
                }))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reuses a generated device with the same root name. Current variables
        /// are read from FEE, compared by their persisted signal GUID and updated
        /// in place when only their addresses changed. Ambiguous legacy devices
        /// are never modified automatically.
        /// </summary>
        public async Task<ExistingSpecialDeviceSyncResult> SynchronizeExistingAsync()
        {
            LastCreationWarning = string.Empty;
            var expectedName = DeviceBasicFrame?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(expectedName))
                return new ExistingSpecialDeviceSyncResult(ExistingSpecialDeviceState.Missing, 0, string.Empty);

            var guidTexts = (await ApiInstance.Object
                    .GetSceneObjectGuidsOfTypeAsync(nameof(FS.SDK.Scene.Objects.BasicFrame)))
                .ToArray();
            var names = guidTexts.Length == 0
                ? []
                : (await ApiInstance.Object.GetPropertiesAsync(guidTexts, nameof(FS.SDK.SceneObject.Name)))
                    .Select(ApiInstance.XmlHelper.ConvertToString)
                    .ToArray();
            var matchingRoots = guidTexts
                .Zip(names, (guid, name) => (GuidText: guid, Name: name))
                .Where(item => string.Equals(item.Name?.Trim(), expectedName, StringComparison.OrdinalIgnoreCase))
                .Where(item => Guid.TryParse(item.GuidText, out _))
                .ToArray();
            if (matchingRoots.Length == 0)
                return new ExistingSpecialDeviceSyncResult(ExistingSpecialDeviceState.Missing, 0, string.Empty);
            if (matchingRoots.Length > 1)
            {
                return new ExistingSpecialDeviceSyncResult(
                    ExistingSpecialDeviceState.RequiresReview,
                    0,
                    $"{matchingRoots.Length} gleichnamige FEE-Geräte wurden gefunden; die Adressen werden nicht automatisch geändert.");
            }

            var rootGuid = Guid.Parse(matchingRoots[0].GuidText);
            FeeSpecialDeviceSnapshot? storedSnapshot = null;
            try
            {
                var tags = await FeeTagPropertyStore.ReadAsync(rootGuid);
                FeeSpecialDeviceProvenanceCodec.TryRead(tags, out storedSnapshot, out _);
            }
            catch
            {
                // An older device can exist without readable provenance. It is
                // intentionally left untouched because its variables cannot be
                // assigned to this device with sufficient certainty.
            }

            if (storedSnapshot is null)
            {
                return new ExistingSpecialDeviceSyncResult(
                    ExistingSpecialDeviceState.RequiresReview,
                    0,
                    "Das gleichnamige Gerät besitzt keine eindeutige SpecialDevices2FEE-Provenienz; " +
                    "eine automatische Adressänderung wäre unsicher.");
            }

            var desiredSignals = (DeviceSignals ?? Array.Empty<FeeInterfaceSignal>()).ToArray();
            var storedByTag = (storedSnapshot.Signals ?? Array.Empty<FeeSpecialDeviceSignalSnapshot>())
                .Where(signal => !string.IsNullOrWhiteSpace(signal.Tag))
                .GroupBy(signal => signal.Tag, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            if (desiredSignals.Any(signal =>
                    string.IsNullOrWhiteSpace(signal.Tag) ||
                    !storedByTag.ContainsKey(signal.Tag)))
            {
                return new ExistingSpecialDeviceSyncResult(
                    ExistingSpecialDeviceState.RequiresReview,
                    0,
                    "Die aktuelle Gerätedefinition und die gespeicherten FEE-Signale unterscheiden sich. " +
                    "Fehlende oder mehrdeutige Signale werden nicht automatisch erzeugt.");
            }

            var interfaces = await FeeInterface.GetAllInterfacesAsync();
            var currentByGuid = interfaces
                .SelectMany(feeInterface => (feeInterface.Signals ?? []).Select(signal =>
                    (Signal: signal, Interface: feeInterface)))
                .GroupBy(item => item.Signal.Guid)
                .ToDictionary(group => group.Key, group => group.First());

            var updates = new List<(FeeInterfaceSignal Desired, FeeInterface Interface)>();
            foreach (var desired in desiredSignals)
            {
                var stored = storedByTag[desired.Tag];
                if (!currentByGuid.TryGetValue(stored.VariableGuid, out var current))
                {
                    return new ExistingSpecialDeviceSyncResult(
                        ExistingSpecialDeviceState.RequiresReview,
                        updates.Count,
                        $"Das bestehende Signal '{desired.Tag}' ({stored.VariableGuid}) ist in FEE nicht mehr vorhanden.");
                }

                desired.Guid = current.Signal.Guid;
                desired.ParentInterface = current.Interface;
                var addressChanged = !string.Equals(
                    current.Signal.Address?.Trim(),
                    desired.Address?.Trim(),
                    StringComparison.OrdinalIgnoreCase);
                if (addressChanged)
                    updates.Add((desired, current.Interface));
            }

            foreach (var update in updates)
            {
                update.Desired.ReuseExistingWithoutUpdate = false;
                if (!await update.Desired.CreateSignalAsync(update.Interface))
                {
                    return new ExistingSpecialDeviceSyncResult(
                        ExistingSpecialDeviceState.RequiresReview,
                        updates.IndexOf(update),
                        $"FEE hat die Adressänderung für '{update.Desired.Tag}' nicht bestätigt.");
                }
            }

            DeviceBasicFrame.Guid = rootGuid;
            var provenance = FeeSpecialDeviceProvenanceCodec.Encode(FeeSpecialDeviceProvenanceCodec.Create(this));
            var tagWrite = await FeeTagPropertyStore.TryWriteAndVerifyAsync(
                rootGuid,
                provenance,
                preserveExisting: true,
                verifyAfterWrite: false);
            if (!tagWrite.Confirmed)
                LastCreationWarning = tagWrite.Warning;
            else
                ApiInstance.Object.Send(rootGuid);

            return updates.Count == 0
                ? new ExistingSpecialDeviceSyncResult(
                    ExistingSpecialDeviceState.Unchanged,
                    0,
                    "Gerät und Signaladressen sind bereits aktuell.")
                : new ExistingSpecialDeviceSyncResult(
                    ExistingSpecialDeviceState.AddressesUpdated,
                    updates.Count,
                    $"{updates.Count} bestehende Signaladresse(n) wurden in FEE aktualisiert.");
        }

        public async Task<bool> CreateAsync()
        {
            LastCreationWarning = string.Empty;
            if (!await CreateDeviceBaseAsync())
                return false;

            if (!await WriteDeviceParameters())
                return false;

            if (!await CreateDeviceSpecificAsync())
                return false;

            // Mark only a completely generated device. A failed or partial
            // SDK transaction remains deliberately ineligible for reverse export.
            var provenance = FeeSpecialDeviceProvenanceCodec.Encode(
                FeeSpecialDeviceProvenanceCodec.Create(this));
            var tagWrite = await FeeTagPropertyStore.TryWriteAndVerifyAsync(
                DeviceBasicFrame.Guid,
                provenance,
                preserveExisting: true,
                verifyAfterWrite: false);
            if (!tagWrite.Confirmed)
                LastCreationWarning = tagWrite.Warning;

            // SetPropertyAsync updates the SDK-side object wrapper. Sending the
            // already existing root is required to persist the changed component
            // in the FEE project. Verify the round-trip before reporting success.
            if (string.IsNullOrWhiteSpace(LastCreationWarning))
            {
                ApiInstance.Object.Send(DeviceBasicFrame.Guid);
                if (!await VerifyProvenanceAsync(provenance))
                {
                    LastCreationWarning =
                        "Das Gerät wurde erzeugt, die Provenienz konnte anschließend aber nicht aus FEE zurückgelesen werden.";
                }
            }
            return true;


        }

        private async Task<bool> VerifyProvenanceAsync(
            IReadOnlyDictionary<string, string> expected)
        {
            const int maximumAttempts = 15;
            for (var attempt = 0; attempt < maximumAttempts; attempt++)
            {
                try
                {
                    var actual = await FeeTagPropertyStore.ReadAsync(DeviceBasicFrame.Guid);
                    if (expected.All(item =>
                            actual.TryGetValue(item.Key, out var value) &&
                            string.Equals(value, item.Value, StringComparison.Ordinal)))
                    {
                        return FeeSpecialDeviceProvenanceCodec.TryRead(actual, out _, out _);
                    }
                }
                catch when (attempt < maximumAttempts - 1)
                {
                    // A freshly sent component may not yet be visible through
                    // the read API. Retry within the bounded confirmation window.
                }

                await Task.Delay(100);
            }

            return false;
        }

        protected abstract Task<bool> CreateDeviceSpecificAsync();

        protected abstract Task<bool> WriteDeviceParameters();




        private async Task<bool> CreateDeviceBaseAsync()
        {
            if (!await InitializeFeeObjectsAsync())
                return false;

            if (!await CreateInterfaceAndSignalsAsync())
                return false;

            if (!await AssignSignalsToDeviceLogic())
                return false;

            return true;
        }


        private async Task<bool> InitializeFeeObjectsAsync()
        {
            (DeviceLogicObject.LogicDefinitionGuid, DeviceLogicObject.LogicDefinitionVersion) = await FeeLogic.GetOrImportLogicDefinition(DeviceLogicObject.LogicDefinitionName, DeviceLogicObject.LogicDefinitionPath);
            if (DeviceLogicObject.LogicDefinitionGuid == Guid.Empty || DeviceLogicObject.LogicDefinitionVersion == String.Empty)
                return false;

            // Create Basic Frame to hold all elements
            await DeviceBasicFrame.CreateAsync();
            await DeviceBasicFrame.SendAndWaitAsync();

            // Create LogicObject
            if (!await DeviceLogicObject.CreateSendAssignAndWaitAsync())
                return false;


            return true;
        }


        private async Task<bool> CreateInterfaceAndSignalsAsync()
        {
            // Create Interface and Device Signals
            DeviceInterface = new FeeInterface()
            {
                Name = DevicePrefix + " (" + DeviceType + ")",
            };

            if (await DeviceInterface.CreateInterfaceAsync())
            {
                foreach (var signal in DeviceSignals)
                {
                    if (!await signal.CreateSignalAsync(DeviceInterface))
                        return false;
                }

                //foreach (var signal in DeviceSignals)
                //{
                //    await signal.CreateSignalAsync(DeviceInterface);
                //}
                return true;
            }

            return false;
        }

        private async Task<bool> AssignSignalsToDeviceLogic()
        {
            foreach (var signal in DeviceSignals)
            {
                var slotName = signal.Tag.Substring(this.DevicePrefix.Length + 1);

                if(DeviceManufacturer == DeviceManufacturer.Grob && DeviceType.Equals(GrobDeviceTypes.SimModeSiemens))
                {
                    slotName = signal.Comment;
                }

                await ApiInstance.Interface.SendSlotVarAssignmentAsync(DeviceLogicObject.Guid, slotName, signal.Guid, true);
            }


            //foreach (var signal in DeviceSignals)
            //{
            //    var slotName = signal.Tag.Substring(this.DevicePrefix.Length + 1);

            //    await ApiInstance.Interface.SendSlotVarAssignmentAsync(DeviceLogicObject.Guid, slotName, signal.Guid, true);
            //}

            return true;
        }








    }


    public record DeviceParameter(Guid ObjectGuid, string SlotName, object Value);



    public record SpecialDeviceAddresses
    {
        public int Input { get; init; }
        public int Output { get; init; }


        public SpecialDeviceAddresses(int address)
        {
            Input = address;
            Output = address;
        }

        public SpecialDeviceAddresses(int input, int output)
        {
            Input = input;
            Output = output;
        }


        public int GetBaseAddress(IOMode ioMode)
        {
            return ioMode switch
            {
                IOMode.Read => Output,
                IOMode.Write => Input,
                _ => throw new ArgumentOutOfRangeException(nameof(ioMode))
            };
        }
    }
}

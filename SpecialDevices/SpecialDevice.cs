using FS.SDK.Io;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Services;
using static VIBN_Tools.SpecialDevices.DeviceCatalog;

namespace VIBN_Tools.SpecialDevices
{
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
                    var tagsXml = await ApiInstance.Object.GetPropertyAsync(
                        rootGuid,
                        nameof(FS.SDK.Components.TagComponent.TagEntries),
                        nameof(FS.SDK.Components.TagComponent));
                    var tags = ApiInstance.XmlHelper.ConvertToDictionaryStringString(tagsXml);
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
            if (!await ApiInstance.Object.SetPropertyAsync(
                DeviceBasicFrame.Guid,
                nameof(FS.SDK.Components.TagComponent.TagEntries),
                new Dictionary<string, string>(provenance, StringComparer.Ordinal),
                nameof(FS.SDK.Components.TagComponent)))
            {
                LastCreationWarning =
                    "Das Gerät wurde erzeugt, FEE hat aber das Schreiben der Provenienz abgelehnt.";
            }

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
                    var tagsXml = await ApiInstance.Object.GetPropertyAsync(
                        DeviceBasicFrame.Guid,
                        nameof(FS.SDK.Components.TagComponent.TagEntries),
                        nameof(FS.SDK.Components.TagComponent));
                    var actual = ApiInstance.XmlHelper.ConvertToDictionaryStringString(tagsXml);
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

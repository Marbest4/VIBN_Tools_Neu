using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using VIBN_Tools.Tia.Contracts;

namespace VIBN_Tools.TiaBridge.Openness;

/// <summary>
/// Read-only adapter for the version-specific Siemens hardware object model.
/// It keeps every DeviceItem hierarchy node separate and never modifies the
/// attached TIA project.
/// </summary>
internal sealed class TiaHardwareReader
{
    private readonly Assembly _engineeringAssembly;

    public TiaHardwareReader(Assembly engineeringAssembly)
    {
        _engineeringAssembly = engineeringAssembly ?? throw new ArgumentNullException(nameof(engineeringAssembly));
    }

    public IReadOnlyList<TiaHardwareModuleInfo> Read(object project, int selectedDeviceIndex)
    {
        var devices = EnumerateDevices(project);
        var result = new List<TiaHardwareModuleInfo>();
        // Openness may surface the same engineering object through different
        // proxy instances and hierarchy paths. Semantic identities deliberately
        // span the complete read, rather than relying on proxy reference equality.
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            var device = devices[deviceIndex];
            var discovery = DiscoverDevice(device);
            discovery.AddNetwork(ReadNetworkMetadata(device));
            var context = ReadDeviceContext(device, deviceIndex)
                .WithDiscovery(discovery);
            Traverse(
                device,
                context,
                result,
                identities,
                parentPath: string.Empty,
                parentName: context.DeviceName,
                depth: 0,
                parentSlot: -1,
                inheritedNetwork: discovery.Network);
        }

        // TIA may expose one GSD submodule both below the rack and below the
        // logical device head. These are not two physical modules when device,
        // module type and the complete process-image ranges are identical.
        // Keep the representation carrying the most precise slot metadata.
        var physicalModules = result
            .GroupBy(CreatePhysicalModuleIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(GetRepresentationQuality)
                .ThenBy(module => module.ModulePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
        var ordered = physicalModules
            .OrderBy(module => module.DeviceIndex == selectedDeviceIndex ? 0 : 1)
            .ThenBy(module => module.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.ModulePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.Slot < 0 ? int.MaxValue : module.Slot)
            .ThenBy(module => module.Subslot < 0 ? int.MaxValue : module.Subslot)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
            ordered[index].TraversalIndex = index + 1;
        return ordered;
    }

    /// <summary>
    /// Enumerates the complete TIA project device tree in a stable order:
    /// root devices, user groups (including nested groups), then the system
    /// group for ungrouped distributed devices. Root devices stay first so
    /// existing PLC indices remain stable.
    /// </summary>
    internal IReadOnlyList<object> EnumerateDevices(object project)
    {
        if (project is null)
            throw new ArgumentNullException(nameof(project));

        var devices = new List<object>();
        var semanticIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUniqueDevices(ReadEnumerableMember(project, "Devices"), devices, semanticIdentities);

        foreach (var group in ReadEnumerableMember(project, "DeviceGroups"))
            AddDeviceGroup(group, devices, semanticIdentities);

        var ungroupedDevices = ReadMember(project, "UngroupedDevicesGroup");
        if (ungroupedDevices is not null)
            AddUniqueDevices(ReadEnumerableMember(ungroupedDevices, "Devices"), devices, semanticIdentities);

        return devices;
    }

    private static void AddDeviceGroup(
        object group,
        ICollection<object> devices,
        ISet<string> semanticIdentities)
    {
        AddUniqueDevices(ReadEnumerableMember(group, "Devices"), devices, semanticIdentities);
        foreach (var childGroup in ReadEnumerableMember(group, "Groups"))
            AddDeviceGroup(childGroup, devices, semanticIdentities);
    }

    private static void AddUniqueDevices(
        IEnumerable<object> candidates,
        ICollection<object> devices,
        ISet<string> semanticIdentities)
    {
        foreach (var candidate in candidates)
        {
            if (devices.Any(existing => ReferenceEquals(existing, candidate)))
                continue;

            var identity = CreateDeviceIdentity(candidate);
            if (identity.Length > 0 && !semanticIdentities.Add(identity))
                continue;

            devices.Add(candidate);
        }
    }

    private void Traverse(
        object parent,
        DeviceContext device,
        ICollection<TiaHardwareModuleInfo> result,
        ISet<string> identities,
        string parentPath,
        string parentName,
        int depth,
        int parentSlot,
        NetworkMetadata inheritedNetwork)
    {
        foreach (var item in ReadDeviceItems(parent))
        {
            var moduleName = ReadString(item, "Name");
            var position = ReadInt(item, "PositionNumber", "Slot");
            var explicitSlot = ReadInt(item, "SlotNumber", "Slot");
            var explicitSubslot = ReadInt(item, "SubslotNumber", "Subslot", "SubPositionNumber");
            var zeroPositionSubmodule = depth >= 2 && position == 0 && parentSlot > 0;
            var slot = explicitSlot >= 0
                ? explicitSlot
                : zeroPositionSubmodule || depth >= 3 && parentSlot >= 0 ? parentSlot : position;
            var subslot = explicitSubslot >= 0
                ? explicitSubslot
                : zeroPositionSubmodule ? 0 : depth >= 3 ? position : -1;
            // A directly nested module owns its PositionNumber as slot. Only
            // deeper submodules inherit that slot and use their position as a
            // best-effort subslot when the API exposes no explicit attribute.
            var nextParentSlot = slot >= 0 ? slot : parentSlot;

            var modulePath = string.IsNullOrWhiteSpace(parentPath)
                ? moduleName
                : $"{parentPath}/{moduleName}";
            var typeIdentifier = ReadString(item, "TypeIdentifier");
            var moduleType = ReadString(item, "TypeName", "Classification");
            var localNetwork = ReadNetworkMetadata(item);
            var network = MergeNetworkMetadata(inheritedNetwork, localNetwork);
            var effectiveDevice = depth == 0
                ? device.WithHeadIdentity(moduleName, moduleType, network)
                : device;
            var deviceType = depth == 0 && moduleType.Length > 0 && !IsInfrastructureName(moduleName)
                ? moduleType
                : effectiveDevice.DeviceType;
            var manufacturer = FirstNotEmpty(
                ReadString(item, "Manufacturer", "VendorName", "Vendor"),
                effectiveDevice.Manufacturer);
            var orderNumber = FirstNotEmpty(
                ReadString(item, "OrderNumber"),
                ParseOrderNumber(typeIdentifier),
                effectiveDevice.OrderNumber);
            var firmware = FirstNotEmpty(
                ReadString(item, "FirmwareVersion", "Firmware", "Version"),
                ParseFirmware(typeIdentifier),
                effectiveDevice.FirmwareVersion);
            var gsd = ReadGsdMetadata(item, "Siemens.Engineering.HW.Features.GsdDeviceItem");
            if (gsd.IsEmpty)
                gsd = effectiveDevice.Gsd;
            var addresses = ReadAddresses(item);

            // Do not create rows for rack, interface and other hierarchy
            // containers without process-image addresses. Their metadata is
            // inherited by address-bearing descendants instead.
            foreach (var addressSet in addresses.CreateSets())
            {
                var identity = CreateModuleIdentity(
                    effectiveDevice.SemanticIdentity,
                    moduleName,
                    moduleType,
                    typeIdentifier,
                    slot,
                    subslot,
                    addressSet);
                if (!identities.Add(identity))
                    continue;

                result.Add(new TiaHardwareModuleInfo
                {
                    DeviceIndex = effectiveDevice.DeviceIndex,
                    HierarchyDepth = depth,
                    ParentName = parentName,
                    ObjectClass = item.GetType().FullName ?? item.GetType().Name,
                    HardwareIdentifier = ReadString(item, "HardwareIdentifier", "HardwareId", "HwIdentifier", "ID"),
                    DeviceName = effectiveDevice.DeviceName,
                    DeviceType = deviceType,
                    Manufacturer = manufacturer,
                    OrderNumber = orderNumber,
                    FirmwareVersion = firmware,
                    GsdName = gsd.Name,
                    GsdType = gsd.Type,
                    ProfinetName = network.ProfinetName,
                    IpAddress = network.IpAddress,
                    NetworkRole = network.Role,
                    Slot = slot,
                    Subslot = subslot,
                    AddressSetIndex = addressSet.Index,
                    ModuleName = moduleName,
                    ModulePath = modulePath,
                    ModuleType = moduleType,
                    TypeIdentifier = typeIdentifier,
                    InputStartByte = addressSet.Input?.StartByte ?? -1,
                    InputLengthBits = addressSet.Input?.RawLengthBits ?? 0,
                    InputLength = addressSet.Input?.ByteLength ?? 0,
                    OutputStartByte = addressSet.Output?.StartByte ?? -1,
                    OutputLengthBits = addressSet.Output?.RawLengthBits ?? 0,
                    OutputLength = addressSet.Output?.ByteLength ?? 0
                });
            }

            Traverse(
                item,
                effectiveDevice.WithMetadata(
                    deviceType,
                    manufacturer,
                    orderNumber,
                    firmware,
                    gsd),
                result,
                identities,
                modulePath,
                moduleName,
                depth + 1,
                nextParentSlot,
                network);
        }
    }

    private DeviceContext ReadDeviceContext(object device, int deviceIndex)
    {
        var typeIdentifier = ReadString(device, "TypeIdentifier");
        var deviceName = ReadString(device, "Name");
        var semanticIdentity = CreateDeviceIdentity(device);
        return new DeviceContext(
            deviceIndex,
            deviceName,
            semanticIdentity.Length > 0
                ? semanticIdentity
                : $"UNNAMED-DEVICE-{deviceIndex}",
            ReadString(device, "TypeName", "Classification"),
            ReadString(device, "Manufacturer", "VendorName", "Vendor"),
            FirstNotEmpty(ReadString(device, "OrderNumber"), ParseOrderNumber(typeIdentifier)),
            FirstNotEmpty(
                ReadString(device, "FirmwareVersion", "Firmware", "Version"),
                ParseFirmware(typeIdentifier)),
            ReadGsdMetadata(device, "Siemens.Engineering.HW.Features.GsdDevice"));
    }

    /// <summary>
    /// GSD stations can expose the actual device head and its NetworkInterface
    /// in a sibling branch next to the rack that owns the addressed modules.
    /// A pure ancestor-only traversal then loses the station name and network
    /// data. Pre-scanning one device keeps metadata scoped to that device while
    /// making it available to every addressed branch.
    /// </summary>
    private DeviceDiscovery DiscoverDevice(object device)
    {
        var state = new DeviceDiscovery();
        DiscoverDeviceItems(device, parentItem: null, state);
        return state;
    }

    private void DiscoverDeviceItems(object parent, object? parentItem, DeviceDiscovery state)
    {
        foreach (var item in ReadDeviceItems(parent))
        {
            var localNetwork = ReadNetworkMetadata(item);
            state.AddNetwork(localNetwork);

            var typeIdentifier = ReadString(item, "TypeIdentifier");
            var gsd = ReadGsdMetadata(item, "Siemens.Engineering.HW.Features.GsdDeviceItem");
            if (!localNetwork.IsEmpty)
            {
                var candidate = parentItem is not null &&
                                !IsInfrastructureName(ReadString(parentItem, "Name"))
                    ? parentItem
                    : item;
                state.ConsiderHead(candidate, score: 100);
            }
            else if (IsGsdDeviceHead(typeIdentifier, gsd.Type))
            {
                state.ConsiderHead(item, score: 80);
            }

            DiscoverDeviceItems(item, item, state);
        }
    }

    private static bool IsGsdDeviceHead(string typeIdentifier, string gsdType) =>
        typeIdentifier.IndexOf("/HM_", StringComparison.OrdinalIgnoreCase) >= 0 ||
        string.Equals(gsdType, "IM", StringComparison.OrdinalIgnoreCase) ||
        gsdType.StartsWith("IM.", StringComparison.OrdinalIgnoreCase);

    private static bool IsInfrastructureName(string value) =>
        string.IsNullOrWhiteSpace(value) || Regex.IsMatch(
            value,
            @"^(BAUGRUPPENTRÄGER|BAUGRUPPENTRAEGER|RACK|RAIL|HEAD|PORT(?:\s*\d+)?|" +
            @"PN[-_/ ]?IO(?:[-_/ ]?\d+)?|PROFINET(?:[-_/ ]?INTERFACE)?(?:[-_/ ]?\d+)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private AddressMetadata ReadAddresses(object item)
    {
        var inputs = new List<AddressRange>();
        var outputs = new List<AddressRange>();
        foreach (var address in ReadEnumerableMember(item, "Addresses"))
        {
            var ioType = ReadString(address, "IoType", "IOType");
            var start = ReadInt(address, "StartAddress", "StartAdress");
            var rawLengthBits = Math.Max(0, ReadInt(address, "Length"));
            if (start < 0 || rawLengthBits <= 0)
                continue;

            var range = new AddressRange(start, rawLengthBits);
            if (ioType.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                AddUniqueAddress(inputs, range);
            else if (ioType.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0)
                AddUniqueAddress(outputs, range);
        }
        return new AddressMetadata(inputs, outputs);
    }

    private static void AddUniqueAddress(ICollection<AddressRange> ranges, AddressRange candidate)
    {
        if (!ranges.Any(existing => existing.StartByte == candidate.StartByte &&
                                    existing.RawLengthBits == candidate.RawLengthBits))
        {
            ranges.Add(candidate);
        }
    }

    private NetworkMetadata ReadNetworkMetadata(object item)
    {
        var direct = new NetworkMetadata(
            ReadString(item, "PnDeviceName", "PnDeviceNameConverted", "ProfinetName"),
            ReadString(item, "IpAddress", "IPAddress", "Ipv4Address"),
            string.Empty);
        var service = GetService(item, "Siemens.Engineering.HW.Features.NetworkInterface");
        if (service is null)
            return direct;

        var roles = new List<string>();
        if (ReadEnumerableMember(service, "IoControllers").Count > 0)
            roles.Add("IO-Controller");
        if (ReadEnumerableMember(service, "IoConnectors").Count > 0)
            roles.Add("IO-Device");

        foreach (var node in ReadEnumerableMember(service, "Nodes"))
        {
            var ipAddress = ReadString(node, "Address", "IpAddress", "IPAddress", "Ipv4Address");
            var profinetName = ReadString(
                node,
                "PnDeviceName",
                "PnDeviceNameConverted",
                "ProfinetName");
            if (ipAddress.Length > 0 || profinetName.Length > 0)
            {
                return new NetworkMetadata(
                    FirstNotEmpty(profinetName, direct.ProfinetName),
                    FirstNotEmpty(ipAddress, direct.IpAddress),
                    string.Join(" + ", roles));
            }
        }
        return new NetworkMetadata(direct.ProfinetName, direct.IpAddress, string.Join(" + ", roles));
    }

    private GsdMetadata ReadGsdMetadata(object target, string serviceTypeName)
    {
        var service = GetService(target, serviceTypeName);
        return new GsdMetadata(
            FirstNotEmpty(ReadString(service, "GsdName"), ReadString(target, "GsdName")),
            FirstNotEmpty(ReadString(service, "GsdType"), ReadString(target, "GsdType")));
    }

    private static NetworkMetadata MergeNetworkMetadata(
        NetworkMetadata inherited,
        NetworkMetadata local) => new(
            FirstNotEmpty(local.ProfinetName, inherited.ProfinetName),
            FirstNotEmpty(local.IpAddress, inherited.IpAddress),
            FirstNotEmpty(local.Role, inherited.Role));

    private object? GetService(object target, string serviceTypeName)
    {
        var serviceType = ResolveEngineeringType(serviceTypeName);
        if (serviceType is null)
            return null;

        foreach (var method in EnumerateMethods(target.GetType(), "GetService"))
        {
            if (!method.IsGenericMethodDefinition || method.GetParameters().Length != 0)
                continue;
            try
            {
                return method.MakeGenericMethod(serviceType).Invoke(target, null);
            }
            catch (Exception)
            {
                // A DeviceItem provides only a subset of Openness services.
            }
        }
        return null;
    }

    private Type? ResolveEngineeringType(string fullName)
    {
        var direct = _engineeringAssembly.GetType(fullName, throwOnError: false);
        if (direct is not null)
            return direct;

        // Depending on the installed TIA generation, feature interfaces can
        // reside in a referenced Siemens.Engineering.* assembly rather than in
        // Siemens.Engineering.dll itself. Restrict the lookup to that family so
        // an unrelated type with the same namespace cannot be selected.
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith(
                "Siemens.Engineering",
                StringComparison.OrdinalIgnoreCase) == true)
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);
    }

    private static IEnumerable<MethodInfo> EnumerateMethods(Type runtimeType, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = runtimeType; type is not null; type = type.BaseType)
        {
            foreach (var method in type.GetMethods(flags).Where(method => method.Name == name))
                yield return method;
        }
        foreach (var interfaceType in runtimeType.GetInterfaces())
        {
            foreach (var method in interfaceType.GetMethods().Where(method => method.Name == name))
                yield return method;
        }
    }

    private static IReadOnlyList<object> ReadEnumerableMember(object? target, string name)
    {
        var value = ReadMember(target, name);
        return value is IEnumerable enumerable
            ? enumerable.Cast<object>().Where(item => item is not null).ToArray()
            : Array.Empty<object>();
    }

    private static IReadOnlyList<object> ReadDeviceItems(object parent)
    {
        var deviceItems = ReadEnumerableMember(parent, "DeviceItems");
        return deviceItems.Count > 0
            ? deviceItems
            : ReadEnumerableMember(parent, "Items");
    }

    private static string ReadString(object? target, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadMember(target, name);
            if (value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
                return Convert.ToString(value)!.Trim();
        }
        return string.Empty;
    }

    private static int ReadInt(object? target, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadMember(target, name);
            if (value is not null && int.TryParse(Convert.ToString(value), out var number))
                return number;
        }
        return -1;
    }

    private static object? ReadMember(object? target, string name)
    {
        if (target is null)
            return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (property is not null)
                    return property.GetValue(target, null);
            }
            catch (Exception)
            {
                // Continue with explicit interface and dynamic attribute access.
            }
        }
        foreach (var interfaceType in target.GetType().GetInterfaces())
        {
            var property = interfaceType.GetProperties().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (property is not null)
                    return property.GetValue(target, null);
            }
            catch (Exception)
            {
                // The runtime proxy may reject unsupported interface members.
            }
        }
        return ReadEngineeringAttribute(target, name);
    }

    private static object? ReadEngineeringAttribute(object target, string name)
    {
        try
        {
            var method = EnumerateMethods(target.GetType(), "GetAttribute")
                .FirstOrDefault(candidate =>
                    !candidate.IsGenericMethod &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType == typeof(string));
            return method?.Invoke(target, new object[] { name });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FirstNotEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string CreateDeviceIdentity(object device)
    {
        var name = NormalizeIdentityPart(ReadString(device, "Name"));
        if (name.Length == 0)
            return string.Empty;

        var typeIdentifier = NormalizeIdentityPart(ReadString(device, "TypeIdentifier"));
        var type = NormalizeIdentityPart(ReadString(device, "TypeName", "Classification"));
        return $"{name}|{typeIdentifier}|{type}";
    }

    private static string CreateModuleIdentity(
        string deviceIdentity,
        string moduleName,
        string moduleType,
        string typeIdentifier,
        int slot,
        int subslot,
        AddressSet addressSet) => string.Join("|",
        deviceIdentity,
        NormalizeIdentityPart(moduleName),
        NormalizeIdentityPart(moduleType),
        NormalizeIdentityPart(typeIdentifier),
        slot,
        subslot,
        addressSet.Input?.StartByte ?? -1,
        addressSet.Input?.RawLengthBits ?? 0,
        addressSet.Output?.StartByte ?? -1,
        addressSet.Output?.RawLengthBits ?? 0);

    private static string CreatePhysicalModuleIdentity(TiaHardwareModuleInfo module) => string.Join("|",
        module.DeviceIndex,
        NormalizeIdentityPart(module.DeviceName),
        NormalizeIdentityPart(module.ModuleName),
        NormalizeIdentityPart(module.ModuleType),
        NormalizeIdentityPart(module.TypeIdentifier),
        module.InputStartByte,
        module.InputLengthBits,
        module.OutputStartByte,
        module.OutputLengthBits);

    private static int GetRepresentationQuality(TiaHardwareModuleInfo module) =>
        (module.Slot > 0 ? 16 : 0) +
        (module.Subslot >= 0 ? 8 : 0) +
        (module.HardwareIdentifier.Length > 0 ? 4 : 0) +
        (module.IpAddress.Length > 0 ? 2 : 0) +
        (module.ProfinetName.Length > 0 ? 1 : 0);

    private static string NormalizeIdentityPart(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToUpperInvariant();

    private static string ParseOrderNumber(string typeIdentifier)
    {
        var match = Regex.Match(typeIdentifier ?? string.Empty, @"^OrderNumber:(?<value>[^/]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string ParseFirmware(string typeIdentifier)
    {
        var match = Regex.Match(
            typeIdentifier ?? string.Empty,
            @"(?:^|/|\s)(?<value>V\d+(?:\.\d+){1,3})(?:$|/|\s)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private sealed class DeviceContext
    {
        public DeviceContext(
            int deviceIndex,
            string deviceName,
            string semanticIdentity,
            string deviceType,
            string manufacturer,
            string orderNumber,
            string firmwareVersion,
            GsdMetadata gsd)
        {
            DeviceIndex = deviceIndex;
            DeviceName = deviceName;
            SemanticIdentity = semanticIdentity;
            DeviceType = deviceType;
            Manufacturer = manufacturer;
            OrderNumber = orderNumber;
            FirmwareVersion = firmwareVersion;
            Gsd = gsd;
        }

        public int DeviceIndex { get; }
        public string DeviceName { get; }
        public string SemanticIdentity { get; }
        public string DeviceType { get; }
        public string Manufacturer { get; }
        public string OrderNumber { get; }
        public string FirmwareVersion { get; }
        public GsdMetadata Gsd { get; }

        public DeviceContext WithHeadIdentity(
            string headName,
            string headType,
            NetworkMetadata network)
        {
            var preferHead = ShouldPreferHeadName(DeviceName, headName);
            var name = preferHead
                ? headName
                : FirstNotEmpty(DeviceName, headName, network.ProfinetName);
            var type = preferHead && !IsInfrastructureName(headName)
                ? FirstNotEmpty(headType, DeviceType)
                : FirstNotEmpty(DeviceType, headType);
            return new DeviceContext(
                DeviceIndex,
                name,
                SemanticIdentity,
                type,
                Manufacturer,
                OrderNumber,
                FirmwareVersion,
                Gsd);
        }

        public DeviceContext WithMetadata(
            string deviceType,
            string manufacturer,
            string orderNumber,
            string firmwareVersion,
            GsdMetadata gsd) => new(
            DeviceIndex,
            DeviceName,
            SemanticIdentity,
            deviceType,
            manufacturer,
            orderNumber,
            firmwareVersion,
            gsd);

        public DeviceContext WithDiscovery(DeviceDiscovery discovery)
        {
            if (discovery.Head is null)
                return this;

            var head = discovery.Head;
            var typeIdentifier = ReadString(head, "TypeIdentifier");
            var headGsd = ReadGsdMetadataForDiscovery(head);
            return new DeviceContext(
                DeviceIndex,
                FirstNotEmpty(ReadString(head, "Name"), discovery.Network.ProfinetName, DeviceName),
                SemanticIdentity,
                FirstNotEmpty(ReadString(head, "TypeName", "Classification"), DeviceType),
                FirstNotEmpty(ReadString(head, "Manufacturer", "VendorName", "Vendor"), Manufacturer),
                FirstNotEmpty(ReadString(head, "OrderNumber"), ParseOrderNumber(typeIdentifier), OrderNumber),
                FirstNotEmpty(
                    ReadString(head, "FirmwareVersion", "Firmware", "Version"),
                    ParseFirmware(typeIdentifier),
                    FirmwareVersion),
                headGsd.IsEmpty ? Gsd : headGsd);
        }

        private static GsdMetadata ReadGsdMetadataForDiscovery(object target) => new(
            ReadString(target, "GsdName"),
            ReadString(target, "GsdType"));

        private static bool ShouldPreferHeadName(string deviceName, string headName)
        {
            if (string.IsNullOrWhiteSpace(headName))
                return false;
            if (string.IsNullOrWhiteSpace(deviceName))
                return true;

            return Regex.IsMatch(
                deviceName,
                @"^(GSD[-_ ]?(GERÄT|GERAET|DEVICE)|DEVICE|GERÄT|GERAET|" +
                @"BAUGRUPPENTRÄGER|BAUGRUPPENTRAEGER|RACK|RAIL)[-_ ]*\d*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    private sealed class GsdMetadata
    {
        public GsdMetadata(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public string Type { get; }
        public bool IsEmpty => Name.Length == 0 && Type.Length == 0;
    }

    private sealed class NetworkMetadata
    {
        public NetworkMetadata(string profinetName, string ipAddress, string role)
        {
            ProfinetName = profinetName;
            IpAddress = ipAddress;
            Role = role;
        }

        public static NetworkMetadata Empty { get; } = new(string.Empty, string.Empty, string.Empty);
        public string ProfinetName { get; }
        public string IpAddress { get; }
        public string Role { get; }
        public bool IsEmpty => ProfinetName.Length == 0 && IpAddress.Length == 0 && Role.Length == 0;
    }

    private sealed class DeviceDiscovery
    {
        private int _headScore;

        public object? Head { get; private set; }
        public NetworkMetadata Network { get; private set; } = NetworkMetadata.Empty;

        public void AddNetwork(NetworkMetadata candidate)
        {
            if (candidate.IsEmpty)
                return;
            Network = new NetworkMetadata(
                FirstNotEmpty(Network.ProfinetName, candidate.ProfinetName),
                FirstNotEmpty(Network.IpAddress, candidate.IpAddress),
                FirstNotEmpty(Network.Role, candidate.Role));
        }

        public void ConsiderHead(object candidate, int score)
        {
            if (score <= _headScore || IsInfrastructureName(ReadString(candidate, "Name")))
                return;

            // Ensure that a proxy really exposes at least a name before using
            // it to replace the generic rack identity.
            if (ReadString(candidate, "Name").Length == 0)
                return;
            Head = candidate;
            _headScore = score;
        }
    }

    private sealed class AddressRange
    {
        public AddressRange(int startByte, int rawLengthBits)
        {
            StartByte = startByte;
            RawLengthBits = rawLengthBits;
        }

        public int StartByte { get; }
        public int RawLengthBits { get; }
        public int ByteLength => RawLengthBits <= 0 ? 0 : checked((RawLengthBits + 7) / 8);
    }

    private sealed class AddressSet
    {
        public AddressSet(int index, AddressRange? input, AddressRange? output)
        {
            Index = index;
            Input = input;
            Output = output;
        }

        public int Index { get; }
        public AddressRange? Input { get; }
        public AddressRange? Output { get; }
    }

    private sealed class AddressMetadata
    {
        public AddressMetadata(
            IReadOnlyList<AddressRange> inputs,
            IReadOnlyList<AddressRange> outputs)
        {
            Inputs = inputs;
            Outputs = outputs;
        }

        public IReadOnlyList<AddressRange> Inputs { get; }
        public IReadOnlyList<AddressRange> Outputs { get; }

        public IEnumerable<AddressSet> CreateSets()
        {
            var count = Math.Max(Inputs.Count, Outputs.Count);
            for (var index = 0; index < count; index++)
            {
                yield return new AddressSet(
                    index,
                    index < Inputs.Count ? Inputs[index] : null,
                    index < Outputs.Count ? Outputs[index] : null);
            }
        }
    }
}

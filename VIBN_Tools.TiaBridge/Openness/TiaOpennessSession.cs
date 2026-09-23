using System.Reflection;
using System.Text.RegularExpressions;
using VIBN_Tools.Tia.Contracts;

namespace VIBN_Tools.TiaBridge.Openness;

public sealed class TiaOpennessSession : ITiaOpennessSession
{
    private static readonly Regex VersionPattern = new("^V(1[5-9]|2[0-2])$", RegexOptions.IgnoreCase);

    private Assembly? _engineeringAssembly;
    private dynamic? _portal;
    private dynamic? _project;
    private string? _engineeringDllPath;
    private string? _selectedVersion;
    private int? _selectedPlcIndex;

    public void SelectVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !VersionPattern.IsMatch(version))
            throw new ArgumentException($"Unsupported TIA version: {version}", nameof(version));

        var normalized = version.ToUpperInvariant();
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Siemens",
            "Automation",
            $"Portal {normalized}",
            "PublicAPI",
            normalized,
            "Siemens.Engineering.dll");

        if (!File.Exists(path))
            throw new FileNotFoundException($"TIA Openness assembly for {normalized} was not found.", path);

        DisposePortal();
        _selectedVersion = normalized;
        _engineeringDllPath = path;
        _engineeringAssembly = Assembly.LoadFrom(path);
    }

    public void Attach()
    {
        var assembly = RequireAssembly();
        var portalType = assembly.GetType("Siemens.Engineering.TiaPortal", throwOnError: true);
        var getProcesses = portalType.GetMethod("GetProcesses", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(portalType.FullName, "GetProcesses");

        var processes = ((System.Collections.IEnumerable)getProcesses.Invoke(null, null))
            .Cast<object>()
            .ToArray();

        if (processes.Length == 0)
            throw new InvalidOperationException($"Keine geöffnete TIA-Portal-Instanz {_selectedVersion} gefunden.");

        var processInfos = processes
            .Select(process => new PortalProcess(
                process,
                ReadStringMember(process, "ProjectPath"),
                ReadStringMember(process, "Id", "ProcessId"),
                ReadStringMember(process, "Mode"),
                DescribeCollectionMember(process, "AttachedSessions")))
            .ToArray();
        var candidates = processInfos
            .Where(process => !string.IsNullOrWhiteSpace(process.ProjectPath))
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Mehrere TIA-Projekte sind geöffnet. Nur ein Projekt in {_selectedVersion} geöffnet lassen. " +
                $"Prozesse: {FormatProcessDiagnostics(candidates)}");
        }
        if (candidates.Length == 0 && processes.Length > 1)
            throw new InvalidOperationException(
                $"Mehrere TIA-Portal-Instanzen {_selectedVersion} sind geöffnet, aber keine meldet einen ProjectPath. " +
                $"Nur die Instanz mit dem gewünschten Projekt geöffnet lassen. Prozesse: {FormatProcessDiagnostics(processInfos)}");

        // Older releases and a few project types do not expose ProjectPath.
        // With a single process attaching is still unambiguous.
        var selectedInfo = candidates.FirstOrDefault() ?? processInfos[0];
        dynamic selectedProcess = selectedInfo.Process;
        _portal = selectedProcess.Attach();

        // TIA's process information is a snapshot and the Openness firewall
        // confirmation can take noticeably longer than the former ten-second
        // window. Also support an already opened Multiuser local session: its
        // project is exposed through LocalSessions[n].Project, not Projects[0].
        const int maximumProjectWaits = 360;
        var projectProbe = ProjectProbe.Empty;
        for (var attempt = 0; attempt < maximumProjectWaits; attempt++)
        {
            projectProbe = ProbeOpenProject(_portal);
            _project = projectProbe.Project;
            if (_project is not null)
                break;
            Thread.Sleep(250);
        }
        if (_project is null)
        {
            throw new InvalidOperationException(
                $"Die verbundene TIA-Instanz {_selectedVersion} stellt nach 90 Sekunden weder ein Einzelprojekt noch eine geöffnete Multiuser-Local-Session über Openness bereit." +
                $" Prozessdiagnose: {FormatProcessDiagnostics(processInfos)}. Projektdiagnose: {projectProbe.Diagnostics}." +
                " Referenzprojekte werden von TiaPortal.Projects nicht als geöffnetes Primärprojekt bereitgestellt; bei Multiuser muss eine lokale oder exklusive Session tatsächlich geöffnet sein." +
                " Den Openness-Firewall-Dialog in TIA mit 'Immer zulassen' bestätigen, UMAC-/Openness-Rechte sowie installierte Optionspakete/HSPs prüfen und TIA sowie VIBN Tools nach einer Änderung der Gruppe 'Siemens TIA Openness' neu anmelden.");
        }
        _selectedPlcIndex = null;
    }

    private static ProjectProbe ProbeOpenProject(object portal)
    {
        var projects = ProbeEnumerableProperty(portal, "Projects");
        var project = projects.Values.FirstOrDefault();
        if (project is not null)
            return new ProjectProbe(
                project,
                $"Projects={projects.Values.Count} ({projects.Diagnostics}; {DescribeObjects(projects.Values)}); " +
                "LocalSessions=nicht benötigt");

        var localSessions = ProbeEnumerableProperty(portal, "LocalSessions");
        var sessionErrors = new List<string>();
        foreach (var localSession in localSessions.Values)
        {
            try
            {
                var sessionProject = ReadPropertyWithInterfaces(localSession, "Project");
                if (sessionProject is not null)
                {
                    return new ProjectProbe(
                        sessionProject,
                        $"Projects={projects.Values.Count} ({projects.Diagnostics}); " +
                        $"LocalSessions={localSessions.Values.Count} ({localSessions.Diagnostics}; {DescribeObjects(localSessions.Values)}); " +
                        $"SessionProject={DescribeObject(sessionProject)}");
                }
            }
            catch (Exception exception)
            {
                sessionErrors.Add(Unwrap(exception).Message);
            }
        }

        var diagnostics = $"Projects={projects.Values.Count} ({projects.Diagnostics}; {DescribeObjects(projects.Values)}); " +
                          $"LocalSessions={localSessions.Values.Count} ({localSessions.Diagnostics}; {DescribeObjects(localSessions.Values)})";
        if (sessionErrors.Count > 0)
            diagnostics += $"; LocalSession.Project: {string.Join(" | ", sessionErrors.Distinct().Take(3))}";
        return new ProjectProbe(null, diagnostics);
    }

    private static CollectionProbe ProbeEnumerableProperty(object target, string propertyName)
    {
        try
        {
            var value = ReadPropertyWithInterfaces(target, propertyName);
            if (value is not System.Collections.IEnumerable enumerable)
                return new CollectionProbe(Array.Empty<object>(), "Member fehlt oder ist nicht aufzählbar");

            return new CollectionProbe(
                enumerable.Cast<object>().Where(item => item is not null).ToArray(),
                "lesbar");
        }
        catch (Exception exception)
        {
            var root = Unwrap(exception);
            return new CollectionProbe(Array.Empty<object>(), $"{root.GetType().Name}: {root.Message}");
        }
    }

    private static string DescribeCollectionMember(object target, string propertyName)
    {
        var probe = ProbeEnumerableProperty(target, propertyName);
        return $"{propertyName}={probe.Values.Count} ({probe.Diagnostics})";
    }

    private static string DescribeObjects(IReadOnlyList<object> values) => values.Count == 0
        ? "keine Einträge"
        : string.Join(" | ", values.Take(3).Select(DescribeObject));

    private static string DescribeObject(object value)
    {
        var name = ReadStringMember(value, "Name");
        var path = ReadStringMember(value, "Path", "ProjectPath", "LocalSessionPath");
        var type = value.GetType().FullName ?? value.GetType().Name;
        return $"Typ='{type}', Name='{ValueOrDash(name)}', Pfad='{ValueOrDash(path)}'";
    }

    private static string FormatProcessDiagnostics(IEnumerable<PortalProcess> processes) =>
        string.Join(" | ", processes.Select(process =>
            $"PID={ValueOrDash(process.ProcessId)}, Modus='{ValueOrDash(process.Mode)}', " +
            $"ProjectPath='{ValueOrDash(process.ProjectPath)}', {process.AttachedSessions}"));

    private static string ValueOrDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static object? ReadPropertyWithInterfaces(object target, string propertyName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property is not null)
                return property.GetValue(target, null);
        }

        foreach (var interfaceType in target.GetType().GetInterfaces())
        {
            var property = interfaceType.GetProperties().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property is not null)
                return property.GetValue(target, null);
        }

        return null;
    }

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: not null } invocationException
            ? invocationException.InnerException
            : exception;

    private static IReadOnlyList<object> ReadEnumerableProperty(object? target, string propertyName)
    {
        if (target is null)
            return Array.Empty<object>();

        try
        {
            var value = ReadMemberValue(target, propertyName);
            return value is System.Collections.IEnumerable enumerable
                ? enumerable.Cast<object>().Where(item => item is not null).ToArray()
                : Array.Empty<object>();
        }
        catch (Exception)
        {
            return Array.Empty<object>();
        }
    }

    public IReadOnlyList<TiaPlcInfo> ListPlcs()
    {
        var devices = GetProjectDevices();
        var plcs = new List<TiaPlcInfo>();

        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            if (TryGetSoftware(device) == null)
                continue;

            plcs.Add(new TiaPlcInfo
            {
                Index = index,
                Name = ReadStringMember(device, "Name"),
                TypeIdentifier = ReadStringMember(device, "TypeIdentifier")
            });
        }

        return plcs;
    }

    public void SelectPlc(int plcIndex)
    {
        var devices = GetProjectDevices();
        if (plcIndex < 0 || plcIndex >= devices.Count)
            throw new ArgumentOutOfRangeException(nameof(plcIndex));

        if (TryGetSoftware(devices[plcIndex]) == null)
            throw new InvalidOperationException($"Device at index {plcIndex} has no PLC software container.");

        _selectedPlcIndex = plcIndex;
    }

    /// <summary>
    /// Enumerates the selected PLC first and then every device tree in the open
    /// project, reading the input/output address compositions exposed by TIA
    /// Openness. Address offsets are kept in bytes; raw lengths are retained in
    /// bits and exposed as rounded-up byte lengths. No project data is modified.
    /// </summary>
    public IReadOnlyList<TiaHardwareModuleInfo> ListHardware()
    {
        var project = RequireProject();
        if (!_selectedPlcIndex.HasValue)
            throw new InvalidOperationException("Select a PLC before reading hardware.");

        // Keep Siemens-version-specific reflection at this boundary. The
        // reader enumerates every project device because distributed IO is
        // not necessarily a child of the selected PLC rack.
        return new TiaHardwareReader(RequireAssembly())
            .Read(project, _selectedPlcIndex.Value);
    }

    public TiaProjectTree ListProgramBlocks()
    {
        dynamic software = RequireSelectedSoftware();
        var tree = new TiaProjectTree();
        TraverseGroup(software.BlockGroup, string.Empty, "Blocks", tree);
        return tree;
    }

    public TiaProjectTree ListDataTypes()
    {
        dynamic software = RequireSelectedSoftware();
        var tree = new TiaProjectTree();
        TraverseGroup(software.TypeGroup, string.Empty, "Types", tree);
        return tree;
    }

    public void ImportBlock(TiaTransferPayload payload)
    {
        ValidateImportPayload(payload);
        dynamic software = RequireSelectedSoftware();
        dynamic group = ResolveGroup(software.BlockGroup, payload.FolderPath);
        Import(group.Blocks, payload.FilePath);
    }

    public void ExportBlock(TiaTransferPayload payload)
    {
        ValidateExportPayload(payload);
        dynamic software = RequireSelectedSoftware();
        dynamic group = ResolveGroup(software.BlockGroup, payload.FolderPath);
        Export(FindByName(group.Blocks, payload.ItemName), payload.FilePath);
    }

    public void ImportDataType(TiaTransferPayload payload)
    {
        ValidateImportPayload(payload);
        dynamic software = RequireSelectedSoftware();
        dynamic group = ResolveGroup(software.TypeGroup, payload.FolderPath);
        Import(group.Types, payload.FilePath);
    }

    public void ExportDataType(TiaTransferPayload payload)
    {
        ValidateExportPayload(payload);
        dynamic software = RequireSelectedSoftware();
        dynamic group = ResolveGroup(software.TypeGroup, payload.FolderPath);
        Export(FindByName(group.Types, payload.ItemName), payload.FilePath);
    }

    public void CreateBlockFolder(TiaFolderPayload payload)
    {
        ValidateFolderPayload(payload);
        dynamic software = RequireSelectedSoftware();
        CreateFolder(ResolveGroup(software.BlockGroup, payload.ParentPath), payload.Name);
    }

    public void CreateDataTypeFolder(TiaFolderPayload payload)
    {
        ValidateFolderPayload(payload);
        dynamic software = RequireSelectedSoftware();
        CreateFolder(ResolveGroup(software.TypeGroup, payload.ParentPath), payload.Name);
    }

    public IReadOnlyList<TiaAxisInfo> ListAxes()
    {
        dynamic software = RequireSelectedSoftware();
        var axes = new List<TiaAxisInfo>();
        ProcessTechnologyGroup(software.TechnologicalObjectGroup, string.Empty, axes, null);
        return axes;
    }

    public IReadOnlyList<TiaAxisInfo> ConfigureAxes(TiaAxisConfigurationPayload payload)
    {
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));

        var selectedIds = new HashSet<string>(
            payload.AxisIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0)
            return Array.Empty<TiaAxisInfo>();

        dynamic software = RequireSelectedSoftware();
        var axes = new List<TiaAxisInfo>();
        ProcessTechnologyGroup(software.TechnologicalObjectGroup, string.Empty, axes, selectedIds);
        var foundIds = axes.Select(axis => axis.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = selectedIds.Where(id => !foundIds.Contains(id)).OrderBy(id => id).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Ausgewählte TIA-Achsen wurden nicht gefunden: {string.Join(", ", missing)}");
        return axes;
    }

    public void Save()
    {
        RequireProject().Save();
    }

    public void Dispose()
    {
        DisposePortal();
    }

    private Assembly RequireAssembly() => _engineeringAssembly
        ?? throw new InvalidOperationException("Select a TIA version before attaching.");

    private dynamic RequireProject() => _project
        ?? throw new InvalidOperationException("TIA Portal is not attached.");

    private dynamic RequireSelectedSoftware()
    {
        if (!_selectedPlcIndex.HasValue)
            throw new InvalidOperationException("Select a PLC first.");

        return TryGetSoftware(_selectedPlcIndex.Value)
            ?? throw new InvalidOperationException("Selected PLC software is no longer available.");
    }

    private dynamic? TryGetSoftware(int deviceIndex)
    {
        if (_project == null || _engineeringAssembly == null)
            return null;

        var devices = GetProjectDevices();
        if (deviceIndex < 0 || deviceIndex >= devices.Count)
            return null;

        return TryGetSoftware(devices[deviceIndex]);
    }

    private dynamic? TryGetSoftware(object device)
    {
        if (_engineeringAssembly == null)
            return null;

        var softwareContainerType = RequireAssembly().GetType(
            "Siemens.Engineering.HW.Features.SoftwareContainer",
            throwOnError: true)!;

        foreach (var deviceItem in EnumerateDeviceItems(device))
        {
            var container = GetService(deviceItem, softwareContainerType);
            var software = container is null ? null : ReadMemberValue(container, "Software");
            if (software is not null)
                return software;
        }

        return null;
    }

    private IReadOnlyList<object> GetProjectDevices()
    {
        var project = (object)RequireProject();
        return new TiaHardwareReader(RequireAssembly()).EnumerateDevices(project);
    }

    private static IEnumerable<object> EnumerateDeviceItems(object parent)
    {
        var items = ReadEnumerableProperty(parent, "DeviceItems");
        if (items.Count == 0)
            items = ReadEnumerableProperty(parent, "Items");

        foreach (var item in items)
        {
            yield return item;
            foreach (var child in EnumerateDeviceItems(item))
                yield return child;
        }
    }

    private static object? GetService(object target, Type serviceType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = target.GetType().GetMethods(flags)
            .Concat(target.GetType().GetInterfaces().SelectMany(type => type.GetMethods()))
            .Where(method => method.Name == "GetService" &&
                             method.IsGenericMethodDefinition &&
                             method.GetParameters().Length == 0);

        foreach (var method in methods)
        {
            try
            {
                var service = method.MakeGenericMethod(serviceType).Invoke(target, null);
                if (service is not null)
                    return service;
            }
            catch (Exception)
            {
                // DeviceItems expose only the services supported by their type.
            }
        }
        return null;
    }

    private static string ReadStringMember(object target, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var value = ReadMemberValue(target, name);
                if (value is not null)
                    return Convert.ToString(value) ?? string.Empty;
            }
            catch (Exception)
            {
                // Dynamic Openness attributes are not supported by every device item.
            }
        }
        return string.Empty;
    }

    private static object? ReadMemberValue(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperties(flags).FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property is not null)
            {
                try
                {
                    return property.GetValue(target, null);
                }
                catch (Exception)
                {
                    // Explicit Openness interfaces are attempted below.
                }
            }
        }

        foreach (var interfaceType in target.GetType().GetInterfaces())
        {
            var property = interfaceType.GetProperties().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                continue;
            try
            {
                return property.GetValue(target, null);
            }
            catch (Exception)
            {
                // Not every Openness proxy supports every interface member.
            }
        }

        try
        {
            return ReadEngineeringAttribute(target, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ReadEngineeringAttribute(object target, string name)
    {
        var publicMethod = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
        if (publicMethod is not null)
            return publicMethod.Invoke(target, new object[] { name });

        var engineeringInterface = target.GetType().GetInterfaces().FirstOrDefault(type =>
            string.Equals(type.FullName, "Siemens.Engineering.IEngineeringObject", StringComparison.Ordinal));
        return engineeringInterface?.GetMethod("GetAttribute", new[] { typeof(string) })
            ?.Invoke(target, new object[] { name });
    }

    private sealed class PortalProcess
    {
        public PortalProcess(
            object process,
            string projectPath,
            string processId,
            string mode,
            string attachedSessions)
        {
            Process = process;
            ProjectPath = projectPath;
            ProcessId = processId;
            Mode = mode;
            AttachedSessions = attachedSessions;
        }

        public object Process { get; }
        public string ProjectPath { get; }
        public string ProcessId { get; }
        public string Mode { get; }
        public string AttachedSessions { get; }
    }

    private sealed class ProjectProbe
    {
        public ProjectProbe(object? project, string diagnostics)
        {
            Project = project;
            Diagnostics = diagnostics;
        }

        public static ProjectProbe Empty { get; } = new(null, "noch keine Abfrage");
        public object? Project { get; }
        public string Diagnostics { get; }
    }

    private sealed class CollectionProbe
    {
        public CollectionProbe(IReadOnlyList<object> values, string diagnostics)
        {
            Values = values;
            Diagnostics = diagnostics;
        }

        public IReadOnlyList<object> Values { get; }
        public string Diagnostics { get; }
    }

    private static void TraverseGroup(dynamic group, string parentPath, string itemCollection, TiaProjectTree tree)
    {
        dynamic items = GetProperty(group, itemCollection);
        foreach (dynamic item in items)
        {
            tree.Items.Add(new TiaProgramItemInfo
            {
                Name = Convert.ToString(item.Name) ?? string.Empty,
                FolderPath = parentPath
            });
        }

        foreach (dynamic child in group.Groups)
        {
            var name = Convert.ToString(child.Name) ?? string.Empty;
            var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
            tree.Folders.Add(new TiaFolderInfo { Name = name, Path = path });
            TraverseGroup(child, path, itemCollection, tree);
        }
    }

    private static dynamic ResolveGroup(dynamic root, string path)
    {
        dynamic current = root;
        foreach (var segment in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            dynamic? next = null;
            foreach (dynamic group in current.Groups)
            {
                if (string.Equals(Convert.ToString(group.Name), segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = group;
                    break;
                }
            }

            current = next ?? throw new InvalidOperationException($"TIA folder was not found: {path}");
        }

        return current;
    }

    private void Import(dynamic composition, string filePath)
    {
        var assembly = RequireAssembly();
        var optionsType = assembly.GetType("Siemens.Engineering.ImportOptions", throwOnError: true);
        dynamic options = Activator.CreateInstance(optionsType);

        optionsType.GetProperty("OverwriteExisting")?.SetValue(options, true, null);
        optionsType.GetProperty("OverrideExisting")?.SetValue(options, true, null);
        optionsType.GetProperty("KeepOriginalName")?.SetValue(options, true, null);
        composition.Import(new FileInfo(filePath), options);
    }

    private void Export(dynamic item, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(filePath))
            File.Delete(filePath);

        var optionsType = RequireAssembly().GetType("Siemens.Engineering.ExportOptions", throwOnError: true);
        dynamic options = Activator.CreateInstance(optionsType);
        item.Export(new FileInfo(filePath), options);
    }

    private static dynamic FindByName(dynamic composition, string name)
    {
        foreach (dynamic item in composition)
        {
            if (string.Equals(Convert.ToString(item.Name), name, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        throw new InvalidOperationException($"TIA item was not found: {name}");
    }

    private static void CreateFolder(dynamic parent, string name)
    {
        foreach (dynamic child in parent.Groups)
        {
            if (string.Equals(Convert.ToString(child.Name), name, StringComparison.OrdinalIgnoreCase))
                return;
        }

        parent.Groups.Create(name);
    }

    private static void ProcessTechnologyGroup(
        dynamic group,
        string parentPath,
        ICollection<TiaAxisInfo> axes,
        ISet<string>? selectedIds)
    {
        var groupName = ReadStringMember(group, "Name");
        var groupPath = string.IsNullOrWhiteSpace(groupName)
            ? parentPath
            : string.IsNullOrWhiteSpace(parentPath) ? groupName : $"{parentPath}/{groupName}";

        if (HasProperty(group, "TechnologicalObjects"))
        {
            foreach (dynamic technologyObject in group.TechnologicalObjects)
            {
                string technologyType = Convert.ToString((object?)technologyObject.OfSystemLibElement) ?? string.Empty;
                if (technologyType.IndexOf("Axis", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string name = Convert.ToString((object?)technologyObject.Name) ?? string.Empty;
                string id = string.IsNullOrWhiteSpace(groupPath) ? name : $"{groupPath}/{name}";
                if (!IsSelectedAxis(selectedIds, id))
                    continue;

                var axis = new TiaAxisInfo
                {
                    Id = id,
                    Name = name,
                    TechnologyType = technologyType,
                    GroupPath = groupPath
                };
                if (selectedIds is not null)
                    axis.ParameterResults.AddRange(ConfigureAxisParameters(technologyObject, name));
                axes.Add(axis);
            }
        }

        if (!HasProperty(group, "Groups"))
            return;

        foreach (dynamic child in group.Groups)
            ProcessTechnologyGroup(child, groupPath, axes, selectedIds);
    }

    private static IReadOnlyList<TiaAxisParameterResult> ConfigureAxisParameters(dynamic technologyObject, string axisName)
    {
        var parameterValues = TiaAxisConfigurationPolicy.CreateParameterValues(axisName);

        var results = new List<TiaAxisParameterResult>();

        foreach (dynamic parameter in technologyObject.Parameters)
        {
            var name = string.Empty;
            int? value = null;
            try
            {
                name = Convert.ToString(parameter.GetAttribute("Name")) ?? string.Empty;
                if (!parameterValues.TryGetValue(name, out var configuredValue))
                    continue;

                value = configuredValue;
                parameter.Value = configuredValue;
                results.Add(new TiaAxisParameterResult
                {
                    Name = name,
                    Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Success = true
                });
            }
            catch (Exception exception)
            {
                if (value is null)
                    continue;
                var root = Unwrap(exception);
                results.Add(new TiaAxisParameterResult
                {
                    Name = name,
                    Value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Success = false,
                    Error = root.Message
                });
                Console.Error.WriteLine($"Could not configure axis parameter '{name}' on '{axisName}': {root.Message}");
            }
        }

        return results;
    }

    private static bool IsSelectedAxis(ISet<string>? selectedIds, string id) =>
        selectedIds is null || selectedIds.Contains(id);

    private static object GetProperty(object target, string propertyName)
    {
        return target.GetType().GetProperty(propertyName)?.GetValue(target, null)
            ?? throw new InvalidOperationException($"TIA object has no property '{propertyName}'.");
    }

    private static bool HasProperty(object target, string propertyName) =>
        target != null && target.GetType().GetProperty(propertyName) != null;

    private static void ValidateImportPayload(TiaTransferPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.FilePath) || !File.Exists(payload.FilePath))
            throw new FileNotFoundException("Import file was not found.", payload.FilePath);
    }

    private static void ValidateExportPayload(TiaTransferPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.ItemName))
            throw new ArgumentException("An item name is required.", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.FilePath))
            throw new ArgumentException("An export path is required.", nameof(payload));
    }

    private static void ValidateFolderPayload(TiaFolderPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Name))
            throw new ArgumentException("A folder name is required.", nameof(payload));
    }

    private void DisposePortal()
    {
        try
        {
            if (_portal is IDisposable disposable)
                disposable.Dispose();
        }
        finally
        {
            _selectedPlcIndex = null;
            _project = null;
            _portal = null;
            _engineeringAssembly = null;
            _engineeringDllPath = null;
        }
    }
}

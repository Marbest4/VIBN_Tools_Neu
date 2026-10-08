using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using FS.SDK;
using FS.SDK.API;
using FS.SDK.Components;
using FS.SDK.Extensibility.Interfaces;
using FS.SDK.Mathematics;
using FS.SDK.Scene.Objects;
using ReadingUnitPlugin.SO;
using VIBN_Tools.Application;
using VIBN_Tools.ModelValidation;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.GlobalClasses.FeeObjects
{
    public class FeeObjectService : IFeeObjectService
    {
        public IReadOnlyList<FeeAbstractObject> AllFeeObjects { get; private set; }

        //public event Action FeeObjectsUpdated;

        public event EventHandler<FeeObjectsUpdatedEventargs> FeeObjectsUpdated;

        // Debounce Timer for user input
        private readonly Dictionary<(object obj, string property), CancellationTokenSource> _debounceUserInput = new Dictionary<(object obj, string property), CancellationTokenSource>();

        private readonly Dictionary<INotifyPropertyChanged, FeeAbstractObject> _subscribedObjects = new();
        private long _activeConnectionRevision = -1;
        private long _snapshotConnectionRevision = -1;

        private readonly object _updateSync = new();
        private readonly SemaphoreSlim _sdkReadGate = new(1, 1);
        private Task? _activeUpdate;
        private bool _isLoadingFeeData = false;




        public async Task GetInitialFeeDataAsync()
        {
            ResetFeeData();

            await UpdateFeeDataAsync();
        }



        //public async Task UpdateFeeDataAsync()
        //{
        //    var startTime = DateTime.Now;

        //    _isLoadingFeeData = true;

        //    // Load all FEE objects
        //    AllFeeObjects = await GetAllFeeObjectsAsync();

        //    // Parent-Mapping
        //    FindAndAssignParents(AllFeeObjects);

        //    // Subscripe to PropertyChanged
        //    SubscribePropertyChanges(AllFeeObjects);

        //    // Plausibility checks of every object
        //    await RunPlausibilityChecks(AllFeeObjects);

        //    _isLoadingFeeData = false;

        //    //==================================================================================
        //    await Task.WhenAll(_debounceTasks);
        //    _debounceTasks.Clear();


        //    var stopTime = DateTime.Now;

        //    // Inform ViewModels
        //    await Task.Yield();       // let UI breath :)

        //    //await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        //    //{
        //    //    FeeObjectsUpdated?.Invoke(this, new FeeObjectsUpdatedEventargs
        //    //    {
        //    //        ElapsedTime = stopTime - startTime,
        //    //    });
        //    //}, System.Windows.Threading.DispatcherPriority.Background);

        //    FeeObjectsUpdated?.Invoke(this, new FeeObjectsUpdatedEventargs
        //    {
        //        ElapsedTime = stopTime - startTime,
        //    });

        //}





        public Task UpdateFeeDataAsync()
        {
            lock (_updateSync)
            {
                var revision = Services.Connection.ConnectionRevision;
                if (_activeUpdate is { IsCompleted: false } active)
                    return _activeConnectionRevision == revision ? active : UpdateAfterPreviousAsync(active);
                _activeConnectionRevision = revision;
                return _activeUpdate = UpdateFeeDataCoreAsync(revision);
            }
        }

        private async Task UpdateAfterPreviousAsync(Task previous)
        {
            try { await previous; }
            catch { /* The previous project's failure must not prevent a fresh read. */ }
            await UpdateFeeDataAsync();
        }

        private void EnsureCurrentConnection(long revision)
        {
            if (!Services.Connection.IsConnected || Services.Connection.ConnectionRevision != revision)
                throw new OperationCanceledException("Die FEE-Verbindung hat sich während des Einlesens geändert.");
        }

        private async Task UpdateFeeDataCoreAsync(long connectionRevision)
        {
            var startTime = DateTime.Now;            
            var snapshotReadTime = TimeSpan.Zero;
            var validationTime = TimeSpan.Zero;

            _isLoadingFeeData = true;
            try
            {
                // A single shared batch is used by Model Validation,
                // Container2FEE Visual and FEE2Container. Concurrent callers
                // await this same operation instead of allocating a second
                // complete project snapshot and racing the vendor client.
                var oldObjects = _snapshotConnectionRevision == connectionRevision ? AllFeeObjects : null;
                DetachPropertyChanges();
                foreach (var cts in _debounceUserInput.Values.ToArray()) cts.Cancel();
                _debounceUserInput.Clear();
                AllFeeObjects = Array.Empty<FeeAbstractObject>();
                EnsureCurrentConnection(connectionRevision);
                var snapshotWatch = Stopwatch.StartNew();
                await _sdkReadGate.WaitAsync();
                List<FeeAbstractObject> newObjects;
                try
                {
                    newObjects = await GetAllFeeObjectsAsync();
                }
                finally
                {
                    _sdkReadGate.Release();
                }
                snapshotReadTime = snapshotWatch.Elapsed;

                EnsureCurrentConnection(connectionRevision);
                FindAndAssignParents(newObjects);
                var validationWatch = Stopwatch.StartNew();
                await RunPlausibilityChecks(newObjects, connectionRevision);
                validationTime = validationWatch.Elapsed;
                EnsureCurrentConnection(connectionRevision);
                AllFeeObjects = MergeAcknowledgeInformation(oldObjects, newObjects);
                _snapshotConnectionRevision = connectionRevision;
                SubscribePropertyChanges(AllFeeObjects);

                await Task.Yield();
                EnsureCurrentConnection(connectionRevision);
                FeeObjectsUpdated?.Invoke(this, new FeeObjectsUpdatedEventargs
                {
                    ElapsedTime = DateTime.Now - startTime,
                    SnapshotReadTime = snapshotReadTime,
                    ValidationTime = validationTime,
                });
            }
            finally
            {
                _isLoadingFeeData = false;
                lock (_updateSync)
                    _activeUpdate = null;
            }

        }

        /// <summary>
        /// Reads the scene hierarchy required by Container2FEE Visual without
        /// loading interfaces, simulation live values or ModelValidation
        /// issues. The same SDK gate as the full refresh is used because the
        /// vendor client is stateful and must not receive competing project
        /// reads from two tabs.
        /// </summary>
        public async Task<IReadOnlyList<FeeAbstractObject>> ReadFeeSceneObjectsForDiscoveryAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectionRevision = Services.Connection.ConnectionRevision;
            await _sdkReadGate.WaitAsync(cancellationToken);
            try
            {
                EnsureCurrentConnection(connectionRevision);
                var sceneObjects = await GetFeeSceneObjectsForDiscoveryAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCurrentConnection(connectionRevision);
                FindAndAssignParents(sceneObjects);
                return sceneObjects;
            }
            finally
            {
                _sdkReadGate.Release();
            }
        }

        private static async Task<List<FeeAbstractObject>> GetFeeSceneObjectsForDiscoveryAsync(CancellationToken cancellationToken)
        {
            // The SDK shares request state across its API facades. Read each
            // response before starting the next request, and bound XML batches
            // so a large project does not produce one oversized request.
            var allGuids = await Services.ApiInstance.Object.GetSceneObjectGuidsAsync() ?? [];
            cancellationToken.ThrowIfCancellationRequested();
            var ignoredDecorationGuids = await ReadIgnoredDecorationGuidsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var guidTexts = allGuids
                .Where(guid => !ignoredDecorationGuids.Contains(guid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var logicDefinitions = (IReadOnlyList<ApiLogicDefinition>)(await Services.ApiInstance.Logic
                .GetAllAvailableLogicDefinitionsAsync() ?? []).ToArray();
            var xmlTexts = new List<string>(guidTexts.Length);
            foreach (var batch in guidTexts.Chunk(128))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = xmlTexts.Count + 1;
                try
                {
                    var response = (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(batch) ?? []).ToArray();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (response.Length != batch.Length)
                        throw new InvalidOperationException($"FEE lieferte {response.Length} statt {batch.Length} XML-Datensätze.");
                    xmlTexts.AddRange(response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"FEE-Objekte {first} bis {first + batch.Length - 1} von {guidTexts.Length} konnten nicht gelesen werden: {exception.Message}",
                        exception);
                }
            }

            var sceneObjects = new FeeAbstractObject[guidTexts.Length];
            var count = guidTexts.Length;
            Parallel.For(0, count, index =>
            {
                var xml = XElement.Parse(xmlTexts[index]);
                var guidText = guidTexts[index];
                var name = xml.Attribute("Name")?.Value;
                var type = xml.Attribute("Type")?.Value ?? xml.Name.LocalName;
                if (FeeSceneObjectReadPolicy.IsIgnoredType(type))
                    return;
                var item = FeeObjectFactory.Create(type, name, guidText);
                if (item is null)
                    return;

                item.StoreXmlObjectProperties(xml, Guid.Parse(guidText));
                item.ApplyBatchData(new FeePropertyBatchData
                {
                    AllLogicDefinitions = logicDefinitions,
                });
                sceneObjects[index] = item;
            });

            return sceneObjects.Where(item => item is not null).ToList();
        }





        private async Task<List<FeeAbstractObject>> GetAllFeeObjectsAsync()
        {
            // Create Guid Batch Tasks
            var guidsTask = Services.ApiInstance.Object.GetSceneObjectGuidsAsync();
            var ignoredDecorationGuidsTask = ReadIgnoredDecorationGuidsAsync();
            var guidsJointsTask = Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(nameof(MotionJoint));
            var guidsSurfacesTask = Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(nameof(Surface));
            var guidsPickPlacesTask = Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync(nameof(PickAndPlace));

            var logicDefsTask = Services.ApiInstance.Logic.GetAllAvailableLogicDefinitionsAsync();

            // Start Tasks
            await Task.WhenAll(
                guidsTask,
                ignoredDecorationGuidsTask,
                guidsJointsTask,
                guidsSurfacesTask,
                guidsPickPlacesTask);

            var ignoredDecorationGuids = await ignoredDecorationGuidsTask;
            string[] stringGuids = (await guidsTask)
                .Where(guid => !ignoredDecorationGuids.Contains(guid))
                .ToArray();
            Guid[] guids = stringGuids.Select(x => Guid.Parse(x)).ToArray();

            string[] stringGuidsJoints = (await guidsJointsTask).ToArray();
            Guid[] guidsJoints = stringGuidsJoints.Select(x => Guid.Parse(x)).ToArray();

            string[] stringGuidsSurfaces = (await guidsSurfacesTask).ToArray();
            Guid[] guidsSurfaces = stringGuidsSurfaces.Select(x => Guid.Parse(x)).ToArray();

            string[] stringGuidsPickPlaces = (await guidsPickPlacesTask).ToArray();
            Guid[] guidsPickPLaces = stringGuidsPickPlaces.Select(x => Guid.Parse(x)).ToArray();


            // Create Global Batch Tasks
            var xmlTask = Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync(stringGuids);

            // Properties
            var posTask = Services.ApiInstance.Object.GetPropertiesAsync(stringGuids, nameof(SceneObject.Transform.Position), nameof(SceneObject.Transform));
            var rotTask = Services.ApiInstance.Object.GetPropertiesAsync(stringGuids, nameof(SceneObject.Transform.Rotation), nameof(SceneObject.Transform));

            // Start Tasks
            await Task.WhenAll(logicDefsTask, xmlTask, posTask, rotTask);



            // Create Specific Batch Tasks
            var jointManualTask = Services.ApiInstance.Object.GetPropertiesAsync(stringGuidsJoints, nameof(JointControllerComponent.IsManualModeEnabled), "Controller");
            var surfaceManualTask = Services.ApiInstance.Object.GetPropertiesAsync(stringGuidsSurfaces, "IsManualModeEnabled");

            // Slot Values
            var jointPosTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsJoints, Enumerable.Repeat("InValue", guidsJoints.Length).ToArray());
            var jointVelTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsJoints, Enumerable.Repeat("InVelocity", guidsJoints.Length).ToArray());
            var jointTargetTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsJoints, Enumerable.Repeat("InTarget", guidsJoints.Length).ToArray());
            var jointActualTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsJoints, Enumerable.Repeat("OutValue", guidsJoints.Length).ToArray());


            var pickTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsPickPLaces, Enumerable.Repeat("Pick", guidsPickPLaces.Length).ToArray());
            var dropTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsPickPLaces, Enumerable.Repeat("Drop", guidsPickPLaces.Length).ToArray());

            var surfVelXTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsSurfaces, Enumerable.Repeat("InVelocityX", guidsSurfaces.Length).ToArray());
            var surfVelYTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsSurfaces, Enumerable.Repeat("InVelocityY", guidsSurfaces.Length).ToArray());
            var surfVelZTask = Services.ApiInstance.Object.GetSlotValuesAsync(guidsSurfaces, Enumerable.Repeat("InVelocityZ", guidsSurfaces.Length).ToArray());

            // Start Tasks
            await Task.WhenAll(jointManualTask, surfaceManualTask, jointPosTask, jointVelTask, jointTargetTask, jointActualTask, pickTask, dropTask, surfVelXTask, surfVelYTask, surfVelZTask);



            // Store data
            var xmlList = (await xmlTask).ToArray();
            var xmlElements = new XElement[xmlList.Length];
            Parallel.For(0, xmlList.Length, index =>
                xmlElements[index] = XElement.Parse(xmlList[index]));

            var positions = (await posTask).Select(x => Services.ApiInstance.XmlHelper.ConvertToVector3(x)).ToList();
            var rotations = (await rotTask).Select(x => Services.ApiInstance.XmlHelper.ConvertToVector3(x)).ToList();

            IReadOnlyList<ApiLogicDefinition> allLogicDefinitions =
                (await logicDefsTask).ToArray();



            // Create Dictionaries for later mapping to global data
            // joint
            var jointManualDict = guidsJoints.Zip(await jointManualTask).ToDictionary(x => x.First, x => Services.ApiInstance.XmlHelper.ConvertToBool(x.Second));
            var jointPosDict = guidsJoints.Zip(await jointPosTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));
            var jointVelDict = guidsJoints.Zip(await jointVelTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));
            var jointTargetDict = guidsJoints.Zip(await jointTargetTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));
            var jointActualDict = guidsJoints.Zip(await jointActualTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));

            // Surface
            var surfaceManualDict = guidsSurfaces.Zip(await surfaceManualTask).ToDictionary(x => x.First, x => Services.ApiInstance.XmlHelper.ConvertToBool(x.Second));
            var surfaceVelXDict = guidsSurfaces.Zip(await surfVelXTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));
            var surfaceVelYDict = guidsSurfaces.Zip(await surfVelYTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));
            var surfaceVelZDict = guidsSurfaces.Zip(await surfVelZTask).ToDictionary(x => x.First, x => Convert.ToSingle(x.Second));

            // Pick and Place
            var pickPlacePickDict = guidsPickPLaces.Zip(await pickTask).ToDictionary(x => x.First, x => Convert.ToBoolean(x.Second));
            var pickPlaceDropDict = guidsPickPLaces.Zip(await dropTask).ToDictionary(x => x.First, x => Convert.ToBoolean(x.Second));


            // List of BatchData
            var batchData = new FeePropertyBatchData[guids.Length];

            for (int i = 0; i < guids.Length; i++)
            {
                var id = guids[i];

                batchData[i] = new FeePropertyBatchData
                {
                    Position = positions[i],
                    Rotation = rotations[i],

                    // Joint
                    JointManualModeactive = jointManualDict.TryGetValue(id, out var isJointManual) ? isJointManual : null,
                    PositionValue = jointPosDict.TryGetValue(id, out var posValue) ? posValue : null,
                    VelocityValue = jointVelDict.TryGetValue(id, out var velValue) ? velValue : null,
                    TargetPositionValue = jointTargetDict.TryGetValue(id, out var targetValue) ? targetValue : null,
                    IsActualPosition = jointActualDict.TryGetValue(id, out var actualValue) ? actualValue : null,

                    // Surface
                    SurfaceManualModeActive = surfaceManualDict.TryGetValue(id, out var isSurfaceManual) ? isSurfaceManual : null,
                    IsActualVelocityX = surfaceVelXDict.TryGetValue(id, out var velXValue) ? velXValue : null,
                    IsActualVelocityY = surfaceVelYDict.TryGetValue(id, out var velYValue) ? velYValue : null,
                    IsActualVelocityZ = surfaceVelZDict.TryGetValue(id, out var velZValue) ? velZValue : null,

                    // Pick and Place
                    IsPick = pickPlacePickDict.TryGetValue(id, out var isPick) ? isPick : null,
                    IsDrop = pickPlaceDropDict.TryGetValue(id, out var isDrop) ? isDrop : null,

                    // Logic definitions are immutable snapshot data. Sharing
                    // one array avoids one complete list copy per scene object.
                    AllLogicDefinitions = allLogicDefinitions,
                };
            }



            var sceneObjects = new FeeAbstractObject[stringGuids.Length];

            Parallel.For(0, stringGuids.Length, i =>
            {
                var guid = stringGuids[i];
                var xElmt = xmlElements[i];

                var name = xElmt.Attribute("Name")?.Value;
                var type = xElmt.Attribute("Type")?.Value ?? xElmt.Name.LocalName;

                // Fallback for SDK versions that do not return every
                // Decoration from GetSceneObjectGuidsOfTypeAsync.
                if (FeeSceneObjectReadPolicy.IsIgnoredType(type))
                    return;

                var obj = FeeObjectFactory.Create(type, name, guid);
                if (obj == null)
                    return;

                obj.StoreXmlObjectProperties(xElmt, Guid.Parse(guid));
                obj.ApplyBatchData(batchData[i]);

                sceneObjects[i] = obj;
            });

            var result = sceneObjects.Where(o => o != null).ToList();

            // Load Interfaces & Signals
            var interfaces = await FeeInterface.GetAllInterfacesAsync();

            result.AddRange(interfaces);
            return result;

        }

        private static async Task<HashSet<string>> ReadIgnoredDecorationGuidsAsync()
        {
            try
            {
                return (await Services.ApiInstance.Object
                        .GetSceneObjectGuidsOfTypeAsync(FeeSceneObjectReadPolicy.DecorationTypeName) ?? [])
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                // XML-type filtering still guarantees the functional exclusion;
                // only the early performance optimization is unavailable.
                ApplicationLogService.Instance.Warning(
                    "FEE object read",
                    "Decoration-GUIDs konnten nicht vorab gelesen werden. " +
                    "Decoration-Objekte werden nach dem XML-Batch gefiltert.",
                    exception.Message);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }





        private List<FeeAbstractObject> MergeAcknowledgeInformation(IReadOnlyList<FeeAbstractObject> oldObjects, List<FeeAbstractObject> newObjects)
        {
            if (oldObjects == null || oldObjects.Count == 0)
                return newObjects;

            var oldLookup = oldObjects.ToDictionary(o => o.Guid);


            foreach (var newObj in newObjects)
            {
                // Skip, if new object is completely new
                if (!oldLookup.TryGetValue(newObj.Guid, out var oldObj))
                    continue;

                // Skip if new object has no issues
                if (newObj.PlausibilityIssues.Count == 0)
                    continue;

                foreach (var newIssue in newObj.PlausibilityIssues)
                {
                    var oldIssue = oldObj.PlausibilityIssues.FirstOrDefault(i => i.Message == newIssue.Message);

                    // Merge only, if issue existed in old object AND was acknowledged
                    if (oldIssue != null && oldIssue.IsAcknowledged)
                        newIssue.IsAcknowledged = true;
                }
            }

            return newObjects;

        }

        







        private void FindAndAssignParents(IEnumerable<FeeAbstractObject> feeObjects)
        {
            var lookup = feeObjects.ToDictionary(x => x.Guid);

            foreach (var obj in feeObjects)
            {
                if (obj.ChildrenGuids == null)
                    continue;

                foreach (var childGuid in obj.ChildrenGuids)
                {
                    if (lookup.TryGetValue(childGuid, out var child))
                    {
                        child.Parent = obj;
                    }
                }
            }

            //AllFeeObjects = feeObjects;
        }



        //private void SubscribePropertyChanges(IEnumerable<FeeAbstractObject> feeObjects)
        //{
        //    // Subscribe to PropertyChanged of every object
        //    foreach (var obj in feeObjects)
        //    {
        //        // Check if already subscribed
        //        if (_subscribedObjects.Contains(obj))
        //            continue;

        //        _subscribedObjects.Add(obj);

        //        PropertyChangedEventManager.AddHandler(obj, OnFeeObjectChanged, string.Empty);

        //        foreach (var issue in obj.PlausibilityIssues)
        //        {
        //            PropertyChangedEventManager.AddHandler(issue, OnFeeObjectChanged, string.Empty);
        //        }

        //        if (obj is FeeInterface iface)
        //        {
        //            foreach (var signal in iface.Signals)
        //            {
        //                PropertyChangedEventManager.AddHandler(signal, OnFeeObjectChanged, string.Empty);
        //            }
        //        }
        //    }
        //}



        private void DetachPropertyChanges()
        {
            foreach (var source in _subscribedObjects.Keys)
                WeakEventManager<INotifyPropertyChanged, PropertyChangedEventArgs>.RemoveHandler(
                    source, nameof(INotifyPropertyChanged.PropertyChanged), OnFeeObjectChanged);
            _subscribedObjects.Clear();
        }

        private void SubscribePropertyChanges(IEnumerable<FeeAbstractObject> feeObjects)
        {
            foreach (var obj in feeObjects)
            {
                Subscribe(obj, obj);
                foreach (var issue in obj.PlausibilityIssues) Subscribe(issue, obj);
                if (obj is FeeInterface iface)
                    foreach (var signal in iface.Signals) Subscribe(signal, signal);
            }
            void Subscribe(INotifyPropertyChanged source, FeeAbstractObject owner)
            {
                if (!_subscribedObjects.TryAdd(source, owner)) return;
                WeakEventManager<INotifyPropertyChanged, PropertyChangedEventArgs>.AddHandler(
                    source, nameof(INotifyPropertyChanged.PropertyChanged), OnFeeObjectChanged);
            }
        }

        private async void OnFeeObjectChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isLoadingFeeData || sender is not INotifyPropertyChanged source ||
                !_subscribedObjects.TryGetValue(source, out var owner)) return;
            // Issue changes update acknowledgement/filters; they are not SDK property edits.
            if (sender is PlausibilityIssue) { owner.NotifyIssueStateChanged(); return; }
            try { await HandleFeeObjectChangedAsync(owner, e); }
            catch (Exception exception)
            {
                ApplicationLogService.Instance.Error("Model Validation", "Objektänderung konnte nicht geprüft werden.", exception);
            }
        }

        private async Task HandleFeeObjectChangedAsync(object sender, PropertyChangedEventArgs e)
        {

            var key = (sender, e.PropertyName);

            if (_debounceUserInput.TryGetValue(key, out var existingCts))
                existingCts.Cancel();

            var cts = new CancellationTokenSource();
            _debounceUserInput[key] = cts;

            try
            {
                await Task.Delay(250, cts.Token);

                cts.Token.ThrowIfCancellationRequested();
                if (!_subscribedObjects.ContainsKey((INotifyPropertyChanged)sender) || _isLoadingFeeData) return;
                var obj = (FeeAbstractObject)sender;
                await ModelValidationService.Router.HandleChangeAsync(obj, e.PropertyName);
            }
            catch (OperationCanceledException) { /* Debounced or replaced project. */ }
            finally
            {
                if (_debounceUserInput.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                    _debounceUserInput.Remove(key);
                cts.Dispose();
            }
        }




        private async Task RunPlausibilityChecks(IEnumerable<FeeAbstractObject> feeObjects, long connectionRevision)
        {
            var basicFrames = feeObjects.Where(x => x.FeeType == nameof(FeeBasicFrame)).OfType<FeeBasicFrame>().ToList();

            await Parallel.ForEachAsync(feeObjects, async (obj, token) =>
            {
                EnsureCurrentConnection(connectionRevision);
                // Delete all issues before
                obj.PlausibilityIssues.Clear();

                // Basic Check
                if (obj is IPlausibilityCheck checker)
                {
                    await checker.CheckObjectIssuesAsync(feeObjects);
                    EnsureCurrentConnection(connectionRevision);
                }

                // Special Check (BasicFrames involved)
                if (obj is IPlausibilityCheck<List<FeeBasicFrame>> frameChecker)
                {
                    await frameChecker.CheckObjectIssuesAsync(basicFrames);
                }
            });
        }




        private void ResetFeeData()
        {
            AllFeeObjects = Array.Empty<FeeAbstractObject>();
            DetachPropertyChanges();
            _snapshotConnectionRevision = -1;

            foreach (var cts in _debounceUserInput.Values)
                cts.Cancel();

            _debounceUserInput.Clear();
        }



    }







    public class FeeObjectsUpdatedEventargs : EventArgs
    {
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan SnapshotReadTime { get; set; }
        public TimeSpan ValidationTime { get; set; }
    }





    public static class FeeObjectFactory
    {
        private static readonly Dictionary<string, Func<string, string, FeeAbstractObject>> _map = new Dictionary<string, Func<string, string, FeeAbstractObject>>()
        {
            { nameof(BasicFrame), (name,guid) => new FeeBasicFrame { Name = name, GuidString = guid } },
            { nameof(Button), (name,guid) => new FeeButton { Name = name, GuidString = guid } },
            { nameof(Decoration), (name,guid) => new FeeDecoration { Name = name, GuidString = guid } },
            { nameof(DetectFlag), (name,guid) => new FeeDetectionFlag { Name = name, GuidString = guid } },
            { nameof(Floor), (name,guid) => new FeeFloor { Name = name, GuidString = guid } },
            { nameof(SequenceInserter), (name,guid) => new FeeInserter { Name = name, GuidString = guid } },
            { nameof(MotionJoint), (name,guid) => new FeeJoint { Name = name, GuidString = guid } },
            { nameof(LabelObject), (name,guid) => new FeeLabel { Name = name, GuidString = guid } },
            { nameof(LogicObject), (name,guid) => new FeeLogic { Name = name, GuidString = guid } },
            { nameof(PickAndPlace), (name,guid) => new FeePickAndPlace { Name = name, GuidString = guid } },
            { nameof(ReadingUnitUdt), (name,guid) => new FeeReadingUnit { Name = name, GuidString = guid } },
            { nameof(Remover), (name,guid) => new FeeRemover { Name = name, GuidString = guid } },
            { nameof(Reparenter), (name,guid) => new FeeReparenter { Name = name, GuidString = guid } },
            { nameof(SegmentedLamp), (name,guid) => new FeeSegmentedLamp { Name = name, GuidString = guid } },
            { nameof(Sensor), (name,guid) => new FeeSensor { Name = name, GuidString = guid } },
            { nameof(SafetySensor), (name,guid) => new FeeSensor { Name = name, GuidString = guid } },
            { nameof(Surface), (name,guid) => new FeeSurface { Name = name, GuidString = guid } },
            { nameof(WritingUnitUdt), (name,guid) => new FeeWritingUnit { Name = name, GuidString = guid } },
            { nameof(KinematicFrame), (name,guid) => new FeeKinematicFrame { Name = name, GuidString = guid } },
            { "BoolNot", (name,guid) => new FeeSimpleNot { Name = name, GuidString = guid } },
            { "MoveBit", (name,guid) => new FeeSimpleMove { Name = name, GuidString = guid } },
            { "BoolAnd", (name,guid) => new FeeSimpleAnd { Name = name, GuidString = guid } },
            { "BoolOr", (name,guid) => new FeeSimpleOr { Name = name, GuidString = guid } },
            { "Cabinet", (name,guid) => new FeeCabinet { Name = name, GuidString = guid } },
            { "CabinetElement", (name,guid) => new FeeCabinetElement { Name = name, GuidString = guid } },
        };

        public static FeeAbstractObject Create(string type, string name, string guid)
        {
            if (_map.TryGetValue(type, out var ctor))
            {
                return ctor(name, guid);
            }

            // Keep SDK types without a specialized validation model in the
            // scene snapshot. Reverse export must never silently lose them.
            return new FeeAbstractObject { Name = name, GuidString = guid, FeeType = type };

            ////Fallback with FeeAbstractObject as object
            //return new FeeAbstractObject()
            //{
            //    Name = name,
            //    GuidString = guid,
            //    Type = type,
            //};
        }
    }


    public class FeePropertyBatchData
    {
        // General
        public Vector3? Position { get; set; }
        public Vector3? Rotation { get; set; }

        // Joint
        public bool? JointManualModeactive { get; set; }
        public float? PositionValue { get; set; }
        public float? VelocityValue { get; set; }
        public float? TargetPositionValue { get; set; }
        public float? IsActualPosition { get; set; }

        // Logic
        public IReadOnlyList<ApiLogicDefinition> AllLogicDefinitions { get; set; } =
            Array.Empty<ApiLogicDefinition>();

        // Surface
        public bool? SurfaceManualModeActive { get; set; }
        public float? IsActualVelocityX { get; set; }
        public float? IsActualVelocityY { get; set; }
        public float? IsActualVelocityZ { get; set; }

        // Pick and Place
        public bool? IsPick { get; set; }
        public bool? IsDrop { get; set; }
    }



}

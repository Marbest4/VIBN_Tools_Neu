using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using FS.SDK.Components;
using FS.SDK.Mathematics;
using FS.SDK.Scene.Objects;
using FS.SDK.Utilities;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.FeeObjects.FeeLogic;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFee.GrobStandard
{
    public class GrobLiftUnit_Container : ContainerBaseClass, ISimObjectFindOrSelect, ILogicSimObjectOwner
    {

        public GrobLiftUnit_Container()
        {
            SlotAssignment = new Dictionary<string, PropertyInfo>()
            {
                {LogicsStandard.Grob_LiftUnit.Slots.ToHomePos, typeof(GrobLiftUnit_Container).GetProperty("Signal_ToHomePos") },
                {LogicsStandard.Grob_LiftUnit.Slots.ToWorkPos, typeof(GrobLiftUnit_Container).GetProperty("Signal_ToWorkPos") },

                {LogicsStandard.Grob_LiftUnit.Slots.InHomePos, typeof(GrobLiftUnit_Container).GetProperty("Signal_InHomePos") },
                {LogicsStandard.Grob_LiftUnit.Slots.InMiddlePos, typeof(GrobLiftUnit_Container).GetProperty("Signal_InMiddlePos") },
                {LogicsStandard.Grob_LiftUnit.Slots.InWorkPos, typeof(GrobLiftUnit_Container).GetProperty("Signal_InWorkPos") },

                {LogicsStandard.Grob_LiftUnit.Slots.ReleaseClamping, typeof(GrobLiftUnit_Container).GetProperty("Signal_ReleaseClamping") },
                {LogicsStandard.Grob_LiftUnit.Slots.ClampingReleased, typeof(GrobLiftUnit_Container).GetProperty("Signal_ClampingReleased") },
            };
        }



        public FeeLogic Logic_LiftUnit { get; set; }

        public FeeInterfaceSignal Signal_ToHomePos { get; set; }
        public FeeInterfaceSignal Signal_ToWorkPos { get; set; }

        public FeeInterfaceSignal Signal_InHomePos { get; set; }
        public FeeInterfaceSignal Signal_InMiddlePos { get; set; }
        public FeeInterfaceSignal Signal_InWorkPos { get; set; }

        public FeeInterfaceSignal Signal_ReleaseClamping { get; set; }
        public FeeInterfaceSignal Signal_ClampingReleased { get; set; }

        public List<FeeJoint> Joints_LiftUnit { get; set; } = new List<FeeJoint>();

        public float Parameter_HomePos { get; set; } = -1f;
        public float Parameter_WorkPos { get; set; } = -1f;
        public float Parameter_OperationTime { get; set; } = -1f;

        public bool IsCreationRequested { get; set; }



        void ISimObjectFindOrSelect.FindSimObjects(ObservableCollection<FeeAbstractObject> mappableSimObjects)
        {
            Joints_LiftUnit = FindSimObjectsByNameAndType<FeeJoint>(mappableSimObjects);
        }

        IEnumerable<SimObjectTarget> ISimObjectFindOrSelect.GetSimObjectTargets()
        {
            yield return new SimObjectTarget()
            {
                DisplayName = "MotionJoints",

                AllowedType = typeof(FeeJoint),

                AllowMultiSelect = true,

                GetObjects = () => Joints_LiftUnit,

                AssignObjects = objects =>
                {
                    Joints_LiftUnit = objects.OfType<FeeJoint>().ToList();
                }
            };
        }





        async Task<FeeLogic> ILogicSimObjectOwner.CreateLogicAsync(FeeAbstractObject parentObject)
        {
            Logic_LiftUnit = new FeeLogic()
            {
                Name = this.ComponentName,
                LogicDefinitionName = LogicsStandard.Grob_LiftUnit.Name,
                LogicDefinitionPath = LogicsStandard.Grob_LiftUnit.Path,
                Parent = parentObject,
            };

            (Logic_LiftUnit.LogicDefinitionGuid, Logic_LiftUnit.LogicDefinitionVersion) = await GetOrImportLogicDefinition(Logic_LiftUnit.LogicDefinitionName, Logic_LiftUnit.LogicDefinitionPath);
            await Logic_LiftUnit.CreateSendAssignAndWaitAsync();

            return Logic_LiftUnit;
        }

        async Task ILogicSimObjectOwner.AssignSignalsAsync(FeeInterface targetInterface)
        {
            // Map signals to LogicObject if existing
            var mappings = new (FeeInterfaceSignal Signal, string SlotName)[]
            {
                (Signal_ToHomePos, LogicsStandard.Grob_LiftUnit.Slots.ToHomePos),
                (Signal_ToWorkPos, LogicsStandard.Grob_LiftUnit.Slots.ToWorkPos),
                (Signal_InHomePos, LogicsStandard.Grob_LiftUnit.Slots.InHomePos),
                (Signal_InMiddlePos, LogicsStandard.Grob_LiftUnit.Slots.InMiddlePos),
                (Signal_InWorkPos, LogicsStandard.Grob_LiftUnit.Slots.InWorkPos),
                (Signal_ReleaseClamping, LogicsStandard.Grob_LiftUnit.Slots.ReleaseClamping),
                (Signal_ClampingReleased, LogicsStandard.Grob_LiftUnit.Slots.ClampingReleased),
            };

            foreach (var (signal, slotname) in mappings)
            {
                if (signal != null)
                {
                    await signal.CreateSignalAsync(targetInterface);
                    await ContainerSlotLinkService.AssignVariableAndVerifyAsync(
                        Logic_LiftUnit.Guid,
                        slotname,
                        signal.Guid,
                        $"LiftUnit {ComponentName}: {slotname}");
                }
            }

            // Map parameters
            Services.ApiInstance.Object.SetSlotValue(
                Logic_LiftUnit.Guid,
                LogicsStandard.Grob_LiftUnit.Slots.HomePos,
                Parameter_HomePos == -1f ? ContainerGeneratedObjectDefaults.MotionHomePosition : Parameter_HomePos);
            Services.ApiInstance.Object.SetSlotValue(
                Logic_LiftUnit.Guid,
                LogicsStandard.Grob_LiftUnit.Slots.WorkPos,
                Parameter_WorkPos == -1f ? ContainerGeneratedObjectDefaults.MotionWorkPosition : Parameter_WorkPos);
            Services.ApiInstance.Object.SetSlotValue(
                Logic_LiftUnit.Guid,
                LogicsStandard.Grob_LiftUnit.Slots.OperationTime,
                Parameter_OperationTime == -1f ? ContainerGeneratedObjectDefaults.MotionOperationTime : Parameter_OperationTime);
        }

        async Task ILogicSimObjectOwner.CreateSimObjectsAsync()
        {
            if (!Joints_LiftUnit.Any() && IsCreationRequested)
            {
                var joint = new FeeJoint()
                {
                    Name = this.ComponentName,
                    Parent = Logic_LiftUnit,
                    JointType = MotionType.Translate,
                    ControlType = MotionSource.Position,
                    Position = new Vector3(0, 0, 0),
                    Scale = ContainerGeneratedObjectDefaults.MotionJointScale,
                };

                await joint.CreateAsync();
                await joint.SendAndWaitAsync();
                Joints_LiftUnit.Add(joint);
            }
        }

        async Task ILogicSimObjectOwner.AssignSimObjectsAsync()
        {
            if (Joints_LiftUnit.Any())
            {
                bool isActualPositionConnected = false;

                // Lists with slot assignments for later assignment
                var slotsToAssignTarget = new List<(Guid, string)>() { (Logic_LiftUnit.Guid, LogicsStandard.Grob_LiftUnit.Slots.TargetPosition) };
                var slotsToAssignVelocity = new List<(Guid, string)>() { (Logic_LiftUnit.Guid, LogicsStandard.Grob_LiftUnit.Slots.Velocity) };

                foreach (var joint in Joints_LiftUnit)
                {
                    // Set ControlType to Position
                    Services.ApiInstance.Object.CreateObject(nameof(MotionJoint), joint.Guid);
                    await Services.ApiInstance.Object.SetPropertyAsync(joint.Guid, nameof(JointControllerComponent.MotionSource), MotionSource.Position, "Controller");
                    await Services.ApiInstance.Object.SendAndWait(joint.Guid);

                    if (!isActualPositionConnected)
                    {
                        isActualPositionConnected = await ContainerSlotLinkService.AssignAndVerifyAsync(
                            Logic_LiftUnit.Guid,
                            LogicsStandard.Grob_LiftUnit.Slots.ActualPosition,
                            joint.Guid,
                            "OutValue",
                            $"LiftUnit {ComponentName}: SIM_ActualPosition");
                    }

                    slotsToAssignTarget.Add((joint.Guid, "InTarget"));
                    slotsToAssignVelocity.Add((joint.Guid, "InVelocity"));

                }

                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssignTarget,
                    $"LiftUnit {ComponentName}: SIM_TargetPosition");
                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssignVelocity,
                    $"LiftUnit {ComponentName}: SIM_Velocity");

            }

        }


    }
}

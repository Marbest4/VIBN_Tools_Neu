using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Automation;
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
    public class GrobCylinder_Container : ContainerBaseClass, ISimObjectFindOrSelect, ILogicSimObjectOwner
    {

        public GrobCylinder_Container()
        {
            SlotAssignment = new Dictionary<string, PropertyInfo>()
            {
                {LogicsStandard.Grob_Cylinder.Slots.ToHomePos, typeof(GrobCylinder_Container).GetProperty(nameof(Signal_ToHomePos)) },
                {LogicsStandard.Grob_Cylinder.Slots.ToWorkPos, typeof(GrobCylinder_Container).GetProperty(nameof(Signal_ToWorkPos)) },

                {LogicsStandard.Grob_Cylinder.Slots.InHomePos, typeof(GrobCylinder_Container).GetProperty(nameof(Signals_InHomePos)) },

                {LogicsStandard.Grob_Cylinder.Slots.InWorkPos, typeof(GrobCylinder_Container).GetProperty(nameof(Signals_InWorkPos)) },

                {LogicsStandard.Grob_Cylinder.Slots.ReleaseClamping, typeof(GrobCylinder_Container).GetProperty(nameof(Signal_ReleaseClamping)) },
                {LogicsStandard.Grob_Cylinder.Slots.ClampingReleased, typeof(GrobCylinder_Container).GetProperty(nameof(Signal_ClampingReleased)) },
            };
        }


        public FeeLogic Logic_Cylinder { get; set; }

        public FeeInterfaceSignal Signal_ToHomePos { get; set; }
        public FeeInterfaceSignal Signal_ToWorkPos { get; set; }

        public List<FeeInterfaceSignal> Signals_InHomePos { get; set; }
        public List<FeeInterfaceSignal> Signals_InWorkPos { get; set; }

        public FeeInterfaceSignal Signal_ReleaseClamping { get; set; }
        public FeeInterfaceSignal Signal_ClampingReleased { get; set; }

        public List<FeeJoint> Joints_Cylinder { get; set; } = new List<FeeJoint>();


        public float Parameter_HomePos { get; set; } = -1f;
        public float Parameter_WorkPos { get; set; } = -1f;
        public float Parameter_OperationTime { get; set; } = -1f;

        public bool IsCreationRequested { get; set; }




        void ISimObjectFindOrSelect.FindSimObjects(ObservableCollection<FeeAbstractObject> mappableSimObjects)
        {
            Joints_Cylinder = FindSimObjectsByNameAndType<FeeJoint>(mappableSimObjects);
        }

        IEnumerable<SimObjectTarget> ISimObjectFindOrSelect.GetSimObjectTargets()
        {
            yield return new SimObjectTarget()
            {
                DisplayName = "MotionJoints",

                AllowedType = typeof(FeeJoint),

                AllowMultiSelect = true,

                GetObjects = () => Joints_Cylinder,

                AssignObjects = objects =>
                {
                    Joints_Cylinder = objects.OfType<FeeJoint>().ToList();
                }
            };
        }





        async Task<FeeLogic> ILogicSimObjectOwner.CreateLogicAsync(FeeAbstractObject parentObject)
        {
            Logic_Cylinder = new FeeLogic()
            {
                Name = this.ComponentName,
                LogicDefinitionName = LogicsStandard.Grob_Cylinder.Name,
                LogicDefinitionPath = LogicsStandard.Grob_Cylinder.Path,
                Parent = parentObject,
            };

            (Logic_Cylinder.LogicDefinitionGuid, Logic_Cylinder.LogicDefinitionVersion) = await FeeLogic.GetOrImportLogicDefinition(Logic_Cylinder.LogicDefinitionName, Logic_Cylinder.LogicDefinitionPath);
            await Logic_Cylinder.CreateSendAssignAndWaitAsync();

            return Logic_Cylinder;
        }

        async Task ILogicSimObjectOwner.AssignSignalsAsync(FeeInterface targetInterface)
        {

            // Map signals to LogicObject if existing
            var singleMappings = new (FeeInterfaceSignal Signal, string SlotName)[]
            {
                (Signal_ToHomePos, LogicsStandard.Grob_Cylinder.Slots.ToHomePos),
                (Signal_ToWorkPos, LogicsStandard.Grob_Cylinder.Slots.ToWorkPos),
                (Signal_ReleaseClamping, LogicsStandard.Grob_GripperBasic.Slots.ReleaseClamping),
                (Signal_ClampingReleased, LogicsStandard.Grob_GripperBasic.Slots.ClampingReleased),
            };

            var listMappings = new (List<FeeInterfaceSignal> Signals, string SlotName)[]
            {
                (Signals_InHomePos, LogicsStandard.Grob_Cylinder.Slots.InHomePos),
                (Signals_InWorkPos, LogicsStandard.Grob_Cylinder.Slots.InWorkPos),
            };

            foreach (var (signal, slotname) in singleMappings)
            {
                if (signal != null)
                {
                    await signal.CreateSignalAsync(targetInterface);
                    await ContainerSlotLinkService.AssignVariableAndVerifyAsync(
                        Logic_Cylinder.Guid,
                        slotname,
                        signal.Guid,
                        $"Cylinder {ComponentName}: {slotname}");
                }
            }

            foreach (var (signals, slotName) in listMappings)
            {
                if (signals == null) continue;

                // Save Slot Assignments for parallel creation, initialize with Logic slot
                var slotsToAssign = new List<(Guid, string)>() { (Logic_Cylinder.Guid, slotName) };

                foreach (var signal in signals)
                {
                    FeeSimpleMove moveBit = new FeeSimpleMove();
                    await moveBit.CreateAsync();
                    await moveBit.SendAndWaitAsync();
                    await ContainerObjectProvenance.WriteNewObjectAsync(moveBit, this);

                    await signal.CreateSignalAsync(targetInterface);

                    await ContainerSlotLinkService.AssignVariableAndVerifyAsync(
                        moveBit.Guid,
                        "Output 01",
                        signal.Guid,
                        $"Cylinder {ComponentName}: MoveBit Output 01");

                    // Add current assignment information
                    slotsToAssign.Add((moveBit.Guid, "Input 01"));
                }

                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssign,
                    $"Cylinder {ComponentName}: PLC_IN-Mehrfachbelegung {slotName}");

            }


            // Map parameters
            Services.ApiInstance.Object.SetSlotValue(
                Logic_Cylinder.Guid,
                LogicsStandard.Grob_Cylinder.Slots.HomePos,
                Parameter_HomePos == -1f ? ContainerGeneratedObjectDefaults.MotionHomePosition : Parameter_HomePos);
            Services.ApiInstance.Object.SetSlotValue(
                Logic_Cylinder.Guid,
                LogicsStandard.Grob_Cylinder.Slots.WorkPos,
                Parameter_WorkPos == -1f ? ContainerGeneratedObjectDefaults.MotionWorkPosition : Parameter_WorkPos);
            Services.ApiInstance.Object.SetSlotValue(
                Logic_Cylinder.Guid,
                LogicsStandard.Grob_Cylinder.Slots.OperationTime,
                Parameter_OperationTime == -1f ? ContainerGeneratedObjectDefaults.MotionOperationTime : Parameter_OperationTime);
        }

        async Task ILogicSimObjectOwner.CreateSimObjectsAsync()
        {
            if (!Joints_Cylinder.Any() && IsCreationRequested)
            {
                var joint = new FeeJoint()
                {
                    Name = this.ComponentName,
                    Parent = Logic_Cylinder,
                    JointType = MotionType.Translate,
                    ControlType = MotionSource.Position,
                    Position = new Vector3(0, 0, 0),
                    Scale = ContainerGeneratedObjectDefaults.MotionJointScale,
                };

                await joint.CreateAsync();
                await joint.SendAndWaitAsync();
                Joints_Cylinder.Add(joint);
            }
        }

        async Task ILogicSimObjectOwner.AssignSimObjectsAsync()
        {
            if (Joints_Cylinder.Any())
            {
                bool isActualPositionConnected = false;

                // Lists with slot assignments for later assignment
                var slotsToAssignTarget = new List<(Guid, string)>() { (Logic_Cylinder.Guid, LogicsStandard.Grob_Cylinder.Slots.TargetPosition) };
                var slotsToAssignVelocity = new List<(Guid, string)>() { (Logic_Cylinder.Guid, LogicsStandard.Grob_Cylinder.Slots.Velocity) };

                foreach (var joint in Joints_Cylinder)
                {
                    // Set ControlType to Position
                    Services.ApiInstance.Object.CreateObject(nameof(MotionJoint), joint.Guid);
                    await Services.ApiInstance.Object.SetPropertyAsync(joint.Guid, nameof(JointControllerComponent.MotionSource), MotionSource.Position, "Controller");
                    await Services.ApiInstance.Object.SendAndWait(joint.Guid);

                    if (!isActualPositionConnected)
                    {
                        isActualPositionConnected = await ContainerSlotLinkService.AssignAndVerifyAsync(
                            Logic_Cylinder.Guid,
                            LogicsStandard.Grob_Cylinder.Slots.ActualPosition,
                            joint.Guid,
                            "OutValue",
                            $"Cylinder {ComponentName}: SIM_ActualPosition");
                    }

                    slotsToAssignTarget.Add((joint.Guid, "InTarget"));
                    slotsToAssignVelocity.Add((joint.Guid, "InVelocity"));

                }

                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssignTarget,
                    $"Cylinder {ComponentName}: SIM_TargetPosition");
                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssignVelocity,
                    $"Cylinder {ComponentName}: SIM_Velocity");

            }
        }


    }
}

using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFee
{
    public class LogicSimObjectContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject)
        {
            if (container is not ILogicSimObjectOwner fullContainer)
            {
                throw new InvalidOperationException("Container does not implement Logic and SimObject");
            }

            var existingLogic = ContainerExistingObjectReuse.GetAssignedLogic(container);
            var logic = existingLogic ?? await fullContainer.CreateLogicAsync(parentObject);
            if (existingLogic is null)
                await ContainerObjectProvenance.WriteNewObjectAsync(logic, container);
            await fullContainer.AssignSignalsAsync(targetInterface);
            await container.AssignAdditionalInputFanInsAsync(logic, targetInterface);
            var existingSimObjects = container is ISimObjectFindOrSelect selectable
                ? selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).Select(item => item.Guid).ToHashSet()
                : new HashSet<Guid>();
            await fullContainer.CreateSimObjectsAsync();
            if (container is ISimObjectFindOrSelect createdSelectable)
            {
                foreach (var created in createdSelectable.GetSimObjectTargets()
                             .SelectMany(target => target.GetObjects())
                             .Where(item => !existingSimObjects.Contains(item.Guid)))
                    await ContainerObjectProvenance.WriteNewObjectAsync(created, container);
            }
            await fullContainer.AssignSimObjectsAsync();
        }
    }



    public class LogicContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject)
        {
            if (container is not ILogicOwner logicContainer)
            {
                throw new InvalidOperationException("Container does not implement Logic");
            }

            var existingLogic = ContainerExistingObjectReuse.GetAssignedLogic(container);
            var logic = existingLogic ?? await logicContainer.CreateLogicAsync(parentObject);
            if (existingLogic is null)
                await ContainerObjectProvenance.WriteNewObjectAsync(logic, container);
            await logicContainer.AssignSignalsAsync(targetInterface);
            await container.AssignAdditionalInputFanInsAsync(logic, targetInterface);
        }
    }



    public class SimObjectContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject)
        {
            if (container is not ISimObjectOwner soContainer)
            {
                throw new InvalidOperationException("Container does not implement SimObject");
            }

            // Always same process
            var existing = container is ISimObjectFindOrSelect selectable
                ? selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).Select(item => item.Guid).ToHashSet()
                : [];
            await soContainer.CreateSimObjectsAsync(parentObject);
            if (container is ISimObjectFindOrSelect createdSelectable)
            {
                foreach (var created in createdSelectable.GetSimObjectTargets()
                             .SelectMany(target => target.GetObjects())
                             .Where(item => !existing.Contains(item.Guid)))
                    await ContainerObjectProvenance.WriteNewObjectAsync(created, container);
            }
            await soContainer.AssignSignalsAsync(targetInterface);
        }
    }

    public class CabinetElementContainerFactory : IContainerFactory
    {
        private readonly CabinetContainerManager _cabinetContainerManager;

        public CabinetElementContainerFactory(CabinetContainerManager cabinetContainerManager)
        {
            _cabinetContainerManager = cabinetContainerManager;
        }

        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject)
        {
            if (container is not ICabinetElementOwner cabinetElement)
            {
                throw new InvalidOperationException("Container does not implement CabinetElement");
            }

            var existingElement = ContainerExistingObjectReuse.GetAssignedCabinetElement(container);
            if (existingElement is null)
            {
                // Get or create a cabinet only when the element itself is
                // missing. A reused element must retain its existing parent.
                var cabinet = await _cabinetContainerManager.GetOrCreateCabinetAsync(cabinetElement.CabinetName, parentObject);
                cabinetElement.ElementPosition = _cabinetContainerManager.GetNextPosition(cabinetElement.CabinetName);
                await cabinetElement.CreateSimObjectsAsync(cabinet);
                var createdElement = ContainerExistingObjectReuse.GetAssignedCabinetElement(container);
                await ContainerObjectProvenance.WriteNewObjectAsync(createdElement, container);
            }

            // Assign Signals to CabinetElement
            await cabinetElement.AssignSignalsAsync(targetInterface);
        }
    }
}

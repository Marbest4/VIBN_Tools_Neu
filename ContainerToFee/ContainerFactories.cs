using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFee
{
    public class LogicSimObjectContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (container is not ILogicSimObjectOwner fullContainer)
            {
                throw new InvalidOperationException("Container does not implement Logic and SimObject");
            }

            var existingLogic = ContainerExistingObjectReuse.GetAssignedLogic(container);
            var logic = existingLogic ?? await fullContainer.CreateLogicAsync(parentObject);
            cancellationToken.ThrowIfCancellationRequested();
            if (existingLogic is null)
                await ContainerObjectProvenance.WriteNewObjectAsync(logic, container);
            await fullContainer.AssignSignalsAsync(targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
            await container.AssignAdditionalInputFanInsAsync(logic, targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
            var existingSimObjects = container is ISimObjectFindOrSelect selectable
                ? selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).Select(item => item.Guid).ToHashSet()
                : new HashSet<Guid>();
            await fullContainer.CreateSimObjectsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (container is ISimObjectFindOrSelect createdSelectable)
            {
                foreach (var created in createdSelectable.GetSimObjectTargets()
                             .SelectMany(target => target.GetObjects())
                             .Where(item => !existingSimObjects.Contains(item.Guid)))
                    await ContainerObjectProvenance.WriteNewObjectAsync(created, container);
            }
            await fullContainer.AssignSimObjectsAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }



    public class LogicContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (container is not ILogicOwner logicContainer)
            {
                throw new InvalidOperationException("Container does not implement Logic");
            }

            var existingLogic = ContainerExistingObjectReuse.GetAssignedLogic(container);
            var logic = existingLogic ?? await logicContainer.CreateLogicAsync(parentObject);
            cancellationToken.ThrowIfCancellationRequested();
            if (existingLogic is null)
                await ContainerObjectProvenance.WriteNewObjectAsync(logic, container);
            await logicContainer.AssignSignalsAsync(targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
            await container.AssignAdditionalInputFanInsAsync(logic, targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }



    public class SimObjectContainerFactory : IContainerFactory
    {
        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (container is not ISimObjectOwner soContainer)
            {
                throw new InvalidOperationException("Container does not implement SimObject");
            }

            // Always same process
            var existing = container is ISimObjectFindOrSelect selectable
                ? selectable.GetSimObjectTargets().SelectMany(target => target.GetObjects()).Select(item => item.Guid).ToHashSet()
                : [];
            await soContainer.CreateSimObjectsAsync(parentObject);
            cancellationToken.ThrowIfCancellationRequested();
            if (container is ISimObjectFindOrSelect createdSelectable)
            {
                foreach (var created in createdSelectable.GetSimObjectTargets()
                             .SelectMany(target => target.GetObjects())
                             .Where(item => !existing.Contains(item.Guid)))
                    await ContainerObjectProvenance.WriteNewObjectAsync(created, container);
            }
            await soContainer.AssignSignalsAsync(targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public class CabinetElementContainerFactory : IContainerFactory
    {
        private readonly CabinetContainerManager _cabinetContainerManager;

        public CabinetElementContainerFactory(CabinetContainerManager cabinetContainerManager)
        {
            _cabinetContainerManager = cabinetContainerManager;
        }

        public async Task CreateContainerAsync(ContainerBaseClass container, FeeInterface targetInterface, FeeAbstractObject parentObject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                var createdElement = ContainerExistingObjectReuse.GetAssignedCabinetElement(container);
                await ContainerObjectProvenance.WriteNewObjectAsync(createdElement, container);
            }

            // Assign Signals to CabinetElement
            await cabinetElement.AssignSignalsAsync(targetInterface);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

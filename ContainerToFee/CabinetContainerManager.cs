using System.Numerics;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee
{
    public class CabinetContainerManager
    {
        private readonly Dictionary<string, FeeAbstractObject> _cabinets = new Dictionary<string, FeeAbstractObject>();
        private readonly Dictionary<string, Vector2> _elementPositions = new Dictionary<string, Vector2>();


        public async Task<FeeAbstractObject> GetOrCreateCabinetAsync(
            string cabinetType,
            FeeAbstractObject parentObject,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cabinets.TryGetValue(cabinetType, out var cachedCabinet))
                return cachedCabinet;

            var guidStrings = (await Services.ApiInstance.Object.GetSceneObjectGuidsOfTypeAsync("Cabinet"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            var names = guidStrings.Length == 0
                ? Array.Empty<string>()
                : (await Services.ApiInstance.Object.GetPropertiesAsync(guidStrings, "Name"))
                    .Select(Services.ApiInstance.XmlHelper.ConvertToString)
                    .ToArray();
            var matches = guidStrings
                .Select((guid, index) => new
                {
                    Guid = Guid.Parse(guid),
                    Name = names[index],
                })
                .Where(item => string.Equals(item.Name, cabinetType, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Cabinet '{cabinetType}' ist {matches.Length}-mal vorhanden. Es wird kein weiteres " +
                    "Cabinet erzeugt. Bitte den gewünschten Bestand eindeutig benennen oder bereinigen.");
            }

            FeeAbstractObject cabinet;
            if (matches.Length == 1)
            {
                cabinet = new FeeCabinet
                {
                    Name = cabinetType,
                    Guid = matches[0].Guid,
                };
                _cabinets[cabinetType] = cabinet;
            }
            else
            {
                cabinet = new FeeCabinet { Name = cabinetType, Parent = parentObject };
                await cabinet.CreateAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await cabinet.SendAndWaitAsync();
                cancellationToken.ThrowIfCancellationRequested();
                _cabinets[cabinetType] = cabinet;
            }

            _elementPositions.TryAdd(cabinetType, new Vector2(-20f, 100f));

            return cabinet;
        }



        public Vector2 GetNextPosition(string cabinetType)
        {
            Vector2 currentPosition = _elementPositions[cabinetType];
            Vector2 nextPosition;

            if (currentPosition.X >= 1420)
            {
                nextPosition = new Vector2(100, currentPosition.Y + 200);
            }
            else
            {
                nextPosition = new Vector2(currentPosition.X + 120, currentPosition.Y);
            }

            _elementPositions[cabinetType] = nextPosition;
            return nextPosition;
        }
    }
}

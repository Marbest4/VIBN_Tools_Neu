using System.Numerics;
using System.Xml.Linq;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;

namespace VIBN_Tools.ContainerToFee
{
    public class CabinetContainerManager
    {
        private readonly Dictionary<string, FeeAbstractObject> _cabinets = new Dictionary<string, FeeAbstractObject>();
        private long _connectionRevision = -1;
        private readonly Dictionary<string, Vector2> _elementPositions = new Dictionary<string, Vector2>();


        public async Task<FeeAbstractObject> GetOrCreateCabinetAsync(
            string cabinetType,
            FeeAbstractObject parentObject,
            CancellationToken cancellationToken = default, Guid? preferredGuid = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = Services.Connection.ConnectionRevision;
            if (_connectionRevision != revision) { _cabinets.Clear(); _elementPositions.Clear(); _connectionRevision = revision; }
            var key = $"{parentObject.Guid:D}:{preferredGuid}:{cabinetType}";
            if (_cabinets.TryGetValue(key, out var cachedCabinet)) return cachedCabinet;

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
                    Name = index < names.Length ? names[index] : "",
                })
                .ToArray();
            var preferred = preferredGuid is { } id ? matches.Where(item => item.Guid == id).ToArray() : [];
            if (preferred.Length == 1) matches = preferred;
            else
            {
                // A name in another BasicFrame is not this cabinet. The live
                // parent's Children list is authoritative when a saved GUID vanished.
                var xml = (await Services.ApiInstance.Object.GetSceneObjectsAsXmlAsync([parentObject.GuidString]) ?? []).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (xml.Length != 1) throw new InvalidOperationException("Cabinet-Parent konnte nicht eindeutig gelesen werden.");
                var children = XElement.Parse(xml[0]).Element("Children")?.Elements().Select(item => item.Attribute("Guid")?.Value)
                    .Where(value => value is not null).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                matches = matches.Where(item => string.Equals(item.Name, cabinetType, StringComparison.OrdinalIgnoreCase) && children.Contains(item.Guid.ToString("D"))).ToArray();
            }
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
                    Name = matches[0].Name,
                    Guid = matches[0].Guid,
                };
                _cabinets[key] = cabinet;
            }
            else
            {
                cabinet = new FeeCabinet { Name = cabinetType, Parent = parentObject };
                await cabinet.CreateAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await cabinet.SendAndWaitAsync();
                cancellationToken.ThrowIfCancellationRequested();
                _cabinets[key] = cabinet;
            }

            _elementPositions.TryAdd(cabinet.GuidString, new Vector2(-20f, 100f));

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

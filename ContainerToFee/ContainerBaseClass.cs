using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.GlobalClasses.FeeObjects;
using VIBN_Tools.ContainerGeneration.Models;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.ContainerToFee
{
    public abstract class ContainerBaseClass
    {
        private readonly Dictionary<string, List<FeeInterfaceSignal>> _signalsBySlot =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FeeInterfaceSignal>> _additionalInputFanIns =
            new(StringComparer.OrdinalIgnoreCase);

        public string ComponentName { get; set; }
        public Dictionary<string, PropertyInfo> SlotAssignment { get; set; }

        /// <summary>Stable visual-plan identity written to newly created primary FEE objects.</summary>
        public string GenerationProvenanceId { get; set; } = string.Empty;

        /// <summary>Container XML type written with per-object provenance.</summary>
        public string GenerationContainerType { get; set; } = string.Empty;






        public void StoreContainerInformation(XElement containerElement, string componentName)
        {
            ComponentName = componentName;

            _signalsBySlot.Clear();
            _additionalInputFanIns.Clear();
            var slotLookup = SlotAssignment
                .Where(item => item.Value is not null)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var entriesGrouped = containerElement.Descendants("Entry")
                .GroupBy(x => x.Element("Slot")?.Value?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var group in entriesGrouped)
            {
                var slotName = group.Key;
                if (string.IsNullOrWhiteSpace(slotName))
                    throw new InvalidDataException($"Container '{ComponentName}' enthält einen Eintrag ohne Slot.");

                var entries = group.ToArray();
                var duplicateError = ContainerSlotMultiplicityPolicy.GetDuplicateError(
                    slotName,
                    entries.Length);
                if (duplicateError is not null)
                    throw new InvalidDataException($"Container '{ComponentName}': {duplicateError}");

                if (!slotLookup.TryGetValue(slotName, out var property) || property is null)
                {
                    throw new InvalidDataException(
                        $"Slot '{slotName}' existiert nicht im Container '{ComponentName}'.");
                }

                var signals = entries.Select(CreateSignal).ToList();
                _signalsBySlot[slotName] = signals;

                if (property.PropertyType == typeof(FeeInterfaceSignal))
                {
                    if (signals.Count == 1)
                        property.SetValue(this, signals[0]);
                    else
                        _additionalInputFanIns[slotName] = signals;
                }
                else if (property.PropertyType == typeof(List<FeeInterfaceSignal>))
                {
                    property.SetValue(this, signals);
                }
                else
                {
                    throw new InvalidDataException(
                        $"Slot '{slotName}' in Container '{ComponentName}' besitzt den nicht unterstützten " +
                        $"Zieltyp '{property.PropertyType.Name}'.");
                }
            }
        }

        private static FeeInterfaceSignal CreateSignal(XElement entry)
        {
            var address = entry.Element("Address")?.Value ?? string.Empty;
            var signal = new FeeInterfaceSignal
            {
                Tag = entry.Element("Signal")?.Value,
                Path = address.Contains("GVL_IO", StringComparison.OrdinalIgnoreCase)
                    ? address
                    : string.Empty,
                Address = !address.Contains("GVL_IO", StringComparison.OrdinalIgnoreCase)
                    ? address
                    : string.Empty,
                Comment = entry.Element("ID")?.Value,
                IOTypeString = entry.Element("DataType")?.Value
            };
            signal.SetIoMode();
            return signal;
        }

        public IEnumerable<FeeInterfaceSignal> EnumerateAssignedSignals() =>
            _signalsBySlot.Values.SelectMany(signals => signals);

        /// <summary>
        /// Reports the actual parsed slot content even when a scalar signal
        /// property intentionally remains empty because a PLC_IN slot is
        /// represented by multiple MoveBit fan-in branches.
        /// </summary>
        public bool HasAssignedSignalsForProperty(string propertyName)
        {
            var slots = SlotAssignment
                .Where(item => string.Equals(item.Value?.Name, propertyName, StringComparison.Ordinal))
                .Select(item => item.Key);
            return slots.Any(slot =>
                _signalsBySlot.TryGetValue(slot, out var signals) && signals.Count > 0);
        }

        public IReadOnlyList<PlcInputFanInAssignment> GetAdditionalInputFanIns() =>
            _additionalInputFanIns
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new PlcInputFanInAssignment(item.Key, item.Value.AsReadOnly()))
                .ToArray();

        internal async Task AssignAdditionalInputFanInsAsync(
            FeeLogic logic,
            FeeInterface targetInterface)
        {
            foreach (var fanIn in GetAdditionalInputFanIns())
            {
                var slotsToAssign = new List<(Guid ObjectGuid, string SlotName)>
                {
                    (logic.Guid, fanIn.SlotName)
                };

                foreach (var signal in fanIn.Signals)
                {
                    var move = new FeeSimpleMove();
                    await move.CreateAsync();
                    await move.SendAndWaitAsync();
                    await ContainerObjectProvenance.WriteNewObjectAsync(move, this);
                    await signal.CreateSignalAsync(targetInterface);
                    await ContainerSlotLinkService.AssignVariableAndVerifyAsync(
                        move.Guid,
                        "Output 01",
                        signal.Guid,
                        $"{ComponentName}: MoveBit Output 01");
                    slotsToAssign.Add((move.Guid, "Input 01"));
                }

                await ContainerSlotLinkService.AssignAndVerifyAsync(
                    slotsToAssign,
                    $"{ComponentName}: PLC_IN-Mehrfachbelegung {fanIn.SlotName}");
            }
        }



        protected List<T> FindSimObjectsByNameAndType<T>(ObservableCollection<FeeAbstractObject> mappableSimObjects) where T : FeeAbstractObject, new()
        {
            // Get expected type name from Dictionary
            if (!FeeAbstractObject.TypeToNameMap.TryGetValue(typeof(T), out var expectedTypeName))
            {
                return new List<T>();
            }

            var matches = mappableSimObjects
                                .Where(obj => 
                                    string.Equals(obj.Name, this.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                                    obj.FeeType == expectedTypeName)
                                .OfType<T>()
                                .ToList();

            foreach (var match in matches)
            {
                if (match is IAssignableSimObject assignableSimObject)
                {
                    assignableSimObject.AssignedContainer = (ISimObjectFindOrSelect)this;
                }
            }

            return matches;

        }





        public int CountNonNullSignals()
        {
            if (_signalsBySlot.Count > 0)
                return _signalsBySlot.Values.Sum(signals => signals.Count);

            var singleSignals = this.GetType()
                                    .GetProperties()
                                    .Where(p => p.PropertyType == typeof(FeeInterfaceSignal))
                                    .Select(s => s.GetValue(this))
                                    .Count(v => v != null);
            var listSignals = this.GetType()
                                  .GetProperties()
                                  .Where(p => p.PropertyType == typeof(List<FeeInterfaceSignal>))
                                  .Select(s => s.GetValue(this) as List<FeeInterfaceSignal>)
                                  .Where(list => list != null)
                                  .Sum(list => list.Count(v => v != null));

            return singleSignals + listSignals;
        }

    }

    public sealed record PlcInputFanInAssignment(
        string SlotName,
        IReadOnlyList<FeeInterfaceSignal> Signals);



    public class SimObjectTarget
    {
        public string DisplayName { get; set; }

        public Type AllowedType { get; set; }

        public bool AllowMultiSelect { get; set; }

        public string DisplayNameWithSelection => $"{DisplayName} ({(AllowMultiSelect ? "Multi Select" : "Single Select")})";

        public Action<IEnumerable<FeeAbstractObject>> AssignObjects { get; set; }

        public Func<IEnumerable<FeeAbstractObject>> GetObjects { get; set; }
    }



}

using System.Windows;
using FS.API;
using FS.SDK.Extensibility.Contracts;
using FS.SDK.Io;
using VIBN_Tools.ModelValidation;
using VIBN_Tools.Core.Interfaces;
using static VIBN_Tools.GlobalClasses.Interfaces;

namespace VIBN_Tools.GlobalClasses.FeeObjects
{
    public class FeeInterfaceSignal : FeeAbstractObject, IPlausibilityCheck
    {

        //===================================================================================================================
        // C L A S S   S P E C I F I C   P R O P E R T I E S
        //===================================================================================================================

        public new string Name => Tag;


        private string _tag;
        public string Tag
        {
            get => _tag;
            set => SetPropertyChange(ref _tag, value);
        }

        private string _address;
        public string Address
        {
            get => _address;
            set => SetPropertyChange(ref _address, value);
        }


        private string _path;
        public string Path
        {
            get => _path;
            set => SetPropertyChange(ref _path, value);
        }

        private string _comment;
        public string Comment
        {
            get => _comment;
            set => SetPropertyChange(ref _comment, value);
        }


        public string Value { get; set; }
        public int References { get; set; }


        private IOType _ioType;
        public IOType IOType
        {
            get { return _ioType; }
            set
            {
                _ioType = value;
                _ioTypeString = value.ToString();
            }
        }

        private string _ioTypeString;
        public string IOTypeString
        {
            get { return _ioTypeString; }
            set
            {
                _ioTypeString = value;
                _ioType = ParseIOType(value);
            }
        }

        private IOMode _usage;
        public IOMode Usage
        {
            get { return _usage; }
            set
            {
                _usage = value;
                _usageString = value.ToString();
            }
        }

        private string _usageString;
        public string UsageString
        {
            get { return _usageString; }
            set
            {
                _usageString = value;
                _usage = (value == "Read") ? IOMode.Read : IOMode.Write;
            }
        }

        public FeeInterface ParentInterface { get; set; }

        /// <summary>
        /// Set only by the visual Container2FEE executor after an unambiguous
        /// existing signal was resolved. The subsequent legacy assignment then
        /// reuses its GUID without updating or overwriting the variable.
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public bool ReuseExistingWithoutUpdate { get; set; }








        //===================================================================================================================
        // C O N S T R U C T O R S
        //===================================================================================================================

        public FeeInterfaceSignal()
        {
            // Default Constructor
            Guid = Guid.NewGuid();
        }

        // Constructor for Interface-Signal
        public FeeInterfaceSignal(string tag, string address, string usage, string type, string comment = "")
        {
            Tag = tag;
            Address = address;
            UsageString = usage;
            IOTypeString = type;
            Comment = comment;
            Guid = Guid.NewGuid();
        }

        // Constructor for Symbolic-Signal
        public FeeInterfaceSignal(string tag, string type, string value, string comment)
        {
            Tag = tag;
            IOTypeString = type;
            Value = value;
            Comment = comment;
            Guid = Guid.NewGuid();
        }



        //===================================================================================================================
        // M E T H O D S
        //===================================================================================================================

        public async Task<bool> CreateSignalAsync(FeeInterface? targetInterface = null)
        {
            var tempInterface = targetInterface ?? ParentInterface;

            if (tempInterface == null)
                throw new InvalidOperationException("No FeeInterface provided!");

            if (ReuseExistingWithoutUpdate)
            {
                if (Guid == Guid.Empty)
                    throw new InvalidOperationException("Existing interface signal has no valid GUID.");
                // Keep the interface in which the variable was resolved. The
                // method parameter is only the create target for missing
                // signals and must not rewrite existing provenance.
                ParentInterface ??= tempInterface;
                return true;
            }

            if (!await Services.ApiInstance.Interface.UpdateOrCreateVariableAsync(new ApiInterfaceVariableDefinition
            {
                InterfacePluginProvider = tempInterface.ProviderGuid,
                InterfaceGuid = tempInterface.Guid,
                VariableGuid = Guid,
                Tag = Tag,
                InterfaceName = Tag,
                Address = Address,
                Path = Path,
                Type = IOType,
                Comment = Comment,
                Usage = Usage,
            }))
                return false;

            return true;
        }



        public static IOType ParseIOType(string input)
        {
            if (Enum.TryParse<IOType>(input, ignoreCase: true, out var result))
            {
                return result;
            }
            return default;

        }

        public void SetIoMode()
        {
            Usage = Address.StartsWith("A") || Address.StartsWith("PA") || Path.Contains(".Og") ? IOMode.Read : IOMode.Write;
        }


        public async Task CheckObjectIssuesAsync(IEnumerable<FeeAbstractObject> newObjects)
        {
            if (Address != String.Empty && Path != String.Empty)
                PlausibilityIssues.Add(new PlausibilityIssue($"Sowohl Adresse als auch Pfad vorhanden", Severity.Error));

            if (Address == String.Empty && Path == String.Empty)
                PlausibilityIssues.Add(new PlausibilityIssue($"Weder Adresse noch Pfad vorhanden", Severity.Error));

            if (Tag == String.Empty)
                PlausibilityIssues.Add(new PlausibilityIssue($"Kein Tag vergeben", Severity.Warning));

            if (Usage != IOMode.Read && Usage != IOMode.Write && ParentInterface?.ProviderGuid != PluginGuids.Symbolicinterface)
                PlausibilityIssues.Add(new PlausibilityIssue($"Read/Write nicht gesetzt", Severity.Error));

            if (References == 0)
                PlausibilityIssues.Add(new PlausibilityIssue($"Signal nicht verknüpft", Severity.Error));

            SiemensSignalAddress.TryParse(Address, IOTypeString, out var myAddress, out var addressWarning);
            if (addressWarning is not null)
                PlausibilityIssues.Add(new PlausibilityIssue(addressWarning, Severity.Warning));
            if (ParentInterface != null && myAddress is not null)
            {
                foreach (var other in ParentInterface.Signals ?? [])
                {
                    if (other == this) continue;
                    if (string.IsNullOrEmpty(Address)) continue;
                    if (string.IsNullOrEmpty(other.Address)) continue;

                    SiemensSignalAddress.TryParse(other.Address, other.IOTypeString, out var otherAddress, out _);
                    if (otherAddress is not null && myAddress.Overlaps(otherAddress))
                    {
                        PlausibilityIssues.Add(new PlausibilityIssue($"Adresskonflikt mit Signal '{other.Tag}'", Severity.Error));
                    }
                }
            }

        }







        //===================================================================================================================
        // A D D I T I O N A L S :   C O N S T A N T S ,   D E F I N E S ,   E T C .
        //===================================================================================================================


    }



    public record SignalDefinition(string Name, double Offset, IOMode Mode, IOType Type, string Comment = "");



}

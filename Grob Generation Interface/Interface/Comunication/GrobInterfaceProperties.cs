using FS.SDK;
using FS.SDK.API.Attributes;
using FS.SDK.Extensibility.Interfaces.Encoding;

namespace GrobGenerationInterface.Interface.Comunication
{
    internal class GrobInterfaceProperties : InterfaceProperties<GrobInterfaceProperties>
    {
        //[Persistent]
        //[UIProperty(RegExString = Constants.IPAddressRegex)]
        //public string IpAddress { get; set; } = "127.0.0.1";

        //[Persistent]
        //[UIProperty]
        //public int ConnectionPort { get; set; }

        /// <summary>
        ///     Gets a bool indicating if this interface can store its values.
        /// </summary>
        public override bool IsValueStorable { get; } = false;
    }
}
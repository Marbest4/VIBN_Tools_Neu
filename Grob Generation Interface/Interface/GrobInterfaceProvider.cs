using System;
using System.Collections.Generic;
using System.Linq;
using FS.SDK.Extensibility.Interfaces;
using FS.SDK.Extensibility.Interfaces.Encoding;
using FS.SDK.Io;
using GrobGenerationInterface.Interface.Comunication;
using GrobGenerationInterface.Localization;

namespace GrobGenerationInterface.Interface
{
    public class GrobInterfaceProvider : InterfaceProvider
    {
        /// <summary>
        ///     Gets the friendly name of the plugin to display.
        /// </summary>
        public override string PluginDisplayName => "Grob Generation Interface";

        public override IEnumerable<IOType> SupportedTypes { get; } = Enum.GetValues(typeof(IOType)).Cast<IOType>();

        public override Guid PluginGuid { get; } = new Guid("a6222164-be37-49de-b760-9b1c97c320bb");

        public override string LocalizedName => "Grob Generation Interface";

        /// <summary>
        ///     Gets a view model for the interface.
        /// </summary>
        /// <param name="id">   The identifier. </param>
        /// <returns>
        ///     The view model.
        /// </returns>
        protected override InterfaceVM VMInstanceRequested(Guid id) => new GrobInterfaceVM(id);

        /// <summary>
        /// Network controller instance requested.
        /// </summary>
        ///
        /// <param name="id">   (Optional) The identifier. </param>
        ///
        /// <returns>
        /// A NetworkInterfaceController.
        /// </returns>
        protected override InterfaceController ControllerInstanceRequested(Guid? id = null) => new GrobInterfaceController(id);

        /// <summary>
        /// Gets network interface properties.
        /// </summary>
        ///
        /// <returns>
        /// The network interface properties.
        /// </returns>
        public override InterfaceProperties GetInterfaceProperties() => new GrobInterfaceProperties();
    }
}
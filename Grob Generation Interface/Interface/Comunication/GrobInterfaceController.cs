using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using FS.SDK;
using FS.SDK.Commands;
using FS.SDK.Extensibility.Interfaces;
using FS.SDK.Extensions;
using FS.SDK.Io;
using FS.SDK.Localization.Buttons;
using FS.SDK.Util;

namespace GrobGenerationInterface.Interface.Comunication
{
    /// <summary>
    /// Funtion is needed for proper usage of the Interface plugin but is not needed for Grob Generation Interface
    /// </summary>
    internal class GrobInterfaceController : InterfaceController
    {
        public new GrobInterfaceProperties Properties => (GrobInterfaceProperties)base.Properties;

        /// <summary>
        /// Gets variable properties.
        /// </summary>
        ///
        /// <returns>
        /// The variable properties.
        /// </returns>
        public override IOVariableProperties GetVariableProperties() => new VariableProperties();

        public override void Connect()
        {
        }

        public override void Disconnect()
        {
        }


        public GrobInterfaceController(Guid? guid) : base(guid)
        {
        }
    }

}
using System;
using System.Collections.Generic;
using System.Linq;
using FS.SDK;
using FS.SDK.API.Attributes;
using FS.SDK.Extensibility;
using FS.SDK.Extensibility.Interfaces;
using FS.SDK.Io;
using FS.SDK.Util;
using FS.SDK.PropertyDelegates;
using FS.SDK.Uid;
using FS.SDK.Commands;

namespace GrobGenerationInterface.Interface.Comunication
{
    [UIMetadata]
    internal class GrobInterfaceVM : InterfaceVM
    {
        
        /// <summary>
        /// Gets variable properties.
        /// </summary>
        ///
        /// <returns>
        /// The variable properties.
        /// </returns>
        public override IOVariableProperties GetVariableProperties() => new VariableProperties();

        public GrobInterfaceVM(Guid interfaceId) : base(interfaceId)
        {
            // Importing Commands for buttons on the top right side
            //ImportingCommands = new List<ParameterlessCommand>()
            //{
            //    new ParameterlessCommand(() => ImportClass.ImportExcelFile(), commandNameGetter: () => "Import from file")
            //};
        }

    }



    public class ImportClass
    {
        public static void ImportExcelFile()
        {

        }
    }
}
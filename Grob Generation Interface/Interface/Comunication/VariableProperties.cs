using FS.SDK.Io;

namespace GrobGenerationInterface.Interface.Comunication
{
    internal class VariableProperties : IOVariableProperties
    {
        /// <summary>
        ///     Gets a value indicating whether this object properties are valid.
        /// </summary>
        public override bool IsValid => !string.IsNullOrWhiteSpace(Tag);

        /// <summary>
        ///     Initializes a new instance of the FS.SDK.Io.IOVariableProperties&lt;T&gt; class.
        /// </summary>
        public VariableProperties() : base(new[] {IOVariableProperty.Tag, IOVariableProperty.Address, IOVariableProperty.CreateProperty("Path", ""), IOVariableProperty.Type, IOVariableProperty.Comment, IOVariableProperty.Usage, IOVariableProperty.Cycle}) { }
    }
}
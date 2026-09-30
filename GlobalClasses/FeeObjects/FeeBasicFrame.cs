using FS.SDK.Scene.Objects;

namespace VIBN_Tools.GlobalClasses.FeeObjects
{
    public class FeeBasicFrame : FeeAbstractObject
    {
        /// <summary>
        /// Persistent namespaced metadata written to the SDK TagComponent.
        /// Existing marks and object names are intentionally not repurposed.
        /// </summary>
        public IReadOnlyDictionary<string, string> PersistentTags { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Non-empty when FEE created the frame but did not expose the written
        /// TagComponent values for confirmation. Generation remains usable;
        /// only provenance-based reverse discovery is potentially incomplete.
        /// </summary>
        public string PersistentTagWarning { get; private set; } = string.Empty;

        //===================================================================================================================
        // C L A S S   S P E C I F I C   P R O P E R T I E S
        //===================================================================================================================




        //===================================================================================================================
        // C O N S T R U C T O R S
        //===================================================================================================================

        public FeeBasicFrame()
        {
            Guid = Guid.NewGuid();
            FeeType = nameof(BasicFrame);
            Visible = false;
        }



        //===================================================================================================================
        // M E T H O D S
        //===================================================================================================================

        public override async Task<bool> CreateAsync()
        {
            await base.CreateAsync();

            if (PersistentTags.Count > 0)
            {
                var result = await FeeTagPropertyStore.TryWriteAndVerifyAsync(
                    Guid,
                    PersistentTags,
                    preserveExisting: true,
                    verifyAfterWrite: false);
                PersistentTagWarning = result.Warning;
            }

            return true;
        }

        public override async Task<bool> SendAndWaitAsync()
        {
            var result = await base.SendAndWaitAsync();
            if (result && PersistentTags.Count > 0)
            {
                // Retry the write after the object was sent. Some FEE builds
                // expose TagComponent only after the first object update.
                var tagResult = await FeeTagPropertyStore.TryWriteAndVerifyAsync(
                    Guid,
                    PersistentTags,
                    preserveExisting: true,
                    verifyAfterWrite: true);
                PersistentTagWarning = tagResult.Warning;
            }
            return result;
        }
    }
}

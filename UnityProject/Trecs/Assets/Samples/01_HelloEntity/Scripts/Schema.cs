using Unity.Mathematics;

namespace Trecs.Samples.HelloEntity
{
    public static class SampleTags
    {
        public struct Spinner : ITag { }
    }

    public static partial class SampleTemplates
    {
        public partial class SpinnerEntity : ITemplate, IHasTags<SampleTags.Spinner>
        {
            public Rotation Rotation = new(quaternion.identity);

            // We can't directly reference game objects from components
            // so instead we have an ID that maps to one instead
            // Components must contain pure unmanaged data
            // This is important so that we can easily serialize all
            // state
            public GameObjectId GameObjectId;
        }
    }
}

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

            public GameObjectId GameObjectId;
        }
    }
}

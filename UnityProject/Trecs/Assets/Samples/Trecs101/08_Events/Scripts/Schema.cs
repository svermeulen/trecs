namespace Trecs.Samples.Events
{
    public static class CubeTags
    {
        public struct Cube : ITag { }

        public struct Growing : ITag { }

        public struct Shrinking : ITag { }
    }

    public static partial class SampleTemplates
    {
        /// <summary>
        /// A cube that transitions between Growing and Shrinking states.
        /// Used to demonstrate entity lifecycle events.
        /// </summary>
        public partial class CubeEntity
            : ITemplate,
                IHasTags<CubeTags.Cube>,
                IHasState<CubeTags.Growing>,
                IHasState<CubeTags.Shrinking>
        {
            public Position Position;
            public UniformScale Scale = new(1f);
            public GameObjectId GameObjectId;
        }
    }
}

namespace Trecs.Samples
{
    public static partial class CommonTemplates
    {
        public partial class Renderable : ITemplate, IHasTags<CommonTags.Renderable>
        {
            public Position Position;
            public Rotation Rotation;
            public UniformScale Scale;
            public ColorComponent Color = new(UnityEngine.Color.white);
        }
    }
}

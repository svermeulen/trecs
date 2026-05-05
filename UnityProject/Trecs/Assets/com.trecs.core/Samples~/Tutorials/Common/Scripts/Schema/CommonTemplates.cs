namespace Trecs.Samples
{
    public static partial class CommonTemplates
    {
        public partial class Renderable : ITemplate, IHasTags<CommonTags.Renderable>
        {
            Position Position;
            Rotation Rotation;
            UniformScale Scale;
            ColorComponent Color = new(UnityEngine.Color.white);
        }
    }
}

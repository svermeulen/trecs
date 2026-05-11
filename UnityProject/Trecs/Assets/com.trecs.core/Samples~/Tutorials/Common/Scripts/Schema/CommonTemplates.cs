namespace Trecs.Samples
{
    public static partial class CommonTemplates
    {
        // `abstract` because every concrete template that needs visuals declares
        // `IExtends<Renderable>` — this base is never registered directly.
        // Trying to do so produces TRECS039 at the call site and a runtime throw.
        public abstract partial class Renderable : ITemplate, ITagged<CommonTags.Renderable>
        {
            Position Position;
            Rotation Rotation;
            UniformScale Scale;
            ColorComponent Color = new(UnityEngine.Color.white);
        }
    }
}

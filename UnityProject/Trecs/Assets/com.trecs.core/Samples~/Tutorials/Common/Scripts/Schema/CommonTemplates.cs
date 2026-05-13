namespace Trecs.Samples
{
    public static partial class CommonTemplates
    {
        // `abstract` because every concrete template that needs visuals declares
        // `IExtends<IndirectRenderable>` — this base is never registered directly.
        // Trying to do so produces TRECS039 at the call site and a runtime throw.
        public abstract partial class IndirectRenderable
            : ITemplate,
                ITagged<CommonTags.IndirectRenderable>
        {
            Position Position;
            Rotation Rotation;
            UniformScale Scale;
            ColorComponent Color = new(UnityEngine.Color.white);
        }

        // GameObject-per-entity binding for samples that use
        // `RenderableGameObjectManager`. Concrete templates set `PrefabId` to
        // the constant matching the factory they registered; the manager fills
        // `GameObjectId` reactively on entity add.
        public abstract partial class RenderableGameObject : ITemplate
        {
            PrefabId PrefabId;
            GameObjectId GameObjectId = default;
        }
    }
}

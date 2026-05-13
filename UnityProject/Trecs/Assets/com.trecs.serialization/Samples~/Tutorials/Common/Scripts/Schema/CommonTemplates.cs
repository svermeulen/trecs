namespace Trecs.Serialization.Samples
{
    public static partial class CommonTemplates
    {
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

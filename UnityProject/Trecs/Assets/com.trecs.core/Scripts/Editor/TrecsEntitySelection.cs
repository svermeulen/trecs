using System;

namespace Trecs
{
    /// <summary>
    /// ScriptableObject proxy used as <see cref="UnityEditor.Selection.activeObject"/>
    /// when the user picks an entity in <see cref="TrecsHierarchyWindow"/>.
    /// Holds the stable <see cref="EntityHandle"/> (not <see cref="EntityIndex"/>,
    /// which shifts on structural change) plus a weak ref to the owning
    /// <see cref="World"/>. The actual component values bound by the inspector
    /// live on a per-Editor <see cref="TrecsEntityInspectorBuffer"/> — keeping
    /// them off the proxy isolates Unity's PropertyHandlerCache to the buffer
    /// (which the Editor owns and destroys on teardown) so cached
    /// SerializedProperty paths can't outlive the layout that created them.
    /// </summary>
    public sealed class TrecsEntitySelection : TrecsSelectionProxy
    {
        [NonSerialized]
        public EntityHandle Handle;

        public void Set(World world, EntityHandle handle)
        {
            SetWorld(world);
            Handle = handle;
            name = $"Entity id:{handle.UniqueId} v:{handle.Version}";
        }
    }
}

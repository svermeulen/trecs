using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Optional recorder for tracking which components and structural changes
    /// each system performs at runtime. Useful for inspector GUIs and
    /// debugging. Set on World via <see cref="World.SetAccessRecorder"/> to
    /// enable recording. Implementations should deduplicate internally if
    /// needed — the component callback fires on every access.
    /// </summary>
    public interface IAccessRecorder
    {
        void OnComponentAccess(
            string systemName,
            GroupIndex group,
            ComponentId componentType,
            bool isReadOnly
        );

        void OnEntityAdded(string systemName, GroupIndex group);

        void OnEntityRemoved(string systemName, GroupIndex group);

        void OnEntityMoved(string systemName, GroupIndex fromGroup, GroupIndex toGroup);
    }
}

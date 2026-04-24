using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Optional recorder for tracking which components each system accesses at runtime.
    /// Useful for inspector GUIs and debugging. Set on World to enable recording.
    /// Implementations should deduplicate internally if needed — this fires on every access.
    /// </summary>
    public interface IComponentAccessRecorder
    {
        void OnComponentAccess(
            string systemName,
            GroupIndex group,
            ComponentId componentType,
            bool isReadOnly
        );
    }
}

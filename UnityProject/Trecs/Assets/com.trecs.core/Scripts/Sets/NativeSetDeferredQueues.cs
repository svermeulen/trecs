using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Lightweight Burst-compatible handle for a single set's deferred add/remove queues.
    /// Used by <see cref="NativeWorldAccessor"/> for <see cref="NativeWorldAccessor.SetAdd{TSet}"/>
    /// and <see cref="NativeWorldAccessor.SetRemove{TSet}"/>.
    /// </summary>
    internal readonly struct NativeSetDeferredQueues
    {
        internal readonly AtomicNativeBags AddQueue;
        internal readonly AtomicNativeBags RemoveQueue;

        internal NativeSetDeferredQueues(AtomicNativeBags addQueue, AtomicNativeBags removeQueue)
        {
            AddQueue = addQueue;
            RemoveQueue = removeQueue;
        }
    }
}

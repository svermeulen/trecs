using System.Collections.Generic;
using System.ComponentModel;

namespace Trecs
{
    /// <summary>
    /// Delegate invoked over the range of just-removed entities in a single
    /// group during submission, for one <see cref="CascadeRemoveAttribute"/> or
    /// <see cref="DisposeOnRemoveAttribute"/> field. The removed entities still
    /// occupy the tail slots <c>[range.Start, range.End)</c> of the group's
    /// component buffers, so <c>world.ComponentBuffer&lt;TComponent&gt;(group).Read</c>
    /// can read their pre-removal field values.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate void RemovalFieldHandler(
        WorldAccessor world,
        GroupIndex group,
        EntityRange range
    );

    /// <summary>
    /// Implemented by generated partial component structs that carry at least
    /// one <see cref="CascadeRemoveAttribute"/> or
    /// <see cref="DisposeOnRemoveAttribute"/> field. The framework dispatches to
    /// this during world build to collect the per-field removal handlers for
    /// each group the component appears in.
    /// </summary>
    /// <remarks>
    /// Generated code implements this explicitly; users never call it. The
    /// dispatch site (<c>ResolvedComponentDeclaration&lt;T&gt;</c>) tests
    /// <c>default(T) is IComponentRemovalHandlers</c>, so non-annotated
    /// components pay nothing.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IComponentRemovalHandlers
    {
        void RegisterRemovalHandlers(RemovalHandlerCollector collector);
    }

    /// <summary>
    /// Sink passed to <see cref="IComponentRemovalHandlers.RegisterRemovalHandlers"/>
    /// during world build. Generated code adds one handler per annotated field:
    /// cascade-read handlers run in the removal-read phase (alongside user
    /// <c>OnRemoved</c>), dispose handlers run strictly afterward in the dispose
    /// phase.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RemovalHandlerCollector
    {
        readonly List<RemovalFieldHandler> _readHandlers = new();
        readonly List<RemovalFieldHandler> _disposeHandlers = new();

        /// <summary>
        /// Register a handler that reads referenced entities and queues their
        /// removal (the <see cref="CascadeRemoveAttribute"/> action). Runs in
        /// the read phase, before any dispose handler.
        /// </summary>
        public void AddCascadeReadHandler(RemovalFieldHandler handler) =>
            _readHandlers.Add(handler);

        /// <summary>
        /// Register a handler that disposes a heap-backed field (the
        /// <see cref="DisposeOnRemoveAttribute"/> action). Runs in the dispose
        /// phase, after every read handler and user <c>OnRemoved</c> callback.
        /// </summary>
        public void AddDisposeHandler(RemovalFieldHandler handler) => _disposeHandlers.Add(handler);

        internal int ReadHandlerCount => _readHandlers.Count;
        internal int DisposeHandlerCount => _disposeHandlers.Count;
        internal bool IsEmpty => _readHandlers.Count == 0 && _disposeHandlers.Count == 0;

        internal RemovalFieldHandler[] CopyReadHandlers() => _readHandlers.ToArray();

        internal RemovalFieldHandler[] CopyDisposeHandlers() => _disposeHandlers.ToArray();

        /// <summary>
        /// Reset so a single collector instance can be reused across groups
        /// during the world-build precompute (each group copies out its handler
        /// arrays before the next group is collected).
        /// </summary>
        internal void Clear()
        {
            _readHandlers.Clear();
            _disposeHandlers.Clear();
        }
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Internal dispatch hook implemented by <c>ResolvedComponentDeclaration&lt;T&gt;</c>
    /// so the per-group precompute can reach the component's <see cref="IComponentRemovalHandlers"/>
    /// implementation without the framework knowing the concrete component type
    /// at the call site. Kept off the public <see cref="IResolvedComponentDeclaration"/>
    /// surface deliberately.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal interface IRemovalHandlerCollectable
    {
        void CollectRemovalHandlers(RemovalHandlerCollector collector);
    }
}

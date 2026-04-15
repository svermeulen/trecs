using System.ComponentModel;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Provides a fluent interface for setting initial component values on a newly created entity before submission.
    /// </summary>
    public readonly ref struct EntityInitializer
    {
        readonly Group _group;
        readonly DenseDictionary<ComponentId, IComponentArray> _groupDictionary;
        readonly int _indexInTransientBuffer;
#if DEBUG && !TRECS_IS_PROFILING
        readonly EntityInitializationTracker _tracker;
        readonly int _trackingId;
#endif

        /// <summary>
        /// The <see cref="EntityHandle"/> identifying the entity being initialized.
        /// </summary>
        public readonly EntityHandle Handle;

        /// <summary>
        /// Creates a new initializer targeting a specific entity in a transient component buffer.
        /// </summary>
        public EntityInitializer(
            Group group,
            DenseDictionary<ComponentId, IComponentArray> groupDictionary,
            in EntityHandle id,
            int indexInTransientBuffer
#if DEBUG && !TRECS_IS_PROFILING
            ,
            EntityInitializationTracker tracker,
            int trackingId
#endif
        )
        {
            _groupDictionary = groupDictionary;
            _group = group;
            _indexInTransientBuffer = indexInTransientBuffer;
            this.Handle = id;

#if DEBUG && !TRECS_IS_PROFILING
            _tracker = tracker;
            _trackingId = trackingId;
#endif
        }

        /// <summary>
        /// Validates that all required components have been set on this entity (debug builds only).
        /// </summary>
        public EntityInitializer AssertComplete()
        {
#if DEBUG && !TRECS_IS_PROFILING
            _tracker?.ValidateEntry(_trackingId);
#endif

            return this;
        }

        /// <summary>
        /// Sets the initial value of component <typeparamref name="T"/> on this entity.
        /// </summary>
        /// <param name="initializer">The component value to assign.</param>
        public EntityInitializer Set<T>(in T component)
            where T : unmanaged, IEntityComponent
        {
            if (!_groupDictionary.TryGetValue(ComponentTypeId<T>.Value, out var typeSafeDictionary))
            {
                throw new TrecsException(
                    $"Expected to find component type '{typeof(T).GetPrettyName()}' associated with entity initializer but none was found, while adding to group {_group}"
                );
            }

#if DEBUG && !TRECS_IS_PROFILING
            _tracker?.MarkComponentSet(_trackingId, ComponentTypeId<T>.Value, _group);
#endif

            var dictionary = (IComponentArray<T>)typeSafeDictionary;
            dictionary.GetValueAtIndexByRef(_indexInTransientBuffer) = component;

            return this;
        }

        /// <summary>
        /// Type-erased version of Set for runtime-determined component types.
        /// Copies raw bytes from <paramref name="valuePtr"/> into the component buffer.
        /// </summary>
        internal unsafe EntityInitializer SetRawImpl(ComponentId componentId, void* valuePtr)
        {
            if (!_groupDictionary.TryGetValue(componentId, out var componentArray))
            {
                throw new TrecsException(
                    $"Expected to find component with ID '{componentId.Value}' associated with entity initializer but none was found, while adding to group {_group}"
                );
            }

#if DEBUG && !TRECS_IS_PROFILING
            _tracker?.MarkComponentSet(_trackingId, componentId, _group);
#endif

            int elementSize = componentArray.ElementSize;
            byte* destPtr =
                (byte*)componentArray.GetUnsafePtr() + _indexInTransientBuffer * elementSize;
            UnsafeUtility.MemCpy(destPtr, valuePtr, elementSize);

            return this;
        }
    }
}

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class EntityInitializerExtensions
    {
        public static unsafe EntityInitializer SetRaw(
            this EntityInitializer initializer,
            ComponentId componentId,
            void* valuePtr
        )
        {
            return initializer.SetRawImpl(componentId, valuePtr);
        }
    }
}

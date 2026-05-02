using System;
using System.Collections.Concurrent;
using System.Reflection;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Type-erased boxed component read/write for editor tooling that doesn't
    /// know the component type at compile time. Lives outside Trecs core so the
    /// runtime API doesn't carry reflection-based boxing helpers that only
    /// editor windows need.
    /// </summary>
    public static class WorldAccessorBoxedExtensions
    {
        // Cached generic-method lookups for the type-erased component API,
        // populated lazily per component type.
        static readonly ConcurrentDictionary<Type, MethodInfo> _readBoxedMethodCache = new();
        static readonly ConcurrentDictionary<Type, MethodInfo> _writeBoxedMethodCache = new();

        /// <summary>
        /// Returns the component of the given type on the given entity as a
        /// boxed copy. The returned object is a snapshot — modifying it has no
        /// effect on the world; call <see cref="WriteComponentBoxed"/> to write
        /// changes back.
        /// </summary>
        public static object ReadComponentBoxed(
            this WorldAccessor accessor,
            EntityIndex entityIndex,
            Type componentType
        )
        {
            Require.That(componentType != null, "componentType must not be null");
            var generic = _readBoxedMethodCache.GetOrAdd(
                componentType,
                static t =>
                {
                    var open = typeof(WorldAccessorBoxedExtensions).GetMethod(
                        nameof(ReadComponentBoxedImpl),
                        BindingFlags.NonPublic | BindingFlags.Static
                    );
                    return open.MakeGenericMethod(t);
                }
            );
            return generic.Invoke(null, new object[] { accessor, entityIndex });
        }

        /// <summary>
        /// Writes <paramref name="value"/> (a boxed copy of an unmanaged
        /// component struct of type <paramref name="componentType"/>) to the
        /// given entity's component slot.
        /// </summary>
        public static void WriteComponentBoxed(
            this WorldAccessor accessor,
            EntityIndex entityIndex,
            Type componentType,
            object value
        )
        {
            Require.That(componentType != null, "componentType must not be null");
            Require.That(value != null, "value must not be null");
            Require.That(
                componentType.IsInstanceOfType(value),
                "value type {} does not match componentType {}",
                value.GetType().Name,
                componentType.Name
            );
            var generic = _writeBoxedMethodCache.GetOrAdd(
                componentType,
                static t =>
                {
                    var open = typeof(WorldAccessorBoxedExtensions).GetMethod(
                        nameof(WriteComponentBoxedImpl),
                        BindingFlags.NonPublic | BindingFlags.Static
                    );
                    return open.MakeGenericMethod(t);
                }
            );
            generic.Invoke(null, new object[] { accessor, entityIndex, value });
        }

        static object ReadComponentBoxedImpl<T>(WorldAccessor accessor, EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            T copy = accessor.Component<T>(entityIndex).Read;
            return copy;
        }

        static void WriteComponentBoxedImpl<T>(
            WorldAccessor accessor,
            EntityIndex entityIndex,
            object value
        )
            where T : unmanaged, IEntityComponent
        {
            ref var slot = ref accessor.Component<T>(entityIndex).Write;
            slot = (T)value;
        }
    }
}

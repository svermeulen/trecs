using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    /// <summary>
    /// Recycling pool for <see cref="SerializationData"/> instances. Each retained snapshot
    /// holds two multi-KB (often multi-MB, LOH) buffers; capturing on a cadence would churn
    /// the LOH on every frame. This pool recycles whole <see cref="SerializationData"/>
    /// instances (their buffer capacity is kept by <see cref="SerializationData.Clear"/>),
    /// bounded by <see cref="MaxBuffers"/> to cap resident memory.
    ///
    /// <para>Main-thread only.</para>
    /// </summary>
    public sealed class SerializationDataPool
    {
        public const int DefaultMaxBuffers = 64;

        readonly Stack<SerializationData> _pool = new();
#if DEBUG
        readonly HashSet<SerializationData> _poolSet = new();
#endif

        public int MaxBuffers { get; }

        public SerializationDataPool(int maxBuffers = DefaultMaxBuffers)
        {
            if (maxBuffers < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxBuffers),
                    maxBuffers,
                    "maxBuffers must be >= 0"
                );
            MaxBuffers = maxBuffers;
        }

        /// <summary>
        /// Rent a cleared <see cref="SerializationData"/> from the pool, or allocate a fresh
        /// one. The caller serializes into it (via <see cref="Trecs.BinarySerializationWriter"/>)
        /// and later returns it with <see cref="Despawn"/>.
        /// </summary>
        public SerializationData Spawn()
        {
            if (_pool.Count > 0)
            {
                var data = _pool.Pop();
#if DEBUG
                _poolSet.Remove(data);
#endif
                // Already cleared on Despawn; cheap idempotent reset guards against a caller
                // that mutated a just-spawned instance before a prior despawn landed.
                data.Clear();
                return data;
            }
            return new SerializationData();
        }

        /// <summary>
        /// Return a <see cref="SerializationData"/> previously obtained from <see cref="Spawn"/>.
        /// Cleared (capacity kept) and pushed back; past the cap it is dropped for the GC.
        ///
        /// <para>Returning the same instance twice is a bug — DEBUG builds assert on it.</para>
        /// </summary>
        public void Despawn(SerializationData data)
        {
            if (data == null)
            {
                return;
            }
            data.Clear();
            if (_pool.Count >= MaxBuffers)
            {
                return;
            }
#if DEBUG
            TrecsDebugAssert.That(
                _poolSet.Add(data),
                "SerializationDataPool instance returned twice — caller tracking is broken"
            );
#endif
            _pool.Push(data);
        }
    }
}

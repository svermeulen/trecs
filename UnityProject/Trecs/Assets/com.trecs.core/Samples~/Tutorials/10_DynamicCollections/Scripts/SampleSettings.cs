using System;

namespace Trecs.Samples.DynamicCollections
{
    public enum TrailCollectionType
    {
        // Managed Queue<float3> behind a UniquePtr — heap, ring buffer
        // trimmed to TrailLength every frame.
        UniquePtrQueue,

        // Inline FixedArray32 ring buffer trimmed in-place. Blittable,
        // Burst-friendly, no heap pointer.
        FixedArrayRingBuffer,

        // Inline FixedList256 with no trimming — grows until full, then
        // stops appending. Blittable.
        FixedListAppend,

        // TrecsList on the world's shared native chunk store — truly growable.
        // The component is a 4-byte handle; the backing buffer doubles
        // geometrically via EnsureCapacity.
        TrecsListAppend,

        // TrecsArray on the world's shared native chunk store — size chosen
        // at allocation time (SampleSettings.TrailLength), fixed thereafter.
        // The component is a 4-byte handle; we use the array as a ring
        // buffer with Head/Count, same shape as FixedArrayRingBuffer but
        // with a heap-backed buffer whose size isn't baked into the type.
        TrecsArrayRingBuffer,
    }

    [Serializable]
    public class SampleSettings
    {
        // Which trail backing to demonstrate this run. The composition
        // root spawns one Character template variant and registers one
        // pair of (trail-updater, presenter) systems based on this.
        public TrailCollectionType CollectionType = TrailCollectionType.UniquePtrQueue;

        // How many character entities to spawn at scene start.
        public int CharacterCount = 5;

        // Half-width of the wander box on the XZ plane, in world units.
        // Characters stay within ±WanderExtent on each axis, so the box
        // they roam is (2 * WanderExtent) on a side.
        public float WanderExtent = 10f;

        // Rate at which we advance through the Perlin noise field.
        // Higher = characters move faster; lower = lazier drift.
        public float WanderTimeScale = 0.4f;

        // How many recent positions each character's trail retains —
        // honored by the UniquePtrQueue, FixedArrayRingBuffer, and
        // TrecsArrayRingBuffer variants. FixedListAppend and
        // TrecsListAppend grow without trimming. TrecsArrayRingBuffer
        // additionally uses this as the array's fixed allocation length.
        public int TrailLength = 30;

        // Minimum world-space distance the character must move from its
        // last sampled trail point before a new point is appended.
        // Applies to all four variants; sampling sparsely lets the
        // FixedListAppend trail cover much more travel before it fills
        // and keeps the Queue/FixedArray ring buffers spanning a longer
        // tail. Set to 0 to revert to per-tick sampling.
        public float TrailMinSampleDistance = 0.3f;
    }
}

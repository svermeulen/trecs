using System.Collections.Generic;
using Trecs.Collections;
using Unity.Mathematics;

namespace Trecs.Samples.DynamicCollections
{
    public static class DynamicCollectionsTags
    {
        // Shared "category" tag carried by every Character variant — lets
        // the variant-agnostic CharacterMover system iterate them all.
        public struct Character : ITag { }

        // Per-variant tags. The Character template only carries the shared
        // Character tag; the four variant templates each add one of these,
        // so the per-variant trail-updater systems can filter to just their
        // own entities via [ForEachEntity(typeof(Character), typeof(...))].
        public struct QueueTrail : ITag { }

        public struct FixedArrayTrail : ITag { }

        public struct FixedListTrail : ITag { }

        public struct TrecsListTrail : ITag { }

        public struct TrecsArrayTrail : ITag { }
    }

    /// <summary>
    /// Per-character seed into the 2D Perlin field that drives wandering.
    /// Distinct values keep characters from tracing identical paths.
    /// </summary>
    [Unwrap]
    public partial struct NoiseOffset : IEntityComponent
    {
        public float Value;
    }

    /// <summary>
    /// World-space position of the most-recent trail sample. Each
    /// per-variant updater compares the character's current
    /// <see cref="Position"/> against this and only appends a new trail
    /// point once the character has moved more than
    /// <c>SampleSettings.TrailMinSampleDistance</c>.
    /// </summary>
    [Unwrap]
    public partial struct LastSamplePosition : IEntityComponent
    {
        public float3 Value;
    }

    // ─── Trail variants ────────────────────────────────────────────────
    // One trail component per dynamic-collection type the sample demonstrates.
    // Only one of these lives on a character at a time, selected by the
    // SampleSettings.CollectionType inspector field at composition-root build.

    /// <summary>
    /// Trail backed by <see cref="UniquePtr{T}"/> wrapping a managed
    /// <see cref="Queue{T}"/>. The Queue lives on the world's UniqueHeap;
    /// the component itself is just a 4-byte handle. The queue grows and
    /// shrinks dynamically — we use it as a ring buffer trimmed to
    /// <c>SampleSettings.TrailLength</c>.
    /// </summary>
    [Unwrap]
    public partial struct TrailQueue : IEntityComponent
    {
        public UniquePtr<Queue<float3>> Value;
    }

    /// <summary>
    /// Trail stored inline as a <see cref="FixedArray32{T}"/> ring buffer.
    /// Blittable, no heap pointer, no cleanup handler — storage travels
    /// with the component and is freed automatically when the entity is
    /// removed. <see cref="Head"/>/<see cref="Count"/> track the live range.
    /// </summary>
    public partial struct TrailFixedArray : IEntityComponent
    {
        public FixedArray32<float3> Positions;
        public int Head;
        public int Count;
    }

    /// <summary>
    /// Trail stored inline as a <see cref="FixedList256{T}"/> ring buffer.
    /// While the list is still filling, new positions are appended (the
    /// list's own <c>Count</c> tracks the live length). Once
    /// <c>Count == Capacity</c>, writes overwrite the slot at
    /// <see cref="Head"/> and advance it. Same shape as
    /// <see cref="TrailFixedArray"/>, but the list's intrinsic <c>Count</c>
    /// removes the need for a separate count field.
    /// </summary>
    public partial struct TrailFixedList : IEntityComponent
    {
        public FixedList32<float3> Positions;
        public int Head;
    }

    /// <summary>
    /// Trail stored as a truly-growable <see cref="TrecsList{T}"/> on the world's
    /// shared native chunk store. The component carries a
    /// 4-byte <c>PtrHandle</c>; the backing buffer is native (blittable,
    /// Burst-friendly) and reallocates geometrically via
    /// <c>EnsureCapacity</c> as the trail grows. No upper bound.
    /// </summary>
    [Unwrap]
    public partial struct TrailTrecsList : IEntityComponent
    {
        public TrecsList<float3> Value;
    }

    /// <summary>
    /// Trail backed by a fixed-length <see cref="TrecsArray{T}"/> on the
    /// world's shared native chunk store, used as a ring buffer. Length is
    /// chosen at allocation time (<c>SampleSettings.TrailLength</c>) and
    /// fixed thereafter — same shape as <see cref="TrailFixedArray"/>, but
    /// the size isn't baked into the type and the storage lives on the heap.
    /// <see cref="Head"/>/<see cref="Count"/> track the live range; an
    /// on-remove handler frees the array.
    /// </summary>
    public partial struct TrailTrecsArray : IEntityComponent
    {
        public TrecsArray<float3> Positions;
        public int Head;
        public int Count;
    }

    public static partial class SampleTemplates
    {
        // Base template with everything except the trail. Each of the four
        // variants below extends this and adds one trail component plus a
        // distinguishing per-variant tag.
        public partial class Character
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<DynamicCollectionsTags.Character>
        {
            Position Position;
            NoiseOffset NoiseOffset;
            LastSamplePosition LastSamplePosition;
            PrefabId PrefabId = new(DynamicCollectionsPrefabs.Character);
        }

        public partial class CharacterQueue
            : ITemplate,
                IExtends<Character>,
                ITagged<DynamicCollectionsTags.QueueTrail>
        {
            TrailQueue TrailQueue;
        }

        public partial class CharacterFixedArray
            : ITemplate,
                IExtends<Character>,
                ITagged<DynamicCollectionsTags.FixedArrayTrail>
        {
            TrailFixedArray TrailFixedArray = default;
        }

        public partial class CharacterFixedList
            : ITemplate,
                IExtends<Character>,
                ITagged<DynamicCollectionsTags.FixedListTrail>
        {
            TrailFixedList TrailFixedList = default;
        }

        public partial class CharacterTrecsList
            : ITemplate,
                IExtends<Character>,
                ITagged<DynamicCollectionsTags.TrecsListTrail>
        {
            TrailTrecsList TrailTrecsList;
        }

        public partial class CharacterTrecsArray
            : ITemplate,
                IExtends<Character>,
                ITagged<DynamicCollectionsTags.TrecsArrayTrail>
        {
            TrailTrecsArray TrailTrecsArray;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Samples.HeightmapBlobs
{
    public static class SampleTags
    {
        // Shared "category" tag carried by both flavor variants, so the
        // flavor-agnostic CharacterMover system can iterate them all.
        public struct Character : ITag { }

        // Per-flavor tags. The Character template only carries the shared
        // Character tag; each flavor template adds one of these, so the
        // per-flavor heightmap-follower systems can filter to just their
        // own entities via [ForEachEntity(typeof(Character), typeof(...))].
        public struct ManagedFollower : ITag { }

        public struct NativeFollower : ITag { }

        public struct NativeFollowerLarge : ITag { }

        public struct InterfaceFollower : ITag { }
    }

    /// <summary>
    /// Inputs that define a heightmap's content. Hashed with
    /// <see cref="UniqueHashGenerator"/> to derive the heightmap's
    /// <see cref="BlobId"/>, so two callers asking for "16×16, seed 42,
    /// 4m tall, 20m wide" always get the same blob — the content recipe is
    /// the identity. Changing any field produces a different BlobId, so the
    /// cache lookup naturally invalidates when the recipe changes.
    /// </summary>
    public readonly partial struct HeightmapDescriptor
    {
        public int Resolution { get; init; }
        public float WorldSize { get; init; }
        public float MaxHeight { get; init; }
        public uint Seed { get; init; }
        public float Frequency { get; init; }
    }

    // ─── Managed flavor ────────────────────────────────────────────────
    // HeightmapData is a managed class (holds a float[]), so it lives behind
    // a SharedPtr<T> on the world's managed shared heap.

    /// <summary>
    /// Managed heightmap blob. Holds a flat <c>float[]</c> of length
    /// <c>Resolution * Resolution</c>, plus the original descriptor used to
    /// build it (so systems can convert world coordinates back to grid
    /// indices). Lives once on the world's shared heap and is referenced by
    /// many entities via <see cref="SharedPtr{T}"/>.
    ///
    /// <para>Marked <c>[Immutable]</c> to satisfy the TRECS125 analyzer:
    /// managed shared blobs live in the BlobCache, which is not snapshotted
    /// alongside game-state. The <c>float[]</c> backing store is held as a
    /// private <c>readonly</c> field and exposed only via a
    /// <see cref="ReadOnlySpan{T}"/> accessor, so external callers cannot
    /// mutate the cached heights. The constructor takes ownership of the
    /// array — pass a freshly-built array, do not keep an alias.</para>
    /// </summary>
    [Immutable]
    public sealed class HeightmapData
    {
        public readonly HeightmapDescriptor Descriptor;
        readonly float[] _heights;

        public HeightmapData(HeightmapDescriptor descriptor, float[] heights)
        {
            Descriptor = descriptor;
            _heights = heights;
        }

        public ReadOnlySpan<float> Heights => _heights;
    }

    /// <summary>
    /// References the shared managed heightmap. The <see cref="SharedPtr{T}"/>
    /// handle is a 12-byte value type stored inline in the component.
    /// </summary>
    [Unwrap]
    public partial struct ManagedHeightmapRef : IEntityComponent
    {
        public SharedPtr<HeightmapData> Value;
    }

    // ─── Managed flavor (interface adoption path) ──────────────────────
    // MutableHeightmapData keeps an unconstrained construction model
    // (public mutable fields, populated via an object initializer) and is
    // exposed to entity-side callers via the [Immutable] IReadOnlyHeightmapData
    // interface. The SharedPtr is parameterised on the interface.

    /// <summary>
    /// Read-only face of <see cref="MutableHeightmapData"/>. Marked
    /// <c>[Immutable]</c> so it can sit behind a <see cref="SharedPtr{T}"/> —
    /// this is the <b>interface adoption path</b> for the analyzer's
    /// TRECS125 rule.
    ///
    /// <para>Contrast with <see cref="HeightmapData"/>, where the class
    /// itself is marked <c>[Immutable]</c> and structurally audited (every
    /// instance field <c>readonly</c>, no public setters, etc.). The class
    /// route fits small leaf types built once via a constructor; the
    /// interface route fits types whose construction lifecycle prefers
    /// per-field initialization — here a public-fields-plus-object-initializer
    /// shape that the class-route rules would reject.</para>
    /// </summary>
    [Immutable]
    public interface IReadOnlyHeightmapData
    {
        HeightmapDescriptor Descriptor { get; }

        // IReadOnlyList<float> is in the analyzer's safe-property-type set,
        // so exposing the concrete's mutable backing array through this
        // view is analyzer-friendly. float[] implements IReadOnlyList<float>
        // directly, so the concrete forwards Heights with no wrapper.
        IReadOnlyList<float> Heights { get; }
    }

    /// <summary>
    /// Mutable concrete behind <see cref="IReadOnlyHeightmapData"/>. Public
    /// mutable fields plus an object-initializer construction site — exactly
    /// the shape that's incompatible with the class-route field-level
    /// immutability rules (TRECS126), and the reason the interface route
    /// exists.
    ///
    /// <para>Not marked <c>[Immutable]</c> itself — the analyzer only
    /// checks the interface surface. Explicit-interface forwarders keep the
    /// concrete's mutable surface available on the class, but
    /// <see cref="SharedPtr{T}.Get"/> returns the interface, so entity-side
    /// callers only see the read-only face.</para>
    /// </summary>
    public sealed class MutableHeightmapData : IReadOnlyHeightmapData
    {
        public HeightmapDescriptor Descriptor;
        public float[] Heights;

        HeightmapDescriptor IReadOnlyHeightmapData.Descriptor => Descriptor;
        IReadOnlyList<float> IReadOnlyHeightmapData.Heights => Heights;
    }

    /// <summary>
    /// References the shared heightmap through its
    /// <see cref="IReadOnlyHeightmapData"/> face. Same 12-byte handle shape
    /// as <see cref="ManagedHeightmapRef"/>; differs only in the resolved
    /// type — the read-only interface rather than the sealed immutable
    /// class.
    /// </summary>
    [Unwrap]
    public partial struct InterfaceHeightmapRef : IEntityComponent
    {
        public SharedPtr<IReadOnlyHeightmapData> Value;
    }

    // ─── Native flavor ─────────────────────────────────────────────────
    // NativeHeightmapData is an unmanaged struct, so it lives behind a
    // NativeSharedPtr<T> on the native shared heap and can be read by
    // Burst-compiled jobs via NativeSharedPtrResolver.

    /// <summary>
    /// Unmanaged heightmap blob: the descriptor plus a 16×16 (256-element)
    /// inline grid of heights. Sized so the entire blob fits inside the
    /// struct — no secondary allocation needed, just <c>NativeSharedPtr.Alloc</c>
    /// of the value. For larger heightmaps, see
    /// <see cref="NativeHeightmapDataLarge"/>, which uses a
    /// <c>BlobArray&lt;float&gt;</c> field built via <c>BlobBuilder</c> —
    /// single allocation, relocatable, no inline cap, and no intermediate
    /// stack-to-field copy on the seed path.
    ///
    /// <para>Declared <c>readonly struct</c> to satisfy the TRECS124 analyzer:
    /// types stored behind <c>NativeSharedPtr&lt;T&gt;</c> must be immutable
    /// because the BlobCache does not snapshot blob memory with game-state
    /// snapshots. The inner <see cref="FixedArray256{T}"/> isn't itself a
    /// readonly struct (it exposes a <c>Mut</c> extension), but is held here
    /// as a <c>readonly</c> private field — external callers can only reach
    /// the cell data through the read-only <see cref="Heights"/> accessor,
    /// which returns by <c>ref readonly</c>.</para>
    /// </summary>
    public readonly partial struct NativeHeightmapData
    {
        public readonly HeightmapDescriptor Descriptor;
        readonly FixedArray256<float> _heights;

        public NativeHeightmapData(
            in HeightmapDescriptor descriptor,
            in FixedArray256<float> heights
        )
        {
            Descriptor = descriptor;
            _heights = heights;
        }

        /// <summary>
        /// Read a single height cell by index. Returns by value — at 4 bytes
        /// per float the copy is free, and avoiding a <c>ref readonly</c>
        /// return keeps the sample free of the unsafe pointer-arithmetic
        /// dance you'd otherwise need to dodge CS8170 ("struct members
        /// cannot return 'this' by reference"). Burst inlines this through
        /// to a direct memory read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Get(int index) => _heights[index];
    }

    /// <summary>
    /// References the shared native heightmap. <see cref="NativeSharedPtr{T}"/>
    /// is a 12-byte value type and the blob it points at is unmanaged, so
    /// the system that reads it can run as a Burst-compiled job that
    /// resolves the handle via <c>NativeSharedPtrResolver</c>.
    /// </summary>
    [Unwrap]
    public partial struct NativeHeightmapRef : IEntityComponent
    {
        public NativeSharedPtr<NativeHeightmapData> Value;
    }

    // ─── Native flavor (large / taking-ownership) ─────────────────────
    // NativeHeightmapDataLarge is the "blob bigger than inline storage"
    // shape. Heights live in the same native allocation as the root struct,
    // reached via a relative-offset BlobArray<float>. Seeded by
    // BlobBuilder + NativeSharedPtr.AllocTakingOwnership; the heap takes
    // ownership of the (ptr, size, alignment) triple and frees it through
    // AllocatorManager when the refcount hits zero.

    /// <summary>
    /// Native heightmap blob with heights in a relative-offset
    /// <see cref="BlobArray{T}"/>. Built via
    /// <see cref="BlobBuilder.Build{T}(WorldAccessor, BlobId)"/> — the
    /// builder produces a single contiguous allocation containing this
    /// header struct plus the heights, and patches <see cref="Heights"/>'s
    /// offset to point at them. No intermediate stack-to-field copy on
    /// the seed path and no inline-storage cap, unlike
    /// <see cref="NativeHeightmapData"/>.
    ///
    /// <para>Declared as a plain <c>struct</c> (not <c>readonly struct</c>)
    /// with no instance methods, so TRECS124's defensive-copy-safety check
    /// passes vacuously — and the seed site can assign <c>root.Descriptor</c>
    /// directly during construction without a constructor or wholesale
    /// re-assignment. Reads still go through <c>NativeSharedRead{T}.Value</c>
    /// which returns <c>ref readonly</c>, so post-seed mutation through the
    /// public API is impossible. <see cref="Heights"/>'s indexer returns by
    /// <c>ref readonly</c> so reads stay zero-copy and Burst-friendly.</para>
    /// </summary>
    [NonCopyable]
    public partial struct NativeHeightmapDataLarge
    {
        public HeightmapDescriptor Descriptor;
        public BlobArray<float> Heights;
    }

    /// <summary>
    /// References the shared header+trailing-data native heightmap. Same
    /// 12-byte handle shape as <see cref="NativeHeightmapRef"/>; differs
    /// only in the resolved blob type.
    /// </summary>
    [Unwrap]
    public partial struct NativeHeightmapRefLarge : IEntityComponent
    {
        public NativeSharedPtr<NativeHeightmapDataLarge> Value;
    }

    // ─── Common character components ───────────────────────────────────

    /// <summary>
    /// Per-character seed into the 2D noise field that drives wandering.
    /// Distinct values keep characters from tracing identical paths.
    /// </summary>
    [Unwrap]
    public partial struct NoiseOffset : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        /// <summary>
        /// Base template carrying everything except the heightmap reference.
        /// Each per-flavor template extends this and adds one heightmap-ref
        /// component plus a distinguishing per-flavor tag.
        /// </summary>
        public abstract partial class Character
            : ITemplate,
                IExtends<CommonTemplates.RenderableGameObject>,
                ITagged<SampleTags.Character>
        {
            Position Position;
            NoiseOffset NoiseOffset;
            PrefabId PrefabId = new(HeightmapBlobsPrefabs.Character);
        }

        public partial class ManagedCharacter
            : ITemplate,
                IExtends<Character>,
                ITagged<SampleTags.ManagedFollower>
        {
            ManagedHeightmapRef HeightmapRef;
        }

        public partial class NativeCharacter
            : ITemplate,
                IExtends<Character>,
                ITagged<SampleTags.NativeFollower>
        {
            NativeHeightmapRef HeightmapRef;
        }

        public partial class NativeCharacterLarge
            : ITemplate,
                IExtends<Character>,
                ITagged<SampleTags.NativeFollowerLarge>
        {
            NativeHeightmapRefLarge HeightmapRef;
        }

        public partial class InterfaceCharacter
            : ITemplate,
                IExtends<Character>,
                ITagged<SampleTags.InterfaceFollower>
        {
            InterfaceHeightmapRef HeightmapRef;
        }
    }
}

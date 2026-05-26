using System;

namespace Trecs.Samples.HeightmapBlobs
{
    public enum HeightmapFlavor
    {
        // Managed heightmap data behind a SharedPtr<HeightmapData>. The
        // class holds a float[] and is sampled from a regular main-thread
        // system via SharedPtr.Get(World).
        ManagedSharedPtr,

        // Unmanaged heightmap data behind a NativeSharedPtr<NativeHeightmapData>.
        // The struct holds a FixedArray256<float> inline, and the sampling
        // system is a [WrapAsJob] Burst job that resolves the pointer via
        // NativeWorldAccessor.SharedPtrResolver. Simple seed path
        // (NativeSharedPtr.Alloc copies the value into the heap), but capped
        // at 256 cells by the inline storage.
        NativeSharedPtrInline,

        // Unmanaged heightmap behind a NativeSharedPtr<NativeHeightmapDataLarge>,
        // seeded via NativeSharedPtr.AllocTakingOwnership of a single
        // header+trailing-data allocation. The struct holds only a header
        // (descriptor + length); heights live in the same allocation right
        // after the header and are reached via pointer arithmetic. Lifts
        // the 256-cell cap and eliminates the intermediate stack copy — at
        // the cost of unsafe code at the seed site.
        NativeSharedPtrTakingOwnership,

        // Managed heightmap behind SharedPtr<IReadOnlyHeightmapData>. The
        // pointer type-parameter is the read-only interface; the concrete
        // (MutableHeightmapData) stays mutable so it can be populated with
        // an object initializer rather than a constructor. Demonstrates the
        // interface adoption path for SharedPtr<T>.
        ManagedSharedPtrInterface,
    }

    [Serializable]
    public class SampleSettings
    {
        // Which flavor to demonstrate this run. The composition root spawns
        // one Character template variant and registers one follower system
        // based on this.
        public HeightmapFlavor Flavor = HeightmapFlavor.ManagedSharedPtr;

        // How many character entities to spawn at scene start.
        public int CharacterCount = 12;

        // Uniform scale applied to each character's sphere visual.
        public float CharacterSize = 0.5f;

        // Inputs that define the heightmap's content. Hashed via
        // UniqueHashGenerator → BlobId, so two callers with the same values
        // resolve to the same blob.
        //
        // Resolution × Resolution must be ≤ 256 for the NativeSharedPtrInline
        // flavor (the inline FixedArray256 sets the cap). 16×16 = 256 — at
        // the cap; shrink Resolution for sparser surfaces. The
        // NativeSharedPtrTakingOwnership flavor uses a single
        // header+trailing-data allocation and has no inline cap.
        public int HeightmapResolution = 16;
        public float HeightmapWorldSize = 20f;
        public float HeightmapMaxHeight = 3f;
        public uint HeightmapSeed = 12345u;

        // Roughly the number of hills across the surface. Higher = bumpier,
        // lower = smoother.
        public float HeightmapFrequency = 2f;

        // Rate at which characters drift through the noise field.
        // Higher = faster wander; lower = lazier drift.
        public float WanderTimeScale = 0.3f;
    }
}

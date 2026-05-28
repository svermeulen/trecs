using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Unit tests for <see cref="BlobBuilder"/> / <see cref="BlobArray{T}"/>.
    /// Most tests build a blob, read it back through an
    /// <see cref="UnsafeUtility.AsRef{T}"/> over the returned
    /// <see cref="NativeBlobAllocation"/>, and free the result manually.
    /// One test runs the full <see cref="BlobBuilder.Build{T}(WorldAccessor, BlobId)"/>
    /// path through a real world / heap to confirm the integration.
    /// </summary>
    [TestFixture]
    public unsafe class BlobBuilderTests
    {
        // ───────────────────────────────────────────────────────────
        // Test blob shapes (readonly struct — matches real-world usage
        // since NativeSharedPtr<T> requires T to be a readonly struct).
        // Each shape exposes a constructor that sets non-array fields;
        // BlobArray fields stay at default and are patched at Build time.
        // ───────────────────────────────────────────────────────────

        [NonCopyable]
        readonly struct RootWithSingleArray
        {
            public readonly int Header;
            public readonly BlobArray<int> Values;

            public RootWithSingleArray(int header)
            {
                Header = header;
                Values = default;
            }
        }

        [NonCopyable]
        readonly struct RootWithThreeArrays
        {
            public readonly int Magic;
            public readonly BlobArray<int> Ints;
            public readonly BlobArray<float> Floats;
            public readonly BlobArray<byte> Bytes;

            public RootWithThreeArrays(int magic)
            {
                Magic = magic;
                Ints = default;
                Floats = default;
                Bytes = default;
            }
        }

        readonly struct RootPrimitiveOnly
        {
            public readonly int A;
            public readonly float B;
            public readonly long C;

            public RootPrimitiveOnly(int a, float b, long c)
            {
                A = a;
                B = b;
                C = c;
            }
        }

        readonly struct Aligned16
        {
            public readonly float4 V;

            public Aligned16(float4 v)
            {
                V = v;
            }
        }

        [NonCopyable]
        readonly struct RootWithAligned16Array
        {
            public readonly int Tag;
            public readonly BlobArray<Aligned16> Values;

            public RootWithAligned16Array(int tag)
            {
                Tag = tag;
                Values = default;
            }
        }

        // Shape with a BlobArray plus enough scalar fields to push sizeof
        // above the minimum chunkSize (64). Used by the
        // "overflow-only root" test below — the root itself takes the
        // standalone-chunk branch in AllocateInternal.
        [NonCopyable]
        readonly struct OversizedRootWithArray
        {
            // 9 longs = 72 bytes > 64-byte minimum chunkSize. Plus the
            // trailing BlobArray<int> (8 bytes payload + 4 bytes length on
            // x64) keeps the total above the smallest chunkSize we allow.
            public readonly long A0;
            public readonly long A1;
            public readonly long A2;
            public readonly long A3;
            public readonly long A4;
            public readonly long A5;
            public readonly long A6;
            public readonly long A7;
            public readonly long A8;
            public readonly BlobArray<int> Values;

            public OversizedRootWithArray(long seed)
            {
                A0 = seed;
                A1 = seed + 1;
                A2 = seed + 2;
                A3 = seed + 3;
                A4 = seed + 4;
                A5 = seed + 5;
                A6 = seed + 6;
                A7 = seed + 7;
                A8 = seed + 8;
                Values = default;
            }
        }

        // Shapes for BlobRef<T> tests.
        readonly struct Payload
        {
            public readonly int A;
            public readonly float B;

            public Payload(int a, float b)
            {
                A = a;
                B = b;
            }
        }

        [NonCopyable]
        readonly struct RootWithSingleRef
        {
            public readonly int Header;
            public readonly BlobRef<Payload> Single;

            public RootWithSingleRef(int header)
            {
                Header = header;
                Single = default;
            }
        }

        [NonCopyable]
        readonly struct RootWithOptionalRef
        {
            public readonly int Tag;
            public readonly BlobRef<Payload> Maybe;

            public RootWithOptionalRef(int tag)
            {
                Tag = tag;
                Maybe = default;
            }
        }

        // Shape with a BlobRef followed by another field, used to verify that
        // the BlobRef patch does not overwrite the four bytes immediately after
        // the BlobRef's m_OffsetPtr (Dead Path A regression: BlobArray writes
        // length at OffsetPtr+4, BlobRef must not).
        [NonCopyable]
        readonly struct RootWithRefThenField
        {
            public readonly BlobRef<Payload> Ref;
            public readonly int Sentinel;

            public RootWithRefThenField(int sentinel)
            {
                Ref = default;
                Sentinel = sentinel;
            }
        }

        // Shape with multiple BlobRef fields all pointing into the same blob —
        // exercises the cross-reference / "two references to the same payload"
        // story end-to-end.
        [NonCopyable]
        readonly struct RootWithTwoRefs
        {
            public readonly BlobRef<Payload> First;
            public readonly BlobRef<Payload> Second;

            public RootWithTwoRefs(byte unused)
            {
                First = default;
                Second = default;
            }
        }

        // Shapes for nested BlobArray<T> tests.
        [NonCopyable]
        readonly struct Polygon
        {
            public readonly int Id;
            public readonly BlobArray<int> Vertices;

            public Polygon(int id)
            {
                Id = id;
                Vertices = default;
            }
        }

        [NonCopyable]
        readonly struct Region
        {
            public readonly int RegionId;
            public readonly BlobArray<Polygon> Polygons;

            public Region(int regionId)
            {
                RegionId = regionId;
                Polygons = default;
            }
        }

        [NonCopyable]
        readonly struct RootWithNestedArrays
        {
            public readonly int Header;
            public readonly BlobArray<Region> Regions;

            public RootWithNestedArrays(int header)
            {
                Header = header;
                Regions = default;
            }
        }

        // ───────────────────────────────────────────────────────────
        // Helpers
        // ───────────────────────────────────────────────────────────

        // Mirrors what NativeBlobBox does at end-of-life when the heap owns
        // the blob. Used in tests that don't route through NativeSharedPtr.
        static void Free(NativeBlobAllocation alloc)
        {
            AllocatorManager.Free(
                Allocator.Persistent,
                (void*)alloc.Ptr,
                alloc.AllocSize,
                alloc.Alignment,
                items: 1
            );
        }

        // ───────────────────────────────────────────────────────────
        // Root-only blobs (no BlobArray fields)
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_RootOnly_RoundtripsPrimitiveFields()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootPrimitiveOnly>();
                root = new RootPrimitiveOnly(7, 3.5f, 999_999L);
                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootPrimitiveOnly>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(7, view.A);
                NAssert.AreEqual(3.5f, view.B);
                NAssert.AreEqual(999_999L, view.C);
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Single-array roundtrip
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_SingleArray_RoundtripsValues()
        {
            const int count = 8;

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0xCAFE);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = i * 10;
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(0xCAFE, view.Header);
                NAssert.AreEqual(count, view.Values.Length);
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(i * 10, view.Values[i], $"Mismatch at index {i}");
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // foreach enumerator
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_BlobArray_Foreach_Roundtrips()
        {
            const int count = 5;

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = (i + 1) * 7;
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                    (void*)alloc.Ptr
                );

                // Walk via foreach and verify order + values match the build.
                int seen = 0;
                foreach (ref readonly var v in view.Values)
                {
                    NAssert.AreEqual((seen + 1) * 7, v, $"Mismatch at index {seen}");
                    seen++;
                }
                NAssert.AreEqual(count, seen, "Enumerator yielded wrong element count");

                // Empty-array case: foreach should produce zero iterations.
                NativeBlobAllocation emptyAlloc;
                using (var builder = new BlobBuilder(Allocator.Temp))
                {
                    ref var emptyRoot = ref builder.ConstructRoot<RootWithSingleArray>();
                    emptyRoot = new RootWithSingleArray(0);
                    builder.Allocate(in emptyRoot.Values, 0);
                    emptyAlloc = builder.BuildNativeBlobAllocation();
                }

                try
                {
                    ref readonly var emptyView = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                        (void*)emptyAlloc.Ptr
                    );
                    int emptySeen = 0;
                    foreach (ref readonly var _ in emptyView.Values)
                    {
                        emptySeen++;
                    }
                    NAssert.AreEqual(0, emptySeen, "Empty BlobArray should yield no iterations");
                }
                finally
                {
                    Free(emptyAlloc);
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Multi-array, mixed element types
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_MultipleArrays_AllRoundtrip()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithThreeArrays>();
                root = new RootWithThreeArrays(unchecked((int)0xDEADBEEFu));

                var ints = builder.Allocate(in root.Ints, 4);
                var floats = builder.Allocate(in root.Floats, 3);
                var bytes = builder.Allocate(in root.Bytes, 5);

                ints[0] = 100;
                ints[1] = 200;
                ints[2] = 300;
                ints[3] = 400;
                floats[0] = 1.5f;
                floats[1] = 2.5f;
                floats[2] = 3.5f;
                bytes[0] = 0x10;
                bytes[1] = 0x20;
                bytes[2] = 0x30;
                bytes[3] = 0x40;
                bytes[4] = 0x50;

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithThreeArrays>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(unchecked((int)0xDEADBEEFu), view.Magic);

                NAssert.AreEqual(4, view.Ints.Length);
                NAssert.AreEqual(100, view.Ints[0]);
                NAssert.AreEqual(200, view.Ints[1]);
                NAssert.AreEqual(300, view.Ints[2]);
                NAssert.AreEqual(400, view.Ints[3]);

                NAssert.AreEqual(3, view.Floats.Length);
                NAssert.AreEqual(1.5f, view.Floats[0]);
                NAssert.AreEqual(2.5f, view.Floats[1]);
                NAssert.AreEqual(3.5f, view.Floats[2]);

                NAssert.AreEqual(5, view.Bytes.Length);
                NAssert.AreEqual(0x10, view.Bytes[0]);
                NAssert.AreEqual(0x20, view.Bytes[1]);
                NAssert.AreEqual(0x30, view.Bytes[2]);
                NAssert.AreEqual(0x40, view.Bytes[3]);
                NAssert.AreEqual(0x50, view.Bytes[4]);
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Length-0 BlobArray
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_LengthZeroArray_IsEmptyAndDoesNotAllocate()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);

                var arr = builder.Allocate(in root.Values, 0);
                NAssert.AreEqual(0, arr.Length);

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(0, view.Values.Length);
                // Just sizeof(root) rounded up to chunk alignment (16).
                NAssert.AreEqual(16, alloc.AllocSize);
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Oversized allocation > chunkSize → standalone chunk
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_OversizedAllocation_StandaloneChunk_Roundtrips()
        {
            const int chunkSize = 64;
            const int count = 32; // 128 bytes, > chunkSize

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp, chunkSize))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(42);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = -i;
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(42, view.Header);
                NAssert.AreEqual(count, view.Values.Length);
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(-i, view.Values[i]);
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_OnlyOversizedAllocations_RoundtripsOk()
        {
            // Both the root *and* the array exceed chunkSize, so every
            // AllocateInternal call takes the standalone-chunk branch and
            // _currentChunkIndex stays at -1 throughout. The pre-fix
            // BuildNativeBlobAllocation asserted `_currentChunkIndex != -1`
            // and rejected this case (and would have crashed on
            // `AlignChunkTail(-1)` if the assert were stripped).
            const int chunkSize = 64;
            const int count = 32; // 128 bytes > chunkSize

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp, chunkSize))
            {
                ref var root = ref builder.ConstructRoot<OversizedRootWithArray>();
                NAssert.Greater(
                    UnsafeUtility.SizeOf<OversizedRootWithArray>(),
                    chunkSize,
                    "Test premise broken: root must exceed chunkSize to exercise "
                        + "the overflow-only path"
                );
                root = new OversizedRootWithArray(1000L);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = i * 3;
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<OversizedRootWithArray>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(1000L, view.A0);
                NAssert.AreEqual(1001L, view.A1);
                NAssert.AreEqual(1008L, view.A8);
                NAssert.AreEqual(count, view.Values.Length);
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(i * 3, view.Values[i]);
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Large array that fills multiple chunks
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_LargeArrayInOneAllocation_Roundtrips()
        {
            // Total bytes (~400) far exceed any single chunk at this size; the
            // allocation is large enough to take a standalone chunk, and the
            // root chunk is separate.
            const int chunkSize = 128;
            int count = (chunkSize / 4) * 3 + 5;

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp, chunkSize))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(1);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = i + 1;
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(1, view.Header);
                NAssert.AreEqual(count, view.Values.Length);
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(i + 1, view.Values[i]);
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Element alignment
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_Aligned16ElementType_RoundtripsAtCorrectAlignment()
        {
            const int count = 4;

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithAligned16Array>();
                root = new RootWithAligned16Array(99);

                // UnsafeUtility.AlignOf<float4>() returns 4 (alignment is
                // derived from contained primitives, not the struct as a
                // whole), so we pass 16 explicitly to exercise the
                // alignment-respecting code path.
                var values = builder.Allocate(in root.Values, count, 16);
                for (int i = 0; i < count; i++)
                {
                    values[i] = new Aligned16(new float4(i, i + 1, i + 2, i + 3));
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithAligned16Array>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(99, view.Tag);
                NAssert.AreEqual(count, view.Values.Length);

                var basePtr = view.Values.GetUnsafePtr();
                NAssert.AreEqual(0, ((long)basePtr) & 0xF, "BlobArray base not 16-aligned");

                for (int i = 0; i < count; i++)
                {
                    var expected = new float4(i, i + 1, i + 2, i + 3);
                    NAssert.AreEqual(expected.x, view.Values[i].V.x);
                    NAssert.AreEqual(expected.y, view.Values[i].V.y);
                    NAssert.AreEqual(expected.z, view.Values[i].V.z);
                    NAssert.AreEqual(expected.w, view.Values[i].V.w);
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Argument validation
        // ───────────────────────────────────────────────────────────

        // The throw-tests below use inline try/catch instead of
        // NAssert.Throws<>(() => ...) because BlobBuilder is a ref struct
        // and a lambda can't capture a ref-struct local (CS8175).

        [Test]
        public void Constructor_ChunkSizeTooSmall_Throws()
        {
            try
            {
                var builder = new BlobBuilder(Allocator.Temp, 32);
                builder.Dispose();
                NAssert.Fail("Expected TrecsException for chunkSize below the supported range");
            }
            catch (TrecsException) { }
        }

        [Test]
        public void Constructor_ChunkSizeTooLarge_Throws()
        {
            // Anything above MaxChunkSize (256 MiB) should be rejected up-front
            // rather than producing a confusing native-allocator error or, worse,
            // silently wrapping in AlignUp.
            try
            {
                var builder = new BlobBuilder(Allocator.Temp, int.MaxValue);
                builder.Dispose();
                NAssert.Fail("Expected TrecsException for chunkSize above the supported range");
            }
            catch (TrecsException) { }
        }

        [Test]
        public void Allocate_NegativeLength_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                try
                {
                    builder.Allocate(in root.Values, -1);
                    NAssert.Fail("Expected TrecsException for negative length");
                }
                catch (TrecsException) { }
            }
        }

        [Test]
        public void Allocate_AlignmentNotPowerOfTwo_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                try
                {
                    builder.Allocate(in root.Values, 4, 3);
                    NAssert.Fail("Expected TrecsException for non-power-of-two alignment");
                }
                catch (TrecsException) { }
            }
        }

        [Test]
        public void Allocate_AlignmentLargerThan16_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                try
                {
                    builder.Allocate(in root.Values, 4, 32);
                    NAssert.Fail("Expected TrecsException for oversized alignment");
                }
                catch (TrecsException) { }
            }
        }

        [Test]
        public void Build_WithoutConstructRoot_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                try
                {
                    builder.BuildNativeBlobAllocation();
                    NAssert.Fail("Expected TrecsException when no ConstructRoot was called");
                }
                catch (TrecsException) { }
            }
        }

        [Test]
        public void Allocate_ForeignBlobArray_Throws()
        {
            // A BlobArray<T> rvalue / stack temp not owned by the builder.
            // ValidateAllocation is always-on so this fires in release too.
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootPrimitiveOnly>();
                root = new RootPrimitiveOnly(0, 0, 0);
                var foreign = default(BlobArray<int>);
                try
                {
                    builder.Allocate(in foreign, 4);
                    NAssert.Fail("Expected InvalidOperationException for foreign ref");
                }
                catch (InvalidOperationException) { }
            }
        }

        // ───────────────────────────────────────────────────────────
        // Lifecycle guards (default(BlobBuilder), use-after-Build, overflow)
        // ───────────────────────────────────────────────────────────

        [Test]
        public void DefaultConstructed_Allocate_Throws()
        {
            // Reaching for an uninitialized BlobBuilder (e.g. `default(BlobBuilder)`)
            // should produce a clear assert rather than a confusing
            // NativeList-uncreated error from deeper in the call.
            BlobBuilder builder = default;
            try
            {
                builder.ConstructRoot<RootPrimitiveOnly>();
                NAssert.Fail("Expected TrecsException for default-constructed builder");
            }
            catch (TrecsException) { }
        }

        [Test]
        public void Allocate_AfterBuild_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                var alloc = builder.BuildNativeBlobAllocation();
                try
                {
                    try
                    {
                        builder.Allocate(in root.Values, 4);
                        NAssert.Fail("Expected TrecsException for Allocate after Build");
                    }
                    catch (TrecsException) { }
                }
                finally
                {
                    Free(alloc);
                }
            }
        }

        [Test]
        public void Build_Twice_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                var first = builder.BuildNativeBlobAllocation();
                try
                {
                    try
                    {
                        builder.BuildNativeBlobAllocation();
                        NAssert.Fail("Expected TrecsException for Build twice");
                    }
                    catch (TrecsException) { }
                }
                finally
                {
                    Free(first);
                }
            }
        }

        [Test]
        public void Allocate_LengthOverflowsInt_Throws()
        {
            // 1 billion floats * 4 bytes = 4 billion = overflows int.
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                try
                {
                    builder.Allocate(in root.Values, 1_000_000_000);
                    NAssert.Fail("Expected TrecsException for size overflow");
                }
                catch (TrecsException) { }
            }
        }

        // ───────────────────────────────────────────────────────────
        // Multi-array memcpy roundtrip — tighter coverage of relocatability
        // when more than one BlobArray field is patched.
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_MultiArrayMemcpy_StillResolves()
        {
            NativeBlobAllocation original;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithThreeArrays>();
                root = new RootWithThreeArrays(42);
                var ints = builder.Allocate(in root.Ints, 5);
                var floats = builder.Allocate(in root.Floats, 3);
                var bytes = builder.Allocate(in root.Bytes, 7);
                for (int i = 0; i < 5; i++)
                    ints[i] = i * 11;
                for (int i = 0; i < 3; i++)
                    floats[i] = i * 0.5f;
                for (int i = 0; i < 7; i++)
                    bytes[i] = (byte)(0xA0 + i);
                original = builder.BuildNativeBlobAllocation();
            }

            byte* copy = (byte*)
                AllocatorManager.Allocate(
                    Allocator.Persistent,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
            try
            {
                UnsafeUtility.MemCpy(copy, (void*)original.Ptr, original.AllocSize);
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithThreeArrays>(copy);
                NAssert.AreEqual(42, view.Magic);
                NAssert.AreEqual(5, view.Ints.Length);
                for (int i = 0; i < 5; i++)
                    NAssert.AreEqual(i * 11, view.Ints[i]);
                NAssert.AreEqual(3, view.Floats.Length);
                for (int i = 0; i < 3; i++)
                    NAssert.AreEqual(i * 0.5f, view.Floats[i]);
                NAssert.AreEqual(7, view.Bytes.Length);
                for (int i = 0; i < 7; i++)
                    NAssert.AreEqual(0xA0 + i, view.Bytes[i]);
            }
            finally
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    copy,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
                Free(original);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Dispose without finalize
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Dispose_WithoutBuild_DoesNotLeak()
        {
            // No NAssert; this would surface as a Unity leak warning at
            // test-runner teardown if Dispose didn't free the chunks.
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0);
                builder.Allocate(in root.Values, 100);
                // builder.Dispose() runs here via using; no Build called.
            }
        }

        // ───────────────────────────────────────────────────────────
        // Relocatable: memcpy the built blob to a new address, still resolves
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_MemcpyToNewAddress_StillResolves()
        {
            const int count = 6;

            NativeBlobAllocation original;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                root = new RootWithSingleArray(0xABCD);

                var values = builder.Allocate(in root.Values, count);
                for (int i = 0; i < count; i++)
                {
                    values[i] = i * 7;
                }

                original = builder.BuildNativeBlobAllocation();
            }

            byte* copy = (byte*)
                AllocatorManager.Allocate(
                    Allocator.Persistent,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
            try
            {
                UnsafeUtility.MemCpy(copy, (void*)original.Ptr, original.AllocSize);

                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleArray>(copy);
                NAssert.AreEqual(0xABCD, view.Header);
                NAssert.AreEqual(count, view.Values.Length);
                for (int i = 0; i < count; i++)
                {
                    NAssert.AreEqual(i * 7, view.Values[i]);
                }
            }
            finally
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    copy,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
                Free(original);
            }
        }

        // ───────────────────────────────────────────────────────────
        // BlobRef<T> — single-T relative pointer
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_BlobRef_Roundtrips()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleRef>();
                root = new RootWithSingleRef(unchecked((int)0xCAFEBABE));

                ref var payload = ref builder.Allocate(in root.Single);
                payload = new Payload(123, 4.5f);

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithSingleRef>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(unchecked((int)0xCAFEBABE), view.Header);
                NAssert.IsTrue(view.Single.IsValid, "BlobRef should be valid after Allocate");
                NAssert.AreEqual(123, view.Single.Value.A);
                NAssert.AreEqual(4.5f, view.Single.Value.B);
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_BlobRef_DefaultIsInvalid()
        {
            // A BlobRef<T> the caller never Allocate'd stays at default — IsValid
            // returns false and we do not dereference.
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithOptionalRef>();
                root = new RootWithOptionalRef(7);
                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithOptionalRef>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(7, view.Tag);
                NAssert.IsFalse(view.Maybe.IsValid, "Unallocated BlobRef should report invalid");
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_BlobRef_DereferencingInvalidThrows()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithOptionalRef>();
                root = new RootWithOptionalRef(0);
                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithOptionalRef>(
                    (void*)alloc.Ptr
                );
                // Inline try/catch: BlobRef.Value is a ref-returning property and
                // a lambda can't bind it (the underlying struct is also non-copyable).
                try
                {
                    // Touch the value to force the assert. Reading into a local
                    // would be a NonCopyable copy of Payload, but Payload itself
                    // is plain — so this compiles and dereferences directly.
                    ref readonly var _ = ref view.Maybe.Value;
                    NAssert.Fail("Expected TrecsException dereferencing invalid BlobRef");
                }
                catch (TrecsException) { }
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_BlobRef_DoesNotOverwriteAdjacentField()
        {
            // Dead Path A regression: BlobArray<T>'s patch writes both offset
            // and length (length at OffsetPtr + 4). BlobRef<T>'s patch must
            // write only the 4-byte offset, otherwise the field immediately
            // following the BlobRef would be clobbered with the length.
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithRefThenField>();
                root = new RootWithRefThenField(0x55AA55AA);

                ref var payload = ref builder.Allocate(in root.Ref);
                payload = new Payload(42, 99f);

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithRefThenField>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(
                    0x55AA55AA,
                    view.Sentinel,
                    "Field after BlobRef was clobbered by patch — Length == 0 branch is broken"
                );
                NAssert.AreEqual(42, view.Ref.Value.A);
                NAssert.AreEqual(99f, view.Ref.Value.B);
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_BlobRef_MultipleRefsRoundtripIndependently()
        {
            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithTwoRefs>();
                root = new RootWithTwoRefs(0);

                ref var first = ref builder.Allocate(in root.First);
                first = new Payload(1, 0.1f);

                ref var second = ref builder.Allocate(in root.Second);
                second = new Payload(2, 0.2f);

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithTwoRefs>((void*)alloc.Ptr);
                NAssert.IsTrue(view.First.IsValid);
                NAssert.IsTrue(view.Second.IsValid);
                NAssert.AreEqual(1, view.First.Value.A);
                NAssert.AreEqual(0.1f, view.First.Value.B);
                NAssert.AreEqual(2, view.Second.Value.A);
                NAssert.AreEqual(0.2f, view.Second.Value.B);
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_BlobRef_MemcpyToNewAddress_StillResolves()
        {
            NativeBlobAllocation original;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleRef>();
                root = new RootWithSingleRef(0xDEAD);

                ref var payload = ref builder.Allocate(in root.Single);
                payload = new Payload(11, 22f);

                original = builder.BuildNativeBlobAllocation();
            }

            byte* copy = (byte*)
                AllocatorManager.Allocate(
                    Allocator.Persistent,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
            try
            {
                UnsafeUtility.MemCpy(copy, (void*)original.Ptr, original.AllocSize);
                ref readonly var moved = ref UnsafeUtility.AsRef<RootWithSingleRef>(copy);
                NAssert.AreEqual(0xDEAD, moved.Header);
                NAssert.IsTrue(moved.Single.IsValid);
                NAssert.AreEqual(11, moved.Single.Value.A);
                NAssert.AreEqual(22f, moved.Single.Value.B);
            }
            finally
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    copy,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
                Free(original);
            }
        }

        [Test]
        public void Allocate_BlobRef_ForeignRef_Throws()
        {
            // Same validator coverage as Allocate_ForeignBlobArray_Throws.
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootPrimitiveOnly>();
                root = new RootPrimitiveOnly(0, 0, 0);
                var foreign = default(BlobRef<Payload>);
                try
                {
                    builder.Allocate(in foreign);
                    NAssert.Fail("Expected InvalidOperationException for foreign BlobRef ref");
                }
                catch (InvalidOperationException) { }
            }
        }

        [Test]
        public void Allocate_BlobRef_AlignmentNotPowerOfTwo_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleRef>();
                root = new RootWithSingleRef(0);
                try
                {
                    builder.Allocate(in root.Single, 3);
                    NAssert.Fail("Expected TrecsException for non-power-of-two alignment");
                }
                catch (TrecsException) { }
            }
        }

        [Test]
        public void Allocate_BlobRef_AlignmentLargerThan16_Throws()
        {
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithSingleRef>();
                root = new RootWithSingleRef(0);
                try
                {
                    builder.Allocate(in root.Single, 32);
                    NAssert.Fail("Expected TrecsException for oversized alignment");
                }
                catch (TrecsException) { }
            }
        }

        // ───────────────────────────────────────────────────────────
        // Nested BlobArray<T> — BlobArray field inside an element of a
        // BlobArray. Exercises Dead Path B in BuildNativeBlobAllocation
        // (multi-chunk "advance iChunk" loop in patch resolution) when
        // forced into multiple chunks via a small chunkSize.
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_NestedBlobArray_Roundtrips()
        {
            const int regionCount = 3;
            int[] polyCounts = { 2, 4, 1 };
            int[][] vertexCounts = { new[] { 3, 4 }, new[] { 5, 3, 6, 4 }, new[] { 7 } };

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<RootWithNestedArrays>();
                root = new RootWithNestedArrays(0xABCD);

                var regions = builder.Allocate(in root.Regions, regionCount);
                for (int i = 0; i < regionCount; i++)
                {
                    regions[i] = new Region(i * 100);

                    var polys = builder.Allocate(in regions[i].Polygons, polyCounts[i]);
                    for (int j = 0; j < polyCounts[i]; j++)
                    {
                        polys[j] = new Polygon(i * 1000 + j);

                        var verts = builder.Allocate(in polys[j].Vertices, vertexCounts[i][j]);
                        for (int k = 0; k < vertexCounts[i][j]; k++)
                        {
                            verts[k] = i * 10000 + j * 100 + k;
                        }
                    }
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithNestedArrays>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(0xABCD, view.Header);
                NAssert.AreEqual(regionCount, view.Regions.Length);

                for (int i = 0; i < regionCount; i++)
                {
                    ref readonly var region = ref view.Regions[i];
                    NAssert.AreEqual(i * 100, region.RegionId);
                    NAssert.AreEqual(polyCounts[i], region.Polygons.Length);

                    for (int j = 0; j < polyCounts[i]; j++)
                    {
                        ref readonly var poly = ref region.Polygons[j];
                        NAssert.AreEqual(i * 1000 + j, poly.Id);
                        NAssert.AreEqual(vertexCounts[i][j], poly.Vertices.Length);

                        for (int k = 0; k < vertexCounts[i][j]; k++)
                        {
                            NAssert.AreEqual(
                                i * 10000 + j * 100 + k,
                                poly.Vertices[k],
                                $"Mismatch at region {i} poly {j} vert {k}"
                            );
                        }
                    }
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_NestedBlobArray_SmallChunkSize_MultiChunkPatchResolves()
        {
            // Force a multi-chunk layout so the patch-resolver's
            // "advance iChunk" loop has to step at least once. chunkSize 64 is
            // small enough that the regions array can't share chunk 0 with the
            // root, putting the nested-array offset-fields in chunks > 0.
            const int chunkSize = 64;
            const int regionCount = 4;

            NativeBlobAllocation alloc;
            using (var builder = new BlobBuilder(Allocator.Temp, chunkSize))
            {
                ref var root = ref builder.ConstructRoot<RootWithNestedArrays>();
                root = new RootWithNestedArrays(0x11);

                var regions = builder.Allocate(in root.Regions, regionCount);
                for (int i = 0; i < regionCount; i++)
                {
                    regions[i] = new Region(i);

                    var polys = builder.Allocate(in regions[i].Polygons, 2);
                    for (int j = 0; j < 2; j++)
                    {
                        polys[j] = new Polygon(i * 10 + j);

                        var verts = builder.Allocate(in polys[j].Vertices, 3);
                        for (int k = 0; k < 3; k++)
                        {
                            verts[k] = i * 100 + j * 10 + k;
                        }
                    }
                }

                alloc = builder.BuildNativeBlobAllocation();
            }

            try
            {
                ref readonly var view = ref UnsafeUtility.AsRef<RootWithNestedArrays>(
                    (void*)alloc.Ptr
                );
                NAssert.AreEqual(0x11, view.Header);
                NAssert.AreEqual(regionCount, view.Regions.Length);
                for (int i = 0; i < regionCount; i++)
                {
                    ref readonly var region = ref view.Regions[i];
                    NAssert.AreEqual(i, region.RegionId);
                    NAssert.AreEqual(2, region.Polygons.Length);
                    for (int j = 0; j < 2; j++)
                    {
                        ref readonly var poly = ref region.Polygons[j];
                        NAssert.AreEqual(i * 10 + j, poly.Id);
                        NAssert.AreEqual(3, poly.Vertices.Length);
                        for (int k = 0; k < 3; k++)
                        {
                            NAssert.AreEqual(
                                i * 100 + j * 10 + k,
                                poly.Vertices[k],
                                $"Mismatch at region {i} poly {j} vert {k}"
                            );
                        }
                    }
                }
            }
            finally
            {
                Free(alloc);
            }
        }

        [Test]
        public void Build_NestedBlobArray_MemcpyToNewAddress_StillResolves()
        {
            const int chunkSize = 64;
            NativeBlobAllocation original;
            using (var builder = new BlobBuilder(Allocator.Temp, chunkSize))
            {
                ref var root = ref builder.ConstructRoot<RootWithNestedArrays>();
                root = new RootWithNestedArrays(0xBEEF);

                var regions = builder.Allocate(in root.Regions, 2);
                for (int i = 0; i < 2; i++)
                {
                    regions[i] = new Region(i + 1);
                    var polys = builder.Allocate(in regions[i].Polygons, 2);
                    for (int j = 0; j < 2; j++)
                    {
                        polys[j] = new Polygon((i + 1) * 10 + j);
                        var verts = builder.Allocate(in polys[j].Vertices, 2);
                        verts[0] = (i + 1) * 100 + j * 10;
                        verts[1] = (i + 1) * 100 + j * 10 + 1;
                    }
                }
                original = builder.BuildNativeBlobAllocation();
            }

            byte* copy = (byte*)
                AllocatorManager.Allocate(
                    Allocator.Persistent,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
            try
            {
                UnsafeUtility.MemCpy(copy, (void*)original.Ptr, original.AllocSize);
                ref readonly var moved = ref UnsafeUtility.AsRef<RootWithNestedArrays>(copy);
                NAssert.AreEqual(unchecked((int)0xBEEF), moved.Header);
                NAssert.AreEqual(2, moved.Regions.Length);
                for (int i = 0; i < 2; i++)
                {
                    ref readonly var region = ref moved.Regions[i];
                    NAssert.AreEqual(i + 1, region.RegionId);
                    NAssert.AreEqual(2, region.Polygons.Length);
                    for (int j = 0; j < 2; j++)
                    {
                        ref readonly var poly = ref region.Polygons[j];
                        NAssert.AreEqual((i + 1) * 10 + j, poly.Id);
                        NAssert.AreEqual(2, poly.Vertices.Length);
                        NAssert.AreEqual((i + 1) * 100 + j * 10, poly.Vertices[0]);
                        NAssert.AreEqual((i + 1) * 100 + j * 10 + 1, poly.Vertices[1]);
                    }
                }
            }
            finally
            {
                AllocatorManager.Free(
                    Allocator.Persistent,
                    copy,
                    original.AllocSize,
                    original.Alignment,
                    items: 1
                );
                Free(original);
            }
        }

        // ───────────────────────────────────────────────────────────
        // Full Build<T>(world, blobId) integration through NativeSharedPtr
        // ───────────────────────────────────────────────────────────

        [Test]
        public void Build_Through_NativeSharedPtr_RoundtripsViaHeap()
        {
            const int count = 10;
            var world = new WorldBuilder().Build();
            try
            {
                world.Initialize();
                var accessor = world.CreateAccessor(AccessorRole.Unrestricted);
                var blobId = new BlobId(unchecked((long)0x12345678ABCDEF01uL));

                NativeSharedPtr<RootWithSingleArray> anchor;
                using (var builder = new BlobBuilder(Allocator.Temp))
                {
                    ref var root = ref builder.ConstructRoot<RootWithSingleArray>();
                    root = new RootWithSingleArray(0x55AA55AA);

                    var values = builder.Allocate(in root.Values, count);
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = i + 1000;
                    }

                    anchor = builder.Build<RootWithSingleArray>(accessor, blobId);
                }

                try
                {
                    NAssert.IsFalse(anchor.IsNull);
                    NAssert.AreEqual(blobId, anchor.GetBlobId(accessor));

                    ref readonly var view = ref anchor.Read(accessor).Value;
                    NAssert.AreEqual(0x55AA55AA, view.Header);
                    NAssert.AreEqual(count, view.Values.Length);
                    for (int i = 0; i < count; i++)
                    {
                        NAssert.AreEqual(i + 1000, view.Values[i]);
                    }
                }
                finally
                {
                    anchor.Dispose(accessor);
                }
            }
            finally
            {
                world.Dispose();
            }
        }
    }
}

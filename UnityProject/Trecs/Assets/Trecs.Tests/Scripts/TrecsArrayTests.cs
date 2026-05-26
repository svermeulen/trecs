using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Functional tests for <see cref="TrecsArray{T}"/>: allocation, indexing, length,
    /// foreach, lifecycle, type-hash checks, and end-to-end through
    /// <see cref="WorldAccessor"/>.
    /// </summary>
    [TestFixture]
    public class TrecsArrayTests
    {
        static NativeHeap CreateChunkStore() => new NativeHeap(TrecsLog.Default);

        // ── Allocation / lifecycle ──────────────────────────────────────

        [Test]
        public void Default_IsNull()
        {
            NAssert.IsTrue(default(TrecsArray<int>).IsNull);
        }

        [Test]
        public void Alloc_ZeroLength_ReturnsNullHandle_NoAllocation()
        {
            // Zero-length is represented as the default / null-handle form: no
            // chunk-store slot is acquired, IsNull is true, Length is 0, and the
            // indexer bounds check rejects every access. Matches default(TrecsArray<T>).
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 0);

            NAssert.IsTrue(arr.IsNull);
            NAssert.AreEqual(0, arr.Length);
            NAssert.AreEqual(0, arr.Read(chunkStore.Resolver).Length);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Alloc_WithLength_AllocatesOneSlot()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 16);

            NAssert.IsFalse(arr.IsNull);
            NAssert.AreEqual(16, arr.Length);
            NAssert.AreEqual(16, arr.Read(chunkStore.Resolver).Length);
            // Single data slot — Length lives inline on the handle, so no separate
            // header slot.
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Alloc_NegativeLength_Throws()
        {
            var chunkStore = CreateChunkStore();
            NAssert.Throws<TrecsException>(() => TrecsArray.Alloc<int>(chunkStore, -1));
            chunkStore.Dispose();
        }

        [Test]
        public void ZeroInit_AllElementsAreZero()
        {
            // Chunk-store Alloc returns zeroed memory, so a fresh TrecsArray contains
            // all-default values. Important contract — callers rely on this for
            // deterministic snapshotting and per-frame reuse patterns.
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 32);
            var read = arr.Read(chunkStore.Resolver);

            for (int i = 0; i < 32; i++)
            {
                NAssert.AreEqual(0, read[i], $"index {i} should be zero");
            }

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Dispose_FreesDataSlot()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 8);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);

            arr.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        [Test]
        public void Dispose_ZeroLength_IsNoOp()
        {
            // Zero-length arrays never acquired a slot — Dispose is a no-op.
            // Symmetrical with default(TrecsArray<T>).Dispose, which is also a no-op
            // (see Default_Dispose_IsNoOp).
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 0);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            arr.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        [Test]
        public void DisposeUnknownHandle_Throws()
        {
            var chunkStore = CreateChunkStore();
            // Length is fabricated alongside the bogus handle — Dispose's type-hash
            // check on the resolved slot fires before Length is ever consulted.
            var bogus = new TrecsArray<int>(new PtrHandle(12345), 4);
            NAssert.Throws<TrecsException>(() => bogus.Dispose(chunkStore));
            chunkStore.Dispose();
        }

        [Test]
        public void Default_Read_ReturnsEmptyView()
        {
            // default(TrecsArray<T>) is a valid empty array — Read returns a view
            // whose Length is 0 and whose indexer rejects every access via the
            // bounds check. Same semantics as Alloc<T>(_, 0).
            var chunkStore = CreateChunkStore();
            var ptr = default(TrecsArray<int>);

            var read = ptr.Read(chunkStore.Resolver);
            NAssert.AreEqual(0, read.Length);

            chunkStore.Dispose();
        }

        [Test]
        public void Default_Write_ReturnsEmptyView()
        {
            var chunkStore = CreateChunkStore();
            var ptr = default(TrecsArray<int>);

            var write = ptr.Write(chunkStore.Resolver);
            NAssert.AreEqual(0, write.Length);

            chunkStore.Dispose();
        }

        [Test]
        public void Default_Dispose_IsNoOp()
        {
            // default(TrecsArray<T>) never acquired a slot — Dispose has nothing to
            // free and silently no-ops, matching Alloc<T>(_, 0) symmetry.
            var chunkStore = CreateChunkStore();
            var ptr = default(TrecsArray<int>);

            ptr.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        [Test]
        public void Alloc_PastIntMaxByteSize_ThrowsClearly()
        {
            // T = long (8 bytes). int.MaxValue / 8 ~ 268M elements is the cap; beyond
            // that, byte size overflows int. ByteSizeOrThrow catches it with a clear
            // "TrecsArray byte size overflow" message before the chunk store sees a
            // negative size.
            var chunkStore = CreateChunkStore();
            NAssert.Throws<TrecsException>(() => TrecsArray.Alloc<long>(chunkStore, int.MaxValue));
            chunkStore.Dispose();
        }

        // ── Indexer / Length ────────────────────────────────────────────

        [Test]
        public void Write_Indexer_SetsValue()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var w = arr.Write(chunkStore.Resolver);

            w[0] = 10;
            w[1] = 20;
            w[2] = 30;
            w[3] = 40;

            NAssert.AreEqual(10, w[0]);
            NAssert.AreEqual(20, w[1]);
            NAssert.AreEqual(30, w[2]);
            NAssert.AreEqual(40, w[3]);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Read_AfterWrite_ReturnsSameValues()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var w = arr.Write(chunkStore.Resolver);
            for (int i = 0; i < 4; i++)
                w[i] = i * 11;

            var r = arr.Read(chunkStore.Resolver);
            for (int i = 0; i < 4; i++)
                NAssert.AreEqual(i * 11, r[i]);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void WriteIndexer_RefReturn_AllowsInPlaceMutation()
        {
            // ref T indexer means `arr[i]++` and `ref var x = ref arr[i]; x = ...`
            // work without a get-then-set roundtrip. Important for hot-loop ergonomics.
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 3);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = 5;
            w[0]++;
            w[1] += 100;

            ref var slot = ref w[2];
            slot = 77;

            NAssert.AreEqual(6, w[0]);
            NAssert.AreEqual(100, w[1]);
            NAssert.AreEqual(77, w[2]);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Length_ReturnsAllocLength()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 42);

            NAssert.AreEqual(42, arr.Read(chunkStore.Resolver).Length);
            NAssert.AreEqual(42, arr.Write(chunkStore.Resolver).Length);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Indexer_OutOfRange_Throws()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var r = arr.Read(chunkStore.Resolver);

            NAssert.Throws<TrecsException>(() =>
            {
                var _ = r[4];
            });
            NAssert.Throws<TrecsException>(() =>
            {
                var _ = r[-1];
            });
            NAssert.Throws<TrecsException>(() =>
            {
                var _ = r[100];
            });

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void WriteIndexer_OutOfRange_Throws()
        {
            // The write indexer has the same bounds check as the read indexer. Verify
            // explicitly since the rest of the test suite exercises in-range writes.
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);
            var w = arr.Write(chunkStore.Resolver);

            NAssert.Throws<TrecsException>(() => w[4] = 0);
            NAssert.Throws<TrecsException>(() => w[-1] = 0);
            NAssert.Throws<TrecsException>(() => w[1000] = 0);
            NAssert.Throws<TrecsException>(() =>
            {
                var _ = w[4];
            });

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Indexer_OnZeroLengthArray_AlwaysThrows()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 0);
            var r = arr.Read(chunkStore.Resolver);

            NAssert.Throws<TrecsException>(() =>
            {
                var _ = r[0];
            });

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Foreach ────────────────────────────────────────────────────

        [Test]
        public void Read_Foreach_IteratesAllElementsInOrder()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 5);
            var w = arr.Write(chunkStore.Resolver);
            for (int i = 0; i < 5; i++)
                w[i] = i * 3;

            int expected = 0;
            int seen = 0;
            foreach (var v in arr.Read(chunkStore.Resolver))
            {
                NAssert.AreEqual(expected, v);
                expected += 3;
                seen++;
            }
            NAssert.AreEqual(5, seen);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Read_Foreach_ZeroLength_NoIterations()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 0);

            int count = 0;
            foreach (var _ in arr.Read(chunkStore.Resolver))
            {
                count++;
            }
            NAssert.AreEqual(0, count);

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Type-hash safety ───────────────────────────────────────────

        [Test]
        public void TypeHashMismatch_WrongElementType_Throws()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<int>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsArray<float>(arr.Handle, arr.Length);
                bad.Read(chunkStore.Resolver);
            });

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void TypeHashMismatch_TrecsListHandleUsedAsArray_Throws()
        {
            // Cross-type confusion: a TrecsList handle's header slot is tagged
            // TypeId<TrecsList<int>>, distinct from TypeId<TrecsArray<int>>. The
            // single-slot resolve's type-hash check on TrecsArray catches it.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore);
            var bogus = new TrecsArray<int>(list.Handle, 1);

            NAssert.Throws<TrecsException>(() => bogus.Read(chunkStore.Resolver));

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void TypeHashMismatch_NativeUniquePtrHandleUsedAsArray_Throws()
        {
            // Cross-type confusion: a NativeUniquePtr<int> slot is tagged
            // TypeId<NativeUniquePtr<int>>, distinct from TypeId<TrecsArray<int>>.
            // Catches handing a unique-ptr handle to a TrecsArray API.
            var chunkStore = CreateChunkStore();
            var ptr = NativeUniquePtr.Alloc<int>(chunkStore, 42);
            var bogus = new TrecsArray<int>(ptr.Handle, 1);

            NAssert.Throws<TrecsException>(() => bogus.Read(chunkStore.Resolver));

            ptr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Layout / equality ──────────────────────────────────────────

        [Test]
        public void StructLayout_IsEightBytes()
        {
            // 4-byte PtrHandle + 4-byte inline Length. Length-on-handle is the
            // deliberate trade for collapsing two chunk-store slots (and two
            // resolves at Open time) down to one — see the TrecsArray<T> class doc.
            NAssert.AreEqual(8, Marshal.SizeOf<TrecsArray<int>>());
            NAssert.AreEqual(8, Marshal.SizeOf<TrecsArray<double>>());
        }

        [Test]
        public void Equality_SameHandle_AreEqual()
        {
            var chunkStore = CreateChunkStore();
            var a = TrecsArray.Alloc<int>(chunkStore, 4);
            var copy = new TrecsArray<int>(a.Handle, a.Length);

            NAssert.IsTrue(a.Equals(copy));
            NAssert.IsTrue(a == copy);
            NAssert.IsFalse(a != copy);
            NAssert.AreEqual(a.GetHashCode(), copy.GetHashCode());

            a.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Equality_DifferentHandles_AreNotEqual()
        {
            var chunkStore = CreateChunkStore();
            var a = TrecsArray.Alloc<int>(chunkStore, 4);
            var b = TrecsArray.Alloc<int>(chunkStore, 4);

            NAssert.IsFalse(a.Equals(b));
            NAssert.IsFalse(a == b);
            NAssert.IsTrue(a != b);

            a.Dispose(chunkStore);
            b.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Equality_AgainstNonArrayObject_IsFalse()
        {
            var chunkStore = CreateChunkStore();
            var a = TrecsArray.Alloc<int>(chunkStore, 4);

            NAssert.IsFalse(a.Equals("not an array"));
            NAssert.IsFalse(a.Equals(null));

            a.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Complex element type ───────────────────────────────────────

        struct ComplexElement : IEquatable<ComplexElement>
        {
            public int A;
            public long B;
            public float C;
            public byte D;

            public bool Equals(ComplexElement o) => A == o.A && B == o.B && C == o.C && D == o.D;
        }

        [Test]
        public void ComplexElement_RoundTrips()
        {
            var chunkStore = CreateChunkStore();
            var arr = TrecsArray.Alloc<ComplexElement>(chunkStore, 3);
            var w = arr.Write(chunkStore.Resolver);
            w[0] = new ComplexElement
            {
                A = 1,
                B = 2L,
                C = 3.5f,
                D = 4,
            };
            w[1] = new ComplexElement
            {
                A = 10,
                B = 20L,
                C = 30.5f,
                D = 40,
            };
            w[2] = new ComplexElement
            {
                A = 100,
                B = 200L,
                C = 300.5f,
                D = 200,
            };

            var r = arr.Read(chunkStore.Resolver);
            NAssert.IsTrue(
                new ComplexElement
                {
                    A = 1,
                    B = 2L,
                    C = 3.5f,
                    D = 4,
                }.Equals(r[0])
            );
            NAssert.IsTrue(
                new ComplexElement
                {
                    A = 10,
                    B = 20L,
                    C = 30.5f,
                    D = 40,
                }.Equals(r[1])
            );
            NAssert.IsTrue(
                new ComplexElement
                {
                    A = 100,
                    B = 200L,
                    C = 300.5f,
                    D = 200,
                }.Equals(r[2])
            );

            arr.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── End-to-end through WorldAccessor ─────────────────────────────

        [Test]
        public void WorldAccessor_AllocReadWriteDispose_RoundTrips()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var arr = TrecsArray.Alloc<int>(world, 8);
            NAssert.IsFalse(arr.IsNull);

            var w = arr.Write(world);
            for (int i = 0; i < 8; i++)
                w[i] = i + 100;

            var r = arr.Read(world);
            NAssert.AreEqual(8, r.Length);
            for (int i = 0; i < 8; i++)
                NAssert.AreEqual(i + 100, r[i]);

            arr.Dispose(world);
        }

        [Test]
        public void WorldAccessor_AllocFromVariableRoleAccessor_Throws()
        {
            // Alloc gates on AssertCanAllocatePersistent — a Variable-role accessor
            // (which can't allocate persistent state) must throw. Same gate as
            // TrecsList.Alloc.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var variableWorld = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableHeapTest"
            );

            NAssert.Throws<TrecsException>(() => TrecsArray.Alloc<int>(variableWorld, 4));
        }

        [Test]
        public void WorldAccessor_WriteFromVariableRoleAccessor_Works()
        {
            // Unlike TrecsList where auto-grow needs the persistent-alloc gate,
            // TrecsArrayWrite never allocates — it just gives indexed access to the
            // existing buffer. Opening from a Variable-role accessor is fine.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var unrestrictedWorld = env.Accessor;
            var variableWorld = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableHeapTest"
            );

            var arr = TrecsArray.Alloc<int>(unrestrictedWorld, 4);
            var w = arr.Write(variableWorld);
            w[0] = 1;
            w[1] = 2;
            w[2] = 3;
            w[3] = 4;
            NAssert.AreEqual(2, w[1]);

            arr.Dispose(unrestrictedWorld);
        }
    }
}

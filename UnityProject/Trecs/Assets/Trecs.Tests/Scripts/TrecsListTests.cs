using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Functional tests for <see cref="TrecsList{T}"/>: allocation, indexing, Add,
    /// growth, RemoveAt, RemoveAtSwapBack, Clear, EnsureCapacity, lifecycle, and
    /// main-thread Read/Write through the chunk store. Safety / Burst concurrency
    /// lives in <see cref="TrecsListSafetyTests"/>.
    /// </summary>
    [TestFixture]
    public class TrecsListTests
    {
        static NativeHeap CreateChunkStore() => new NativeHeap(TrecsLog.Default);

        [Test]
        public void Default_IsNull()
        {
            NAssert.IsTrue(default(TrecsList<int>).IsNull);
        }

        [Test]
        public void Alloc_ReturnsNonNullHandle_AndZeroCount()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore);

            NAssert.IsFalse(list.IsNull);
            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void AllocWithInitialCapacity_PresizesBuffer()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(16, read.Capacity);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Add_AppendsValues_AndUpdatesCount()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var write = list.Write(chunkStore.Resolver);

            write.Add(10);
            write.Add(20);
            write.Add(30);

            NAssert.AreEqual(3, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(20, write[1]);
            NAssert.AreEqual(30, write[2]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_ThenAdd_FillsAcrossGrowBoundary()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 2);

            list.EnsureCapacity(chunkStore, 100);
            var write = list.Write(chunkStore.Resolver);
            for (int i = 0; i < 100; i++)
            {
                write.Add(i);
            }

            NAssert.AreEqual(100, write.Count);
            NAssert.GreaterOrEqual(write.Capacity, 100);
            for (int i = 0; i < 100; i++)
            {
                NAssert.AreEqual(i, write[i], $"value at index {i}");
            }

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_FromZeroCapacity_AllocatesInitialBuffer()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore);
            NAssert.AreEqual(0, list.Read(chunkStore.Resolver).Capacity);

            list.EnsureCapacity(chunkStore, 1);
            var write = list.Write(chunkStore.Resolver);
            write.Add(42);
            NAssert.AreEqual(1, write.Count);
            NAssert.AreEqual(42, write[0]);
            NAssert.GreaterOrEqual(write.Capacity, 1);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Add_PastCapacity_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 2);
            var write = list.Write(chunkStore.Resolver);
            write.Add(1);
            write.Add(2);

            NAssert.Throws<TrecsException>(() => write.Add(3));

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Indexer_WritesPersist()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var write = list.Write(chunkStore.Resolver);
            write.Add(0);
            write.Add(0);
            write.Add(0);

            write[1] = 99;
            NAssert.AreEqual(99, write[1]);
            NAssert.AreEqual(0, write[0]);
            NAssert.AreEqual(0, write[2]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAt_ShiftsTailDown()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var write = list.Write(chunkStore.Resolver);
            for (int i = 0; i < 5; i++)
                write.Add(i * 10);

            write.RemoveAt(1); // remove `10`
            NAssert.AreEqual(4, write.Count);
            NAssert.AreEqual(0, write[0]);
            NAssert.AreEqual(20, write[1]);
            NAssert.AreEqual(30, write[2]);
            NAssert.AreEqual(40, write[3]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAtSwapBack_MovesLastIntoSlot()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var write = list.Write(chunkStore.Resolver);
            write.Add(10);
            write.Add(20);
            write.Add(30);
            write.Add(40);

            write.RemoveAtSwapBack(1); // remove 20, replace with 40
            NAssert.AreEqual(3, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(40, write[1]);
            NAssert.AreEqual(30, write[2]);

            // Removing the last element should just decrement.
            write.RemoveAtSwapBack(2);
            NAssert.AreEqual(2, write.Count);
            NAssert.AreEqual(10, write[0]);
            NAssert.AreEqual(40, write[1]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Clear_ResetsCount_KeepsCapacity()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var write = list.Write(chunkStore.Resolver);
            write.Add(1);
            write.Add(2);
            write.Add(3);

            write.Clear();
            NAssert.AreEqual(0, write.Count);
            NAssert.AreEqual(8, write.Capacity);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_GrowsToAtLeastTarget()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);

            list.EnsureCapacity(chunkStore, 64);
            NAssert.GreaterOrEqual(list.Read(chunkStore.Resolver).Capacity, 64);

            list.EnsureCapacity(chunkStore, 16); // no-op
            NAssert.GreaterOrEqual(list.Read(chunkStore.Resolver).Capacity, 64);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void AllocAndDispose_TracksLiveSlotsViaChunkStore()
        {
            // TrecsList ownership lives entirely in the chunk store. Verify
            // alloc/dispose by watching NumLiveAllocations there. Alloc<T>() with no
            // initial capacity uses one slot (header only); disposal frees it (and
            // any data buffer) via the two-Free pattern in TrecsList<T>.Dispose.
            var chunkStore = CreateChunkStore();
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            var a = TrecsList.Alloc<int>(chunkStore);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);

            var b = TrecsList.Alloc<float>(chunkStore);
            NAssert.AreEqual(2, chunkStore.NumLiveAllocations);

            a.Dispose(chunkStore);
            NAssert.AreEqual(1, chunkStore.NumLiveAllocations);

            b.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        [Test]
        public void Dispose_FreesHeaderAndDataBufferImmediately()
        {
            // With an initial capacity, Alloc reserves both a header slot and a
            // data buffer slot — Dispose must release both so the chunk store
            // returns to a clean state.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            NAssert.AreEqual(2, chunkStore.NumLiveAllocations);

            list.Dispose(chunkStore);
            NAssert.AreEqual(0, chunkStore.NumLiveAllocations);

            chunkStore.Dispose();
        }

        [Test]
        public void TypeHashMismatch_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore);

            NAssert.Throws<TrecsException>(() =>
            {
                var bad = new TrecsList<float>(list.Handle);
                bad.Read(chunkStore.Resolver);
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void DisposeUnknownHandle_Throws()
        {
            var chunkStore = CreateChunkStore();
            var bogus = new TrecsList<int>(new PtrHandle(12345));
            NAssert.Throws<TrecsException>(() => bogus.Dispose(chunkStore));
            chunkStore.Dispose();
        }

        [Test]
        public void ResolvingNullHandle_Throws()
        {
            var chunkStore = CreateChunkStore();
            var ptr = default(TrecsList<int>);
            NAssert.Throws<TrecsException>(() => ptr.Read(chunkStore.Resolver));
            chunkStore.Dispose();
        }

        [Test]
        public void Read_OnFreshlyAllocated_RoundTrips()
        {
            var chunkStore = CreateChunkStore();

            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var write = list.Write(chunkStore.Resolver);
            write.Add(7);

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(1, read.Count);
            NAssert.AreEqual(7, read[0]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Read_AfterMultipleAdds_RoundTrips()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(11);
            w.Add(22);

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(2, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_AcrossBucketBoundaries_PreservesContents()
        {
            // Crosses several chunk-store bucket boundaries (16→32→64→…→2048) to
            // exercise the data-slot reallocation path. Each grow performs a
            // chunkStore.Alloc + MemCpy + chunkStore.Free; the contents must survive
            // intact even though the underlying slot can land in a different bucket each
            // time.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 2);
            var write = list.Write(chunkStore.Resolver);
            write.Add(11);
            write.Add(22);

            list.EnsureCapacity(chunkStore, 500);
            write = list.Write(chunkStore.Resolver);
            for (int i = 2; i < 500; i++)
            {
                write.Add(i * 7);
            }

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(500, read.Count);
            NAssert.AreEqual(11, read[0]);
            NAssert.AreEqual(22, read[1]);
            for (int i = 2; i < 500; i++)
            {
                NAssert.AreEqual(i * 7, read[i], $"value at index {i}");
            }

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void StructLayout_IsFourBytes()
        {
            NAssert.AreEqual(4, Marshal.SizeOf<TrecsList<int>>());
            NAssert.AreEqual(4, Marshal.SizeOf<TrecsList<double>>());
        }

        // ── End-to-end through WorldAccessor ───────────────────────────────
        //
        // The tests above exercise TrecsList against a bare chunk store. These
        // walk the full World → EcsHeapAllocator → WorldAccessor → chunk store
        // chain so the wiring stays honest.

        [Test]
        public void WorldAccessor_AllocReadWriteDispose_RoundTrips()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 8);
            NAssert.IsFalse(list.IsNull);

            var write = list.Write(world);
            write.Add(1);
            write.Add(2);
            write.Add(3);

            var read = list.Read(world);
            NAssert.AreEqual(3, read.Count);
            NAssert.AreEqual(1, read[0]);
            NAssert.AreEqual(2, read[1]);
            NAssert.AreEqual(3, read[2]);

            list.Dispose(world);
        }

        [Test]
        public void WorldAccessor_AllocTrecsList_DefaultCapacityIsZero()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world);
            var read = list.Read(world);
            NAssert.AreEqual(0, read.Count);
            NAssert.AreEqual(0, read.Capacity);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Add_AutoGrows_FromZeroCapacity()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world);
            NAssert.AreEqual(0, list.Read(world).Capacity);

            var write = list.Write(world);
            for (int i = 0; i < 100; i++)
            {
                write.Add(i);
            }
            NAssert.AreEqual(100, write.Count);
            NAssert.GreaterOrEqual(write.Capacity, 100);
            for (int i = 0; i < 100; i++)
            {
                NAssert.AreEqual(i, write[i], $"value at index {i}");
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_CachedPointer_StaysValidAcrossSelfGrow()
        {
            // The managed wrapper updates its cached data pointer in place on grow,
            // so subsequent Add / indexer access on the same wrapper sees the new
            // buffer. (The native wrapper, by contrast, caches at Open time and
            // requires re-opening after a handle-side EnsureCapacity.)
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 2);
            var write = list.Write(world);
            write.Add(11);
            write.Add(22);

            // Force several grows without re-opening the wrapper.
            for (int i = 2; i < 500; i++)
            {
                write.Add(i * 7);
            }

            NAssert.AreEqual(500, write.Count);
            NAssert.AreEqual(11, write[0]);
            NAssert.AreEqual(22, write[1]);
            for (int i = 2; i < 500; i++)
            {
                NAssert.AreEqual(i * 7, write[i], $"value at index {i}");
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_EnsureCapacity_GrowsInPlace()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var write = list.Write(world);
            write.Add(1);
            write.Add(2);
            write.Add(3);

            write.EnsureCapacity(200);
            NAssert.GreaterOrEqual(write.Capacity, 200);
            NAssert.AreEqual(3, write.Count);
            NAssert.AreEqual(1, write[0]);
            NAssert.AreEqual(2, write[1]);
            NAssert.AreEqual(3, write[2]);

            // Fill past the original capacity using the same wrapper.
            for (int i = 3; i < 200; i++)
            {
                write.Add(i * 11);
            }
            NAssert.AreEqual(200, write.Count);
            for (int i = 3; i < 200; i++)
            {
                NAssert.AreEqual(i * 11, write[i], $"value at index {i}");
            }

            list.Dispose(world);
        }

        // ── Version-stamp staleness detection ─────────────────────────
        //
        // The header carries a ushort Version bumped on every data-slot reallocation.
        // Managed (ref struct) wrappers check version on every data-touching op, so any
        // managed wrapper that didn't perform the grow itself throws on next use instead
        // of dereferencing a freed buffer. This is plain integer compare — not gated on
        // ENABLE_UNITY_COLLECTIONS_CHECKS — so it holds in shipping builds.
        //
        // Native wrappers validate version at construction only (matching NativeList
        // semantics). Staleness after construction is caught by the AtomicSafetyHandle
        // at schedule time in editor builds; in release it's the caller's responsibility
        // to not mutate a collection while a native wrapper from a prior state is in use.

        [Test]
        public void NativeRead_NoPerAccessVersionCheck_MatchesNativeListSemantics()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var write = list.Write(chunkStore.Resolver);
            write.Add(7);

            var read = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(7, read[0]);

            list.EnsureCapacity(chunkStore, 64);

            // Native wrappers validate at construction only, matching NativeList
            // semantics. Per-access version/slot-alive checks are editor-only
            // (AtomicSafetyHandle). The stale read still "works" — the old data
            // pointer is freed memory, but no runtime check fires in release.
            // In real usage, the job safety walker catches this at schedule time.
            NAssert.DoesNotThrow(() =>
            {
                var _ = read.Count;
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeWrite_NoPerAccessVersionCheck_MatchesNativeListSemantics()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var write = list.Write(chunkStore.Resolver);
            write.Add(7);

            list.EnsureCapacity(chunkStore, 64);

            // Same as read: native wrappers don't per-access version check.
            NAssert.DoesNotThrow(() =>
            {
                var _ = write.Count;
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ManagedWrite_SelfGrow_DoesNotInvalidateItsOwnWrapper()
        {
            // The growing wrapper re-syncs _capturedVersion. Subsequent ops on the same
            // wrapper see the bumped version match and proceed normally.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 2);
            var write = list.Write(world);
            for (int i = 0; i < 50; i++)
            {
                write.Add(i);
            }
            NAssert.AreEqual(50, write.Count);
            for (int i = 0; i < 50; i++)
            {
                NAssert.AreEqual(i, write[i]);
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_OtherWrapperGrew_StaleWrapperThrows()
        {
            // Two write wrappers on the same list. Second one triggers a grow via
            // EnsureCapacity, which bumps the header version. First wrapper's next op
            // catches the version mismatch.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w1 = list.Write(world);
            w1.Add(1);

            var w2 = list.Write(world);
            w2.EnsureCapacity(64); // bumps version; w2 re-syncs, w1 doesn't

            // w1 is a ref struct so we can't capture it in NAssert.Throws's lambda —
            // use try/catch instead.
            bool threw = false;
            try
            {
                w1.Add(2);
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "w1 should throw on stale-version use after w2 grew");

            // w2 stays usable.
            w2.Add(2);
            NAssert.AreEqual(2, w2.Count);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_OtherWrapperAdded_StaleWrapperThrows()
        {
            // Every Add bumps version, matching List<T> enumerator semantics — even
            // without a grow, a sibling wrapper that opened earlier is invalidated.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 16);
            var w1 = list.Write(world);
            w1.Add(1);

            var w2 = list.Write(world);
            w2.Add(2); // bumps; capacity 16, no grow

            bool threw = false;
            try
            {
                w1.Add(3);
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "w1 should throw after w2.Add bumped the version");

            list.Dispose(world);
        }

        [Test]
        public void ManagedRead_OtherWrapperRemovedAt_StaleReadThrows()
        {
            // RemoveAt shifts elements down — any other wrapper holding a stale
            // interpretation of indices is invalidated.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 8);
            var w = list.Write(world);
            w.Add(10);
            w.Add(20);
            w.Add(30);

            var r = list.Read(world);
            NAssert.AreEqual(20, r[1]);

            // RemoveAt bumps version.
            var w2 = list.Write(world);
            w2.RemoveAt(0);

            bool threw = false;
            try
            {
                var _ = r[0];
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "r should throw after w2.RemoveAt bumped the version");

            list.Dispose(world);
        }

        [Test]
        public void ManagedRead_OtherWrapperCleared_StaleReadThrows()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 8);
            var w = list.Write(world);
            w.Add(42);

            var r = list.Read(world);
            NAssert.AreEqual(42, r[0]);

            var w2 = list.Write(world);
            w2.Clear();

            bool threw = false;
            try
            {
                var _ = r.Count;
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "r should throw after w2.Clear bumped the version");

            list.Dispose(world);
        }

        [Test]
        public void NativeWrite_Add_SiblingMutation_NoPerAccessCheck()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);

            var w1 = list.Write(chunkStore.Resolver);
            w1.Add(1);

            var w2 = list.Write(chunkStore.Resolver);
            w2.Add(2);

            // Native wrappers don't per-access version check. In real usage the
            // job safety walker prevents two concurrent writers at schedule time.
            NAssert.DoesNotThrow(() => w1.Add(3));

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeWrite_RemoveAt_SiblingRead_NoPerAccessCheck()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var w = list.Write(chunkStore.Resolver);
            w.Add(10);
            w.Add(20);
            w.Add(30);

            var r = list.Read(chunkStore.Resolver);

            var w2 = list.Write(chunkStore.Resolver);
            w2.RemoveAt(0);

            NAssert.DoesNotThrow(() =>
            {
                var _ = r.Count;
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeWrite_Clear_SiblingRead_NoPerAccessCheck()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(42);

            var r = list.Read(chunkStore.Resolver);

            var w2 = list.Write(chunkStore.Resolver);
            w2.Clear();

            NAssert.DoesNotThrow(() =>
            {
                var _ = r.Count;
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeWrite_SelfMutation_DoesNotInvalidateItself()
        {
            // The wrapper that performs the mutation re-syncs its captured version,
            // so subsequent ops on the same wrapper still pass the version check.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);
            var w = list.Write(chunkStore.Resolver);

            for (int i = 0; i < 16; i++)
            {
                w.Add(i);
            }
            NAssert.AreEqual(16, w.Count);
            for (int i = 0; i < 16; i++)
            {
                NAssert.AreEqual(i, w[i]);
            }

            w.RemoveAt(0);
            NAssert.AreEqual(15, w.Count);
            NAssert.AreEqual(1, w[0]);

            w.Clear();
            NAssert.AreEqual(0, w.Count);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── foreach enumeration ─────────────────────────────────────────

        [Test]
        public void ManagedRead_Foreach_VisitsAllElements()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var write = list.Write(world);
            for (int i = 0; i < 10; i++)
            {
                write.Add(i * 3);
            }

            var read = list.Read(world);
            int seen = 0;
            int sum = 0;
            foreach (var v in read)
            {
                NAssert.AreEqual(seen * 3, v, $"value at iteration {seen}");
                sum += v;
                seen++;
            }
            NAssert.AreEqual(10, seen);
            NAssert.AreEqual(0 + 3 + 6 + 9 + 12 + 15 + 18 + 21 + 24 + 27, sum);

            list.Dispose(world);
        }

        [Test]
        public void ManagedRead_Foreach_EmptyList_NoIterations()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var read = list.Read(world);
            int seen = 0;
            foreach (var _ in read)
            {
                seen++;
            }
            NAssert.AreEqual(0, seen);

            list.Dispose(world);
        }

        [Test]
        public void NativeRead_Foreach_VisitsAllElements()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 8);
            var write = list.Write(chunkStore.Resolver);
            for (int i = 0; i < 5; i++)
            {
                write.Add(i + 100);
            }

            var read = list.Read(chunkStore.Resolver);
            int seen = 0;
            foreach (var v in read)
            {
                NAssert.AreEqual(seen + 100, v);
                seen++;
            }
            NAssert.AreEqual(5, seen);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeRead_Foreach_NoPerAccessVersionCheck()
        {
            // Native wrappers don't per-access version check. The enumerator
            // uses raw pointers cached at construction, matching NativeList.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);
            var w = list.Write(chunkStore.Resolver);
            for (int i = 0; i < 5; i++)
            {
                w.Add(i);
            }

            var r = list.Read(chunkStore.Resolver);
            var iter = r.GetEnumerator();
            NAssert.IsTrue(iter.MoveNext());
            var _ = iter.Current;

            var w2 = list.Write(chunkStore.Resolver);
            w2.Add(999);

            // No throw — native enumerator doesn't re-check version.
            NAssert.DoesNotThrow(() => iter.MoveNext());

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Equality / hash ─────────────────────────────────────────────

        [Test]
        public void Equals_SameHandle_True()
        {
            var chunkStore = CreateChunkStore();
            var a = TrecsList.Alloc<int>(chunkStore, 4);
            var aliasOfA = a;

            NAssert.IsTrue(a.Equals(aliasOfA));
            NAssert.IsTrue(a == aliasOfA);
            NAssert.IsFalse(a != aliasOfA);
            NAssert.AreEqual(a.GetHashCode(), aliasOfA.GetHashCode());
            NAssert.IsTrue(a.Equals((object)aliasOfA));

            a.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Equals_DifferentHandles_False()
        {
            var chunkStore = CreateChunkStore();
            var a = TrecsList.Alloc<int>(chunkStore, 4);
            var b = TrecsList.Alloc<int>(chunkStore, 4);

            NAssert.IsFalse(a.Equals(b));
            NAssert.IsFalse(a == b);
            NAssert.IsTrue(a != b);
            NAssert.IsFalse(a.Equals("not a TrecsList"));

            a.Dispose(chunkStore);
            b.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Default-initialized handle ──────────────────────────────────

        [Test]
        public void Default_Write_Throws()
        {
            var chunkStore = CreateChunkStore();
            var bogus = default(TrecsList<int>);

            NAssert.Throws<TrecsException>(() => bogus.Write(chunkStore.Resolver));

            chunkStore.Dispose();
        }

        [Test]
        public void Default_EnsureCapacity_Throws()
        {
            var chunkStore = CreateChunkStore();
            var bogus = default(TrecsList<int>);

            NAssert.Throws<TrecsException>(() => bogus.EnsureCapacity(chunkStore, 10));

            chunkStore.Dispose();
        }

        [Test]
        public void Default_Dispose_Throws()
        {
            var chunkStore = CreateChunkStore();
            var bogus = default(TrecsList<int>);

            NAssert.Throws<TrecsException>(() => bogus.Dispose(chunkStore));

            chunkStore.Dispose();
        }

        // ── RemoveAt boundary cases ─────────────────────────────────────

        [Test]
        public void RemoveAt_FirstElement_ShiftsAllDown()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(10);
            w.Add(20);
            w.Add(30);

            w.RemoveAt(0);
            NAssert.AreEqual(2, w.Count);
            NAssert.AreEqual(20, w[0]);
            NAssert.AreEqual(30, w[1]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAt_LastElement_NoShift()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(10);
            w.Add(20);
            w.Add(30);

            w.RemoveAt(2);
            NAssert.AreEqual(2, w.Count);
            NAssert.AreEqual(10, w[0]);
            NAssert.AreEqual(20, w[1]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAt_OnlyElement_ListIsEmpty()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(42);

            w.RemoveAt(0);
            NAssert.AreEqual(0, w.Count);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void RemoveAt_OutOfRange_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(1);

            NAssert.Throws<TrecsException>(() => w.RemoveAt(1));
            NAssert.Throws<TrecsException>(() => w.RemoveAt(-1));

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Indexer_OutOfRange_Throws()
        {
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(7);

            NAssert.Throws<TrecsException>(() =>
            {
                var _ = w[1];
            });
            NAssert.Throws<TrecsException>(() =>
            {
                var _ = w[-1];
            });

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        // ── Complex element type ────────────────────────────────────────

        struct ComplexElement : IEquatable<ComplexElement>
        {
            public int A;
            public long B;
            public float C;
            public byte D;

            public bool Equals(ComplexElement o) => A == o.A && B == o.B && C == o.C && D == o.D;
        }

        [Test]
        public void ManagedWrite_FromVariableRoleAccessor_Throws()
        {
            // Write itself is gated on AssertCanMutateHeap — Variable-role
            // accessors get Read access to the heap but cannot mutate, even
            // within an existing list's capacity. The heap is simulation
            // state; mutating it at variable cadence would desync.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var unrestrictedWorld = env.Accessor;
            var variableWorld = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableHeapTest"
            );

            var list = TrecsList.Alloc<int>(unrestrictedWorld, 4);

            NAssert.Throws<TrecsException>(() => list.Write(variableWorld));

            list.Dispose(unrestrictedWorld);
        }

        [Test]
        public void NativeWrite_FromVariableRoleNativeWorldAccessor_Throws()
        {
            // Burst-job-side gate: the NativeWorldAccessor handed back by
            // ToNative() carries an AllowSimulationMutation flag derived from
            // the source accessor's role, and stamps its ChunkStoreResolver
            // with the matching _canMutateHeap bit. Both the world-flag check
            // and the resolver-flag check reject a Variable-role caller.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var unrestrictedWorld = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableNativeWorldTest"
            );

            var list = TrecsList.Alloc<int>(unrestrictedWorld, 4);
            var nativeVariable = variableAccessor.ToNative();

            // World-flag path
            NAssert.Throws<TrecsException>(() => list.Write(in nativeVariable));

            // Resolver-flag path: the resolver pulled off the same accessor
            // also carries the flag, so a job that holds the resolver alone
            // can't bypass the role check either.
            var resolver = nativeVariable.ChunkStoreResolver;
            NAssert.Throws<TrecsException>(() => list.Write(in resolver));

            list.Dispose(unrestrictedWorld);
        }

        [Test]
        public void NativeWrite_FromFixedRoleNativeWorldAccessor_Succeeds()
        {
            // Companion positive case: a Fixed-role (or Unrestricted-role)
            // NativeWorldAccessor's resolver has _canMutateHeap = 1 stamped,
            // so the Burst-job Write paths open normally.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var unrestrictedWorld = env.Accessor;

            var list = TrecsList.Alloc<int>(unrestrictedWorld, 4);
            var nativeUnrestricted = env.Accessor.ToNative();

            // Mutation through the role-permissive resolver is allowed.
            var w = list.Write(in nativeUnrestricted);
            w.Add(42);
            NAssert.AreEqual(1, w.Count);
            NAssert.AreEqual(42, w[0]);

            list.Dispose(unrestrictedWorld);
        }

        [Test]
        public void EnsureCapacity_FromVariableRoleAccessor_Throws()
        {
            // EnsureCapacity allocates a fresh data slot. Matching TrecsList.Alloc, it
            // gates on AssertCanMutateHeap — a Variable-role accessor (which
            // can't mutate the heap) must throw.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var unrestrictedWorld = env.Accessor;
            var variableWorld = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableHeapTest"
            );

            var list = TrecsList.Alloc<int>(unrestrictedWorld, 4);

            NAssert.Throws<TrecsException>(() => list.EnsureCapacity(variableWorld, 100));

            list.Dispose(unrestrictedWorld);
        }

        [Test]
        public void EnsureCapacity_PastIntMaxByteSize_ThrowsClearly()
        {
            // T = long (8 bytes). int.MaxValue / 8 ~ 268M elements is the cap.
            // Beyond that, byte size overflows int. ComputeNewCapacity catches it
            // with a clear "TrecsList capacity overflow" message before the chunk
            // store sees a negative size.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<long>(chunkStore, 4);

            NAssert.Throws<TrecsException>(() => list.EnsureCapacity(chunkStore, int.MaxValue));

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void Alloc_WithComplexUnmanagedElement_RoundTrips()
        {
            // Exercises alignment/size handling for a struct that isn't a simple int —
            // ElementSize and ElementAlign on the header are read from
            // UnsafeUtility.SizeOf/AlignOf at Alloc, and grow has to honor them.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<ComplexElement>(chunkStore, 2);
            list.EnsureCapacity(chunkStore, 50);
            var w = list.Write(chunkStore.Resolver);

            for (int i = 0; i < 50; i++)
            {
                w.Add(
                    new ComplexElement
                    {
                        A = i,
                        B = (long)i * 1_000_000_007,
                        C = i * 0.5f,
                        D = (byte)(i & 0xFF),
                    }
                );
            }

            var r = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(50, r.Count);
            for (int i = 0; i < 50; i++)
            {
                NAssert.IsTrue(
                    r[i]
                        .Equals(
                            new ComplexElement
                            {
                                A = i,
                                B = (long)i * 1_000_000_007,
                                C = i * 0.5f,
                                D = (byte)(i & 0xFF),
                            }
                        ),
                    $"element {i}"
                );
            }

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void EnsureCapacity_NoOpWhenAlreadyMet_DoesNotBumpVersion()
        {
            // EnsureCapacity returns early when minCapacity <= Capacity; that path
            // must not bump the version, otherwise any live wrapper would invalidate
            // for no reason.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 16);
            var w = list.Write(chunkStore.Resolver);
            w.Add(1);
            w.Add(2);

            list.EnsureCapacity(chunkStore, 4); // <= current capacity 16, no-op
            list.EnsureCapacity(chunkStore, 16); // exact match, also no-op

            // w must still be valid; no version bump means the captured version still matches.
            w.Add(3);
            NAssert.AreEqual(3, w.Count);
            NAssert.AreEqual(3, w[2]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void NativeWrite_IndexerAssign_DoesNotBumpVersion()
        {
            // Parity with the managed test: in-place value mutation via the ref
            // indexer doesn't bump version (the wrapper can't intercept ref
            // assignments). A read on a sibling wrapper continues to work.
            var chunkStore = CreateChunkStore();
            var list = TrecsList.Alloc<int>(chunkStore, 4);
            var w = list.Write(chunkStore.Resolver);
            w.Add(10);
            w.Add(20);

            var r = list.Read(chunkStore.Resolver);
            NAssert.AreEqual(20, r[1]);

            w[1] = 99; // in-place — no bump

            NAssert.AreEqual(99, r[1]);

            list.Dispose(chunkStore);
            chunkStore.Dispose();
        }

        [Test]
        public void ManagedRead_Foreach_ThrowsOnSiblingMutationMidIteration()
        {
            // Parity with the native test, but the managed enumerator is a ref struct
            // so we can't capture it in NAssert.Throws's lambda — use try/catch instead.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 16);
            var w = list.Write(world);
            for (int i = 0; i < 5; i++)
            {
                w.Add(i);
            }

            var r = list.Read(world);
            var iter = r.GetEnumerator();
            NAssert.IsTrue(iter.MoveNext());
            var _ = iter.Current;

            // Sibling mutation bumps version.
            var w2 = list.Write(world);
            w2.Add(999);

            bool threw = false;
            try
            {
                iter.MoveNext();
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "managed enumerator should throw on stale version");

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_IndexerAssign_DoesNotBumpVersion()
        {
            // In-place value mutation via the ref indexer doesn't bump — the wrapper
            // can't intercept ref assignments, and the new value is immediately
            // visible to any reader through the shared data buffer, so there's
            // nothing stale to invalidate.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            w.Add(10);
            w.Add(20);

            var r = list.Read(world);
            NAssert.AreEqual(20, r[1]);

            // In-place assign through ref indexer — no version bump.
            w[1] = 99;

            // r is still valid; it sees the new value through the shared buffer.
            NAssert.AreEqual(99, r[1]);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Foreach_VisitsAllElements()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            for (int i = 0; i < 10; i++)
            {
                w.Add(i * 3);
            }

            int seen = 0;
            int sum = 0;
            foreach (var v in w)
            {
                NAssert.AreEqual(seen * 3, v, $"value at iteration {seen}");
                sum += v;
                seen++;
            }
            NAssert.AreEqual(10, seen);
            NAssert.AreEqual(0 + 3 + 6 + 9 + 12 + 15 + 18 + 21 + 24 + 27, sum);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Foreach_EmptyList_NoIterations()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            int seen = 0;
            foreach (var _ in w)
            {
                seen++;
            }
            NAssert.AreEqual(0, seen);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Foreach_MutatesInPlaceViaRefCurrent()
        {
            // The whole point of GetEnumerator on the Write view: Current returns
            // ref T, so `foreach (ref var x in w) x = ...` mutates the backing
            // buffer in place without needing an indexed loop. In-place ref
            // assignment doesn't bump the version (same reasoning as the indexer
            // test above), so iteration completes cleanly.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            for (int i = 0; i < 5; i++)
            {
                w.Add(i);
            }

            foreach (ref var x in w)
            {
                x *= 10;
            }

            var r = list.Read(world);
            for (int i = 0; i < 5; i++)
            {
                NAssert.AreEqual(i * 10, r[i]);
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Foreach_ThrowsOnSiblingMutationMidIteration()
        {
            // Structural mutation through any other wrapper bumps the header's
            // version; the enumerator holds a copy of the Write view with the
            // captured version from open-time, so the next MoveNext throws via
            // CheckVersion in the indexer. Mirrors the Read-view test —
            // foreach-write is not a license to bypass List<T> enumerator semantics.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 16);
            var w = list.Write(world);
            for (int i = 0; i < 5; i++)
            {
                w.Add(i);
            }

            var iter = w.GetEnumerator();
            NAssert.IsTrue(iter.MoveNext());
            var _ = iter.Current;

            // Sibling Write mutation bumps the version.
            var w2 = list.Write(world);
            w2.Add(999);

            bool threw = false;
            try
            {
                iter.MoveNext();
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "write enumerator should throw on stale version");

            list.Dispose(world);
        }

        struct IntAscComparer : IComparer<int>
        {
            public int Compare(int x, int y) => x.CompareTo(y);
        }

        struct IntDescComparer : IComparer<int>
        {
            public int Compare(int x, int y) => y.CompareTo(x);
        }

        [Test]
        public void ManagedWrite_Sort_ReordersInPlace()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 8);
            var w = list.Write(world);
            foreach (var v in new[] { 5, 1, 4, 2, 8, 3, 7, 6 })
            {
                w.Add(v);
            }

            w.Sort(default(IntAscComparer));

            var r = list.Read(world);
            for (int i = 0; i < 8; i++)
            {
                NAssert.AreEqual(i + 1, r[i], $"index {i}");
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Sort_HonoursComparerOrdering()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            foreach (var v in new[] { 2, 7, 1, 4 })
            {
                w.Add(v);
            }

            w.Sort(default(IntDescComparer));

            var r = list.Read(world);
            NAssert.AreEqual(7, r[0]);
            NAssert.AreEqual(4, r[1]);
            NAssert.AreEqual(2, r[2]);
            NAssert.AreEqual(1, r[3]);

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Sort_BumpsVersion_InvalidatesSiblingWrapper()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);
            foreach (var v in new[] { 3, 1, 2 })
            {
                w.Add(v);
            }

            var r = list.Read(world);
            // Touch r once to confirm it's live pre-sort.
            NAssert.AreEqual(3, r[0]);

            w.Sort(default(IntAscComparer));

            bool threw = false;
            try
            {
                var _ = r[0];
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "sibling Read should throw after sort bumps version");

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Sort_Parameterless_UsesIComparable()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 8);
            var w = list.Write(world);
            foreach (var v in new[] { 5, 1, 4, 2, 8, 3, 7, 6 })
            {
                w.Add(v);
            }

            w.Sort();

            var r = list.Read(world);
            for (int i = 0; i < 8; i++)
            {
                NAssert.AreEqual(i + 1, r[i], $"index {i}");
            }

            list.Dispose(world);
        }

        [Test]
        public void ManagedWrite_Sort_EmptyList_NoOp()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var world = env.Accessor;

            var list = TrecsList.Alloc<int>(world, 4);
            var w = list.Write(world);

            w.Sort(default(IntAscComparer));
            NAssert.AreEqual(0, w.Count);

            list.Dispose(world);
        }
    }
}

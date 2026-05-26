using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Verifies the shipping-build use-after-dispose guard on every
    /// chunk-store-backed Read/Write wrapper. The wrappers capture the
    /// underlying allocation's side-table <c>Generation</c> byte at Open and
    /// re-check it on every Read/Write. That check runs unconditionally — it
    /// is the only protection against dereferencing a freed slot once
    /// <c>AtomicSafetyHandle</c> is compiled out
    /// (<c>!ENABLE_UNITY_COLLECTIONS_CHECKS</c> builds).
    ///
    /// <para><b>Test caveat.</b> In editor / dev builds, Unity's safety
    /// handle is also live; on a stale-slot access the safety check
    /// (<c>CheckRead/WriteAndThrow</c> against a released handle) will throw
    /// first, before the new generation check has a chance to fire. So in
    /// editor these tests prove the wrapper throws on stale access — without
    /// isolating <em>which</em> check tripped. The new check's behaviour in
    /// shipping is exercised by the same test paths, but cannot be observed
    /// in isolation from an editor-mode test run. The fixture is intentionally
    /// not gated on <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> so the same tests
    /// also run in a no-safety-checks build, where the new check is the only
    /// thing that can throw.</para>
    ///
    /// <para>The managed ref-struct wrappers (<see cref="TrecsListRead{T}"/>,
    /// <see cref="TrecsListWrite{T}"/>) share the same Open-time resolve
    /// path (<c>ResolveHeaderAndData</c>) and the same <c>CheckSlotAlive</c>
    /// shape; the Native* coverage below transitively exercises the field
    /// wiring. Their ref-struct nature means they can't be captured in
    /// <c>NAssert.Catch</c>/<c>NAssert.Throws</c> lambdas, so they are
    /// covered by the existing version-stamp staleness tests in
    /// <see cref="TrecsListTests"/> with the same try/catch pattern.</para>
    /// </summary>
    [TestFixture]
    public class WrapperSlotGenerationTests
    {
        static NativeHeap CreateStore() => new NativeHeap(TrecsLog.Default);

        // ─── NativeUniquePtr ─────────────────────────────────────

        [Test]
        public void NativeUniqueRead_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var ptr = NativeUniquePtr.Alloc<int>(store, 42);
            var read = ptr.Read(store.Resolver);
            NAssert.AreEqual(42, read.Value); // live: works
            ptr.Dispose(store);

            NAssert.Catch(() =>
            {
                var _ = read.Value;
            });
        }

        [Test]
        public void NativeUniqueWrite_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var ptr = NativeUniquePtr.Alloc<int>(store, 42);
            var write = ptr.Write(store.Resolver);
            write.Set(100); // live: works
            ptr.Dispose(store);

            NAssert.Catch(() => write.Set(200));
        }

        [Test]
        public void NativeUniqueRead_AfterFreeAndSlotRecycle_Throws()
        {
            // The stale wrapper points at the same side-table slot the recycled
            // alloc now occupies. The slot's Generation byte has been bumped
            // (Free → Alloc bumps by 1 on the same slot), so the captured
            // generation mismatches.
            using var store = CreateStore();

            var ptr1 = NativeUniquePtr.Alloc<int>(store, 7);
            var read = ptr1.Read(store.Resolver);
            ptr1.Dispose(store);

            // Reuse the same side-table slot via a fresh alloc.
            var ptr2 = NativeUniquePtr.Alloc<int>(store, 13);

            NAssert.Catch(() =>
            {
                var _ = read.Value;
            });

            ptr2.Dispose(store);
        }

        // ─── NativeTrecsList ─────────────────────────────────────

        [Test]
        public void NativeTrecsListRead_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var list = TrecsList.Alloc<int>(store, 4);
            var w = list.Write(store.Resolver);
            w.Add(99);

            var read = list.Read(store.Resolver);
            NAssert.AreEqual(99, read[0]); // live: works
            list.Dispose(store);

            NAssert.Catch(() =>
            {
                var _ = read[0];
            });
        }

        [Test]
        public void NativeTrecsListWrite_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var list = TrecsList.Alloc<int>(store, 4);
            var w = list.Write(store.Resolver);
            w.Add(99); // live: works
            list.Dispose(store);

            NAssert.Catch(() => w.Add(123));
        }

        [Test]
        public void NativeTrecsListRead_AfterFreeAndSlotRecycle_Throws()
        {
            using var store = CreateStore();

            var list1 = TrecsList.Alloc<int>(store, 4);
            var w1 = list1.Write(store.Resolver);
            w1.Add(7);
            var read = list1.Read(store.Resolver);
            list1.Dispose(store);

            // Recycle the side-table slot via a fresh list.
            var list2 = TrecsList.Alloc<int>(store, 4);

            NAssert.Catch(() =>
            {
                var _ = read[0];
            });

            list2.Dispose(store);
        }

        // ─── TrecsArray ───────────────────────────────────────────

        [Test]
        public void TrecsArrayRead_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var array = TrecsArray.Alloc<int>(store, 4);
            var w = array.Write(store.Resolver);
            w[0] = 99;

            var read = array.Read(store.Resolver);
            NAssert.AreEqual(99, read[0]);
            array.Dispose(store);

            NAssert.Catch(() =>
            {
                var _ = read[0];
            });
        }

        [Test]
        public void TrecsArrayWrite_UseAfterDispose_Throws()
        {
            using var store = CreateStore();

            var array = TrecsArray.Alloc<int>(store, 4);
            var w = array.Write(store.Resolver);
            w[0] = 99; // live: works
            array.Dispose(store);

            NAssert.Catch(() => w[0] = 123);
        }

        [Test]
        public void TrecsArrayRead_AfterFreeAndSlotRecycle_Throws()
        {
            using var store = CreateStore();

            var array1 = TrecsArray.Alloc<int>(store, 4);
            var read = array1.Read(store.Resolver);
            array1.Dispose(store);

            var array2 = TrecsArray.Alloc<int>(store, 4);

            NAssert.Catch(() =>
            {
                var _ = read[0];
            });

            array2.Dispose(store);
        }

        // ─── Generation wrap-around (the documented 1/256 hole) ───

        [Test]
        public void GenerationWrap_AfterFull256CycleSlotMatchesCapturedGeneration()
        {
            // Sanity check on the documented size/perf trade-off: the 8-bit
            // generation wraps after 256 Free→Alloc cycles on the same slot
            // (with 0 reserved for "never allocated", so a full cycle is 255
            // steps). A stale wrapper that sees the slot re-allocated exactly
            // 255 times will see the same generation again — and the new
            // check passes vacuously, since the slot is now occupied by an
            // unrelated tenant at the same generation.
            //
            // This is the 1/256 silent-corruption hole the user accepted in
            // exchange for not widening the generation field. The test
            // exercises the wrap path to confirm the captured generation
            // does come back around (rather than getting stuck or skipped),
            // not to assert any specific behaviour from the wrapper at the
            // collision — that behaviour is build-mode-dependent (editor
            // throws via the released safety handle; shipping silently reads
            // the new tenant's bytes through the cached pointer).
            using var store = CreateStore();

            // Open the first alloc and free it. _freeSideTableSlots gets a
            // single entry pointing at this slot; subsequent allocs pop it
            // back deterministically (LIFO stack with no other allocations
            // in between).
            var first = NativeUniquePtr.Alloc<int>(store, 0);
            var captured = store.ResolveEntry(first.Handle).Generation;
            first.Dispose(store);

            // 254 more alloc/free pairs on the same slot — generation cycles
            // through (captured+1)..255, wraps to 1 (skipping 0), and arrives
            // back at `captured` after 255 total steps.
            for (int i = 0; i < 254; i++)
            {
                var p = NativeUniquePtr.Alloc<int>(store, 0);
                p.Dispose(store);
            }

            // The 255th alloc puts a live slot at the same generation the
            // first alloc held.
            var collider = NativeUniquePtr.Alloc<int>(store, 999);
            var colliderGen = store.ResolveEntry(collider.Handle).Generation;
            NAssert.AreEqual(
                captured,
                colliderGen,
                "After 255 cycles the slot must return to the captured generation"
            );

            collider.Dispose(store);
        }
    }
}

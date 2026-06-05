using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Set scoped to the Alpha tag — used to assert set membership during OnRemoved.
    public struct BulkRemoveSpecSet : IEntitySet<TestAlpha> { }

    // A second set on the same (Alpha) group, used to verify that a whole-group set
    // clear walks *every* set registered for the group, not just the first.
    public struct BulkRemoveSpecSet2 : IEntitySet<TestAlpha> { }

    /// <summary>
    /// SPEC tests for the bulk-remove rework. These assert the *desired* (target)
    /// behavior, so several are expected to FAIL against current code until the
    /// rework lands — they are the implementation worklist, not a regression net.
    ///
    /// The contract we're building toward, identical for runtime removal and every
    /// bulk-remove entry point (RemoveEntitiesWithTags / RemoveAllEntitiesInGroup /
    /// RemoveAllEntities):
    ///
    ///   During an OnRemoved callback for an entity:
    ///     * its component data is still readable,
    ///     * Exists() returns false,
    ///     * it is STILL a member of its sets (removed from sets only after callbacks).
    ///   After the submit completes: it is gone from sets and queries.
    ///
    /// Plus: an entity added and bulk-removed in the SAME submit fires BOTH OnAdded
    /// and OnRemoved (no silent lifecycle drops).
    ///
    /// The in-callback set assertions use full-group / single-entity removals (no
    /// swap-back), where "still in the set" is unambiguous because slots don't shift.
    /// </summary>
    [TestFixture]
    public class BulkRemoveSpecTests
    {
        static TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<BulkRemoveSpecSet>();
                    b.AddSet<BulkRemoveSpecSet2>();
                },
                TestTemplates.SimpleAlpha
            );

        // ───────────── The OnRemoved contract, via a normal single removal ─────────────

        [Test]
        public void NormalRemoval_OnRemoved_ComponentDataReadable()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 7 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            int observed = -1;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                        observed = a.Component<TestInt>(new EntityIndex(idx.Start, g)).Read.Value
                );

            a.RemoveEntity(handle);
            a.World.Submit();
            sub.Dispose();

            NAssert.AreEqual(7, observed, "component data must be readable inside OnRemoved");
        }

        [Test]
        public void NormalRemoval_OnRemoved_ExistsIsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 7 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            bool fired = false,
                existsInCallback = true;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        fired = true;
                        existsInCallback = handle.Exists(a);
                    }
                );

            a.RemoveEntity(handle);
            a.World.Submit();
            sub.Dispose();

            NAssert.IsTrue(fired);
            NAssert.IsFalse(existsInCallback, "Exists() must be false inside OnRemoved");
        }

        [Test]
        public void NormalRemoval_OnRemoved_EntityStillInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 7 })
                .AssertComplete()
                .Handle;
            a.World.Submit();
            a.Set<BulkRemoveSpecSet>().DeferredAdd(new EntityIndex(0, group));
            a.World.Submit();

            bool fired = false,
                inSetInCallback = false;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        fired = true;
                        inSetInCallback = a.Set<BulkRemoveSpecSet>()
                            .Read.Contains(new EntityIndex(idx.Start, g));
                    }
                );

            a.RemoveEntity(handle);
            a.World.Submit();
            sub.Dispose();

            NAssert.IsTrue(fired);
            // DESIRED: the entity should still register as a set member during its own
            // OnRemoved, mirroring how its component data is still readable.
            NAssert.IsTrue(inSetInCallback, "entity should still be in its set during OnRemoved");
        }

        [Test]
        public void NormalRemoval_AfterSubmit_EntityRemovedFromSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 7 })
                .AssertComplete()
                .Handle;
            a.World.Submit();
            a.Set<BulkRemoveSpecSet>().DeferredAdd(new EntityIndex(0, group));
            a.World.Submit();

            a.RemoveEntity(handle);
            a.World.Submit();

            NAssert.AreEqual(
                0,
                a.Set<BulkRemoveSpecSet>().Read.Count,
                "set must be empty after the removal submit completes"
            );
        }

        // ───────────── Same contract must hold for bulk RemoveEntitiesWithTags ─────────────

        [Test]
        public void RemoveWithTags_OnRemoved_ExistsIsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            bool fired = false,
                existsInCallback = true;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        fired = true;
                        existsInCallback = handle.Exists(a);
                    }
                );

            a.RemoveEntitiesWithTags<TestAlpha>();
            a.World.Submit();
            sub.Dispose();

            NAssert.IsTrue(fired);
            NAssert.IsFalse(existsInCallback, "Exists() must be false inside a bulk OnRemoved");
        }

        [Test]
        public void RemoveWithTags_OnRemoved_EntityStillInSet()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.World.Submit();
            a.Set<BulkRemoveSpecSet>().DeferredAdd(new EntityIndex(0, group));
            a.World.Submit();

            bool fired = false,
                inSetInCallback = false;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        fired = true;
                        inSetInCallback = a.Set<BulkRemoveSpecSet>()
                            .Read.Contains(new EntityIndex(idx.Start, g));
                    }
                );

            a.RemoveEntitiesWithTags<TestAlpha>();
            a.World.Submit();
            sub.Dispose();

            NAssert.IsTrue(fired);
            NAssert.IsTrue(
                inSetInCallback,
                "entity should still be in its set during a bulk OnRemoved"
            );
        }

        [Test]
        public void RemoveWithTags_FiresOnRemovedForEveryEntity()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            a.World.Submit();

            int removed = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved((g, idx) => removed += idx.End - idx.Start);

            a.RemoveEntitiesWithTags<TestAlpha>();
            a.World.Submit();
            sub.Dispose();

            NAssert.AreEqual(3, removed, "OnRemoved must fire for every entity in the group");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        // ───────────── Add + bulk-remove in the SAME submit fires BOTH events ─────────────

        [Test]
        public void RemoveWithTags_AddThenRemoveSameSubmit_FiresBothAddedAndRemoved()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            int added = 0,
                removed = 0;
            var s1 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((g, idx) => added += idx.End - idx.Start);
            var s2 = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved((g, idx) => removed += idx.End - idx.Start);

            // Add and bulk-remove the same group within a single submit.
            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.RemoveEntitiesWithTags<TestAlpha>();
            a.World.Submit();
            s1.Dispose();
            s2.Dispose();

            NAssert.AreEqual(1, added, "the added entity must fire OnAdded");
            // DESIRED: once an add is submitted its OnRemoved must also fire, even when
            // a bulk removal of the same group lands in the same submit.
            NAssert.AreEqual(
                1,
                removed,
                "the added-then-bulk-removed entity must also fire OnRemoved"
            );
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        // ───────────── World.RemoveAllEntities: same OnRemoved contract ─────────────
        // (This entry point will migrate to WorldAccessor + become deferred in a later
        // step; these tests will move with it.)

        [Test]
        public void RemoveAllEntities_OnRemoved_ExistsIsFalse()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            bool fired = false,
                existsInCallback = true;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        fired = true;
                        existsInCallback = handle.Exists(a);
                    }
                );

            env.World.RemoveAllEntities();
            sub.Dispose();

            NAssert.IsTrue(fired);
            NAssert.IsFalse(
                existsInCallback,
                "Exists() must be false inside RemoveAllEntities' OnRemoved"
            );
        }

        [Test]
        public void RemoveAllEntities_FiresOnRemovedForEveryEntity()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            a.World.Submit();

            int removed = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved((g, idx) => removed += idx.End - idx.Start);

            env.World.RemoveAllEntities();
            sub.Dispose();

            NAssert.AreEqual(3, removed, "RemoveAllEntities must fire OnRemoved for every entity");
        }

        // ───────────── RemoveAllEntitiesInGroup: direct entry point ─────────────
        // Exercised directly here (the bulk tests above reach it only via
        // RemoveEntitiesWithTags).

        [Test]
        public void RemoveAllEntitiesInGroup_RemovesEveryEntityInTheGroup()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            for (int i = 0; i < 3; i++)
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            a.World.Submit();

            int removed = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved((g, idx) => removed += idx.End - idx.Start);

            a.RemoveAllEntitiesInGroup(group);
            a.World.Submit();
            sub.Dispose();

            NAssert.AreEqual(3, removed, "OnRemoved must fire for every entity in the group");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        // ───────────── RemoveAllEntities is idempotent ─────────────
        // A second RemoveAllEntities finds every group already empty and is a no-op:
        // it fires no further OnRemoved and leaves the (global-only) world untouched.

        [Test]
        public void RemoveAllEntities_CalledTwice_SecondCallIsNoOp()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 0; i < 3; i++)
                a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = i }).AssertComplete();
            a.World.Submit();

            int removed = 0;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved((g, idx) => removed += idx.End - idx.Start);

            // First call removes everything (World.RemoveAllEntities queues + submits).
            env.World.RemoveAllEntities();
            NAssert.AreEqual(3, removed, "first call removes every entity");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));

            // Second call finds every group empty: no further OnRemoved, no error.
            env.World.RemoveAllEntities();
            sub.Dispose();

            NAssert.AreEqual(3, removed, "second call is a no-op — fires no further OnRemoved");
            NAssert.AreEqual(0, a.CountEntitiesWithTags(TestTags.Alpha));
        }

        // ───────────── Whole-group set clear walks EVERY set on the group ─────────────
        // SetStore.ClearGroupFromSets loops every set registered for the group. With two
        // sets on the Alpha group, both must be cleared by a whole-group removal — not
        // just the first.

        [Test]
        public void RemoveAllEntitiesInGroup_ClearsAllSetsOnTheGroup()
        {
            using var env = CreateEnv();
            var a = env.Accessor;
            var group = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.World.Submit();

            // Put the entity in BOTH sets registered for the group.
            a.Set<BulkRemoveSpecSet>().DeferredAdd(new EntityIndex(0, group));
            a.Set<BulkRemoveSpecSet2>().DeferredAdd(new EntityIndex(0, group));
            a.World.Submit();

            NAssert.AreEqual(1, a.Set<BulkRemoveSpecSet>().Read.Count);
            NAssert.AreEqual(1, a.Set<BulkRemoveSpecSet2>().Read.Count);

            a.RemoveAllEntitiesInGroup(group);
            a.World.Submit();

            NAssert.AreEqual(
                0,
                a.Set<BulkRemoveSpecSet>().Read.Count,
                "first set must be cleared by the whole-group removal"
            );
            NAssert.AreEqual(
                0,
                a.Set<BulkRemoveSpecSet2>().Read.Count,
                "second set must also be cleared — the clear walks every set on the group"
            );
        }

        // ───────────── Shutdown guard: adds during dispose don't materialize ─────────────

        [Test]
        public void Add_FromShutdownOnRemoved_DoesNotMaterialize()
        {
            // Not a `using` env: we drive Dispose() explicitly mid-test to exercise
            // the shutdown path, then inspect what fired.
            var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(TestTags.Alpha).Set(new TestInt { Value = 1 }).AssertComplete();
            a.World.Submit();

            int addedAfterSubscribe = 0;
            bool onRemovedFired = false;

            // Subscribed after the initial add+submit, so this only counts adds that
            // materialize from here on — i.e. the doomed shutdown add (if any).
            a.Events.EntitiesWithTags(TestTags.Alpha)
                .OnAdded((g, idx) => addedAfterSubscribe += idx.End - idx.Start);
            a.Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (g, idx) =>
                    {
                        onRemovedFired = true;
                        // Attempting to add during shutdown throws in debug and is a no-op
                        // in release. Swallow the debug assert so Dispose still completes;
                        // either way no entity must materialize.
                        try
                        {
                            a.AddEntity(TestTags.Alpha)
                                .Set(new TestInt { Value = 999 })
                                .AssertComplete();
                        }
                        catch (TrecsException) { }
                    }
                );

            // Dispose runs the deferred RemoveAllEntities under the shutdown guard,
            // firing OnRemoved (where the doomed add is attempted).
            env.Dispose();

            NAssert.IsTrue(
                onRemovedFired,
                "the original entity must fire OnRemoved during shutdown"
            );
            NAssert.AreEqual(
                0,
                addedAfterSubscribe,
                "an add attempted during shutdown must never materialize or fire OnAdded"
            );
        }
    }
}

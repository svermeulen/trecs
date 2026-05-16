using System;
using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Smoke tests for the deferred-handle-free pipeline in
    /// EntitySubmitter.RemoveEntities / FinalizeDeferredHandleFrees. The
    /// pipeline relocates each removed entity's handle entry to a tail slot
    /// in the reverse-map list for the duration of the OnRemoved fan-out, so
    /// EntityIndex.ToHandle resolves to the still-valid pre-removal handle
    /// instead of throwing. After FireRemoveCallbacks returns the handle is
    /// freed for real (version bumped, returned to the free list).
    /// </summary>
    [TestFixture]
    public class OnRemovedHandleResolutionTests
    {
        [Test]
        public void OnRemoved_ToHandle_ResolvesToOriginalHandle()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var initializer = a.AddEntity(TestTags.Alpha);
            initializer.AssertComplete();
            var originalHandle = initializer.Handle;
            a.SubmitEntities();

            EntityHandle observedHandle = default;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            observedHandle = new EntityIndex(i, group).ToHandle(a);
                        }
                    }
                );

            var alphaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, alphaGroup));
            a.SubmitEntities();

            NAssert.AreEqual(
                originalHandle,
                observedHandle,
                "ToHandle inside OnRemoved should resolve to the entity's "
                    + "pre-removal handle, not throw or return a different handle."
            );
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_ToHandle_HandleInvalidatedAfterCallback()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var initializer = a.AddEntity(TestTags.Alpha);
            initializer.AssertComplete();
            a.SubmitEntities();

            EntityHandle handleCapturedInCallback = default;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        handleCapturedInCallback = new EntityIndex(indices.Start, group).ToHandle(
                            a
                        );
                    }
                );

            var alphaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, alphaGroup));
            a.SubmitEntities();

            NAssert.AreNotEqual(EntityHandle.Null, handleCapturedInCallback);
            NAssert.IsFalse(
                handleCapturedInCallback.Exists(a),
                "The handle resolved inside OnRemoved must become invalid "
                    + "once the submission completes — the entity is gone."
            );
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_ToHandle_WholeGroupRemoval()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var originalHandles = new List<EntityHandle>();
            for (int i = 0; i < 3; i++)
            {
                var initializer = a.AddEntity(TestTags.Alpha);
                initializer.AssertComplete();
                originalHandles.Add(initializer.Handle);
            }
            a.SubmitEntities();

            var observedHandles = new List<EntityHandle>();
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            observedHandles.Add(new EntityIndex(i, group).ToHandle(a));
                        }
                    }
                );

            a.RemoveEntitiesWithTags(TestTags.Alpha);
            a.SubmitEntities();

            NAssert.AreEqual(3, observedHandles.Count);
            // Order is implementation-defined (depends on swap-back layout);
            // assert the set of resolved handles matches the originals.
            CollectionAssert.AreEquivalent(originalHandles, observedHandles);
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_ToHandle_PartialGroupRemoval()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var originalHandles = new List<EntityHandle>();
            for (int i = 0; i < 5; i++)
            {
                var initializer = a.AddEntity(TestTags.Alpha);
                initializer.AssertComplete();
                originalHandles.Add(initializer.Handle);
            }
            a.SubmitEntities();

            var observedHandles = new List<EntityHandle>();
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                        {
                            observedHandles.Add(new EntityIndex(i, group).ToHandle(a));
                        }
                    }
                );

            // Remove two non-adjacent entities (indices 1 and 3) from a group
            // of 5. Exercises the case where each removed entity gets its own
            // tail slot in [newCount, originalCount) and survivors swap-back
            // into the freed low slots.
            var alphaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(1, alphaGroup));
            a.RemoveEntity(new EntityIndex(3, alphaGroup));
            a.SubmitEntities();

            NAssert.AreEqual(2, observedHandles.Count);
            CollectionAssert.AreEquivalent(
                new[] { originalHandles[1], originalHandles[3] },
                observedHandles
            );
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_ObserverThrows_SubmitterRecovers()
        {
            // Regression: an observer throw used to leave _isRunningSubmit
            // wedged (and, after the deferred-free spike, also leak captured
            // handle entries since the next RemoveEntities Clear()s the
            // cache). Asserts both that a subsequent submission proceeds
            // normally AND that the handle captured by the throwing observer
            // is properly invalidated (version-bumped) afterwards.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var initializer = a.AddEntity(TestTags.Alpha);
            initializer.AssertComplete();
            var firstHandle = initializer.Handle;
            a.SubmitEntities();

            EntityHandle handleSeenByThrowingObserver = default;
            var sub = a
                .Events.EntitiesWithTags(TestTags.Alpha)
                .OnRemoved(
                    (GroupIndex group, EntityRange indices) =>
                    {
                        handleSeenByThrowingObserver = new EntityIndex(
                            indices.Start,
                            group
                        ).ToHandle(a);
                        throw new InvalidOperationException("test: observer deliberately throws");
                    }
                );

            var alphaGroup = a.WorldInfo.GetSingleGroupWithTags(TestTags.Alpha);
            a.RemoveEntity(new EntityIndex(0, alphaGroup));
            NAssert.Throws<InvalidOperationException>(() => a.SubmitEntities());
            sub.Dispose();

            NAssert.AreEqual(
                firstHandle,
                handleSeenByThrowingObserver,
                "Observer should have seen the original handle before throwing."
            );
            NAssert.IsFalse(
                handleSeenByThrowingObserver.Exists(a),
                "Even though the observer threw, FinalizeDeferredHandleFrees must "
                    + "still run (try/finally in RemoveEntities) so the handle is "
                    + "version-bumped and no longer Exists()."
            );

            // A follow-up submission must succeed — i.e. _isRunningSubmit
            // wasn't left stuck at true by the throw.
            a.AddEntity(TestTags.Alpha).AssertComplete();
            NAssert.DoesNotThrow(() => a.SubmitEntities());
        }
    }
}

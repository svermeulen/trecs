using NUnit.Framework;
using Trecs.Internal;
using Unity.Burst;
using Unity.Jobs;
using UnityEngine.TestTools.Constraints;
using NAssert = NUnit.Framework.Assert;
using NIs = UnityEngine.TestTools.Constraints.Is;

namespace Trecs.Tests
{
    // Three independent presence/absence dims for testing 3-way coalescing.
    // 2^3 = 8 partitions, well under TRECS038's 16-partition warning.
    public struct PeBase : ITag { }

    public struct PeAlive : ITag { }

    public struct PePoisoned : ITag { }

    public struct PeOnFire : ITag { }

    public partial class PeTestEntity
        : ITemplate,
            ITagged<PeBase>,
            IPartitionedBy<PeAlive>,
            IPartitionedBy<PePoisoned>,
            IPartitionedBy<PeOnFire>
    {
        TestInt TestInt;
    }

    // Single arity-3 multi-variant dim. 3 partitions.
    public struct A3Base : ITag { }

    public struct A3State1 : ITag { }

    public struct A3State2 : ITag { }

    public struct A3State3 : ITag { }

    public partial class A3TestEntity
        : ITemplate,
            ITagged<A3Base>,
            IPartitionedBy<A3State1, A3State2, A3State3>
    {
        TestInt TestInt;
    }

    // Edge cases exercising the per-entity coalescing pipeline in
    // EntitySubmitter.ApplyTagOpToPending and FlushNativeOperations:
    // - no-op detection (toGroup == from.GroupIndex skip)
    // - same-dim mask collision detection (even for idempotent ops)
    // - dim-resolution failure modes (tag not in any dim, UnsetTag on
    //   multi-variant dim)
    // - cross-path (managed + native) ops on the same entity
    // - per-entity isolation when many entities touch the same dim
    [TestFixture]
    public class PartitionEdgeCaseTests
    {
        TestEnvironment CreateMcEnv() =>
            EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);

        TestEnvironment CreatePeEnv() =>
            EcsTestHelper.CreateEnvironment(b => { }, PeTestEntity.Template);

        TestEnvironment CreateA3Env() =>
            EcsTestHelper.CreateEnvironment(b => { }, A3TestEntity.Template);

        [Test]
        public void SetTag_AlreadyActiveVariant_IsCoalescedAsNoOp()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // Entity already in the Alive variant of the Alive/Dead dim.
            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // SetTag<McAlive> resolves to dim {McAlive, McDead}, strips McAlive,
            // adds McAlive — net TagSet unchanged. The coalesce loop sees
            // toGroup == from.GroupIndex and skips. One op on the dim is fine;
            // the mask only fires on a *second* op (next test).
            a.SetTag<McAlive>(init.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<McBase, McAlive>().Count());
            NAssert.AreEqual(1, a.Component<TestInt>(init.Handle).Read.Value);
        }

        [Test]
        public void SetTagTwiceSameVariant_StillThrows()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // Two SetTag<McAlive> calls land on the same dim — TouchedDimsMask
            // bit is set on the first call and tripped on the second. The
            // framework rejects this even though the second call would be
            // idempotent: the contract is "at most one op per dim per
            // submission", not "no net change". Catches double-bookings across
            // systems before they paper over a real conflict elsewhere.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McAlive>(idx);
            a.SetTag<McAlive>(idx);
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void SetTag_TagNotInAnyDim_Throws()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // McBase is the ITagged base, not a partition variant. The queue-time
            // path doesn't catch this (it only verifies the source group is
            // writable); FindDimContainingTag returns -1 at submission and
            // ApplyTagOpToPending throws.
            a.SetTag<McBase>(init.Handle.ToIndex(a));
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void UnsetTag_OnMultiVariantDim_Throws()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // UnsetTag is defined for arity-1 (presence/absence) dims only.
            // McAlive lives in the {McAlive, McDead} 2-variant dim, which has
            // no "absent" partition. The submitter throws because there is no
            // unambiguous destination — to switch off Alive you have to say
            // *to what*, which is what SetTag is for.
            a.UnsetTag<McAlive>(init.Handle.ToIndex(a));
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void ThreeIndependentDims_AllCoalesceIntoOneMove()
        {
            using var env = CreatePeEnv();
            var a = env.Accessor;

            // Entity starts in the all-absent partition {PeBase}.
            var init = a.AddEntity<PeBase>().Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            // One SetTag on each of three independent dims — none of them
            // conflict, so the coalesce path merges them into a single move
            // landing in the all-present partition {PeBase, PeAlive, PePoisoned,
            // PeOnFire}.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<PeAlive>(idx);
            a.SetTag<PePoisoned>(idx);
            a.SetTag<PeOnFire>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PeAlive, PePoisoned, PeOnFire>().Count()
            );
            NAssert.AreEqual(0, a.Query().WithTags<PeBase>().WithoutTags<PeAlive>().Count());
            // Component data must survive the single coalesced move.
            NAssert.AreEqual(42, a.Component<TestInt>(init.Handle).Read.Value);
        }

        [Test]
        public void ThreeIndependentDims_MixedSetAndUnset_CoalesceCorrectly()
        {
            using var env = CreatePeEnv();
            var a = env.Accessor;

            // Start fully present: PeAlive + PePoisoned + PeOnFire all set.
            // All four tags up front since the entity must be submitted before
            // we can refer to it via ToIndex.
            var init = a.AddEntity<PeBase, PeAlive, PePoisoned, PeOnFire>()
                .Set(new TestInt { Value = 99 })
                .AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PeAlive, PePoisoned, PeOnFire>().Count()
            );

            // In one submission: unset Alive, unset Poisoned, keep OnFire.
            // Coalescing must produce a single move to {PeBase, PeOnFire}.
            var idx = init.Handle.ToIndex(a);
            a.UnsetTag<PeAlive>(idx);
            a.UnsetTag<PePoisoned>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PeOnFire>().WithoutTags<PeAlive>().Count()
            );
            NAssert.AreEqual(99, a.Component<TestInt>(init.Handle).Read.Value);
        }

        [Test]
        public void ArityThreeDim_SetTagSwitchesAmongVariants()
        {
            using var env = CreateA3Env();
            var a = env.Accessor;

            // Single arity-3 dim emits 3 partitions, one per variant.
            NAssert.AreEqual(3, A3TestEntity.Template.Partitions.Count);
            NAssert.AreEqual(1, A3TestEntity.Template.Dimensions.Count);

            var init = a.AddEntity<A3Base, A3State1>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<A3Base, A3State1>().Count());

            // SetTag<State2> swaps within the dim. Same single dim, single op
            // per submission, so coalescing doesn't trip.
            a.SetTag<A3State2>(init.Handle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<A3Base, A3State2>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<A3Base, A3State1>().Count());

            // And again to the third variant.
            a.SetTag<A3State3>(init.Handle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<A3Base, A3State3>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<A3Base, A3State2>().Count());
        }

        [Test]
        public void ArityThreeDim_TwoSetTagsOnSameDim_Throws()
        {
            using var env = CreateA3Env();
            var a = env.Accessor;

            var init = a.AddEntity<A3Base, A3State1>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // Two SetTag calls targeting the same arity-3 dim — same-dim
            // conflict, independent of which variants are picked.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<A3State2>(idx);
            a.SetTag<A3State3>(idx);
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void ArityThreeDim_UnsetTagThrows()
        {
            using var env = CreateA3Env();
            var a = env.Accessor;

            var init = a.AddEntity<A3Base, A3State1>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            // UnsetTag is invalid on a multi-variant dim — same reason as the
            // arity-2 case (no "absent" partition exists). Verify the rule
            // generalizes to arity-3.
            a.UnsetTag<A3State1>(init.Handle.ToIndex(a));
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void ManagedAndNative_IndependentDims_SameEntity_Coalesce()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 7 })
                .AssertComplete();
            a.SubmitEntities();

            // Managed-side SetTag on the McPoisoned dim, native-side SetTag on
            // the McAlive/McDead dim — different dims, so coalescing merges
            // them into one move regardless of which path queued them. Both
            // _managedTagOps and _nativeMoveOperationQueue feed the same
            // pending dict.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McPoisoned>(idx);
            nativeEcs.SetTag<McDead>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<McBase, McDead, McPoisoned>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive>().Count());
            NAssert.AreEqual(7, a.Component<TestInt>(init.Handle).Read.Value);
        }

        [Test]
        public void ManagedAndNative_SameDim_SameEntity_Throws()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;
            var nativeEcs = a.ToNative();

            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 7 })
                .AssertComplete();
            a.SubmitEntities();

            // Cross-path same-dim conflict: managed SetTag<McAlive> + native
            // SetTag<McDead> both target the Alive/Dead dim. The coalescing
            // mask is path-agnostic and must catch this.
            var idx = init.Handle.ToIndex(a);
            a.SetTag<McAlive>(idx);
            nativeEcs.SetTag<McDead>(idx);
            NAssert.Throws<TrecsException>(() => a.SubmitEntities());
        }

        [Test]
        public void PerEntityIsolation_TwoEntitiesSameDim_BothMoveIndependently()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // Two entities in the same starting group both touch the same dim.
            // Pending state is keyed by EntityIndex, so each entity gets its
            // own PendingEntityChanges record and the same-dim mask never
            // crosses entities.
            var initA = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            var initB = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 2 })
                .AssertComplete();
            a.SubmitEntities();

            a.SetTag<McDead>(initA.Handle.ToIndex(a));
            a.SetTag<McDead>(initB.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(2, a.Query().WithTags<McBase, McDead>().Count());
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive>().Count());
            NAssert.AreEqual(1, a.Component<TestInt>(initA.Handle).Read.Value);
            NAssert.AreEqual(2, a.Component<TestInt>(initB.Handle).Read.Value);
        }

        [Test]
        public void PerEntityIsolation_DifferentEntitiesDifferentDims_NoCrossTalk()
        {
            using var env = CreatePeEnv();
            var a = env.Accessor;

            var initA = a.AddEntity<PeBase>().Set(new TestInt { Value = 10 }).AssertComplete();
            var initB = a.AddEntity<PeBase>().Set(new TestInt { Value = 20 }).AssertComplete();
            var initC = a.AddEntity<PeBase>().Set(new TestInt { Value = 30 }).AssertComplete();
            a.SubmitEntities();

            // Each entity gets a SetTag on a different dim. The coalescer
            // builds three independent pending records; each entity lands in
            // a distinct destination group.
            a.SetTag<PeAlive>(initA.Handle.ToIndex(a));
            a.SetTag<PePoisoned>(initB.Handle.ToIndex(a));
            a.SetTag<PeOnFire>(initC.Handle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PeAlive>().WithoutTags<PePoisoned, PeOnFire>().Count()
            );
            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PePoisoned>().WithoutTags<PeAlive, PeOnFire>().Count()
            );
            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase, PeOnFire>().WithoutTags<PeAlive, PePoisoned>().Count()
            );
        }

        [Test]
        public void CoalescedMove_AcrossThreeDims_PreservesAllComponentData()
        {
            using var env = CreatePeEnv();
            var a = env.Accessor;

            // Move an entity from one corner of the 8-partition cross-product
            // to the diagonally-opposite corner via a single coalesced move.
            // Component data must round-trip through the destination group's
            // per-component buffers.
            var init = a.AddEntity<PeBase, PeAlive, PePoisoned, PeOnFire>()
                .Set(new TestInt { Value = 1234 })
                .AssertComplete();
            a.SubmitEntities();

            var idx = init.Handle.ToIndex(a);
            a.UnsetTag<PeAlive>(idx);
            a.UnsetTag<PePoisoned>(idx);
            a.UnsetTag<PeOnFire>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<PeBase>().WithoutTags<PeAlive, PePoisoned, PeOnFire>().Count()
            );
            NAssert.AreEqual(1234, a.Component<TestInt>(init.Handle).Read.Value);
        }

        [Test]
        public void SetTagThenUnsetTagOnIndependentDims_Coalesce()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // Start with poisoned + alive. Same submission: unset poisoned,
            // swap alive→dead. Different dims, so coalescing merges them.
            var init = a.AddEntity<McBase, McAlive, McPoisoned>()
                .Set(new TestInt { Value = 5 })
                .AssertComplete();
            a.SubmitEntities();

            var idx = init.Handle.ToIndex(a);
            a.UnsetTag<McPoisoned>(idx);
            a.SetTag<McDead>(idx);
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<McBase, McDead>().WithoutTags<McPoisoned, McAlive>().Count()
            );
        }

        [Test]
        public void RepeatedSetTagOnSameDimAcrossSubmissions_OK()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // The same-dim conflict is *within a single submission*. After
            // SubmitEntities, the pending dict is cleared and the entity can
            // be touched on the same dim again next frame.
            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete();
            a.SubmitEntities();

            a.SetTag<McDead>(init.Handle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<McBase, McDead>().Count());

            a.SetTag<McAlive>(init.Handle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<McBase, McAlive>().Count());

            a.SetTag<McDead>(init.Handle.ToIndex(a));
            a.SubmitEntities();
            NAssert.AreEqual(1, a.Query().WithTags<McBase, McDead>().Count());
        }

        [Test]
        public void UnsetTag_OnAbsentPresenceDim_IsNoOp_DoesNotThrowOnSameDimMask()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // Entity already absent on McPoisoned. UnsetTag<McPoisoned> applies
            // RemoveDimensionTags (strips nothing), final TagSet unchanged,
            // toGroup == from.GroupIndex → skip. Single op on the dim, so the
            // mask gets set once but not collided.
            var init = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 5 })
                .AssertComplete();
            a.SubmitEntities();

            a.UnsetTag<McPoisoned>(init.Handle.ToIndex(a));
            // Should not throw — single op on a dim is always fine.
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query().WithTags<McBase, McAlive>().WithoutTags<McPoisoned>().Count()
            );
        }

        [Test]
        public void CoalescedMoveOnIndependentDims_PlusRemoveSameSubmission_EntityRemoved()
        {
            using var env = CreatePeEnv();
            var a = env.Accessor;

            // Three SetTag ops on independent dims would normally coalesce into
            // a single pending-record move. Then RemoveEntity should supersede
            // the entire coalesced move. The coalescer must not bypass the
            // remove-supersedes-move path that QueueNativeMoveOperations relies
            // on (it filters out fromEntityIndex's whose _entitiesRemoved set
            // contains them).
            var init = a.AddEntity<PeBase>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            var idx = init.Handle.ToIndex(a);
            a.SetTag<PeAlive>(idx);
            a.SetTag<PePoisoned>(idx);
            a.SetTag<PeOnFire>(idx);
            a.RemoveEntity(idx);
            a.SubmitEntities();

            NAssert.IsFalse(init.Handle.Exists(a), "Entity should be removed");
            NAssert.AreEqual(
                0,
                a.Query().WithTags<PeBase, PeAlive, PePoisoned, PeOnFire>().Count(),
                "Coalesced move must not race the remove and land the entity in the destination group."
            );
            NAssert.AreEqual(
                0,
                a.Query().WithTags<PeBase>().Count(),
                "Source partition should be empty too — the entity is gone, not staying behind."
            );
        }

        [Test]
        public void OnMovedCallback_QueuesSetTagOnOtherEntity_AppliedInNextIteration()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;

            // Submission cascade: SingleSubmission inside SubmitEntitiesImpl
            // iterates until no more pending structural changes (capped by
            // MaxSubmissionIterations). Observer callbacks that queue further
            // SetTag ops should be processed in the next iteration — they
            // re-enter ApplyTagOpToPending against a freshly-Recycle'd
            // _pendingChanges dict, with no leftover state from the prior
            // iteration.
            // EntityInitializer is a ref struct, so we lift .Handle (a regular
            // struct) into locals before capturing in the OnMoved lambda.
            var observedHandle = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 1 })
                .AssertComplete()
                .Handle;
            var triggeredHandle = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 2 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // OnMoved callback on the {McBase, McAlive, McPoisoned} partition
            // (where `observedHandle` will land after its SetTag<McPoisoned>)
            // fires an extra SetTag on `triggeredHandle`. That cascade must
            // complete in a following iteration of the submission loop.
            var sub = a
                .Events.EntitiesWithTags<McBase, McPoisoned>()
                .OnMoved(
                    (EntitiesMovedObserver)(
                        (fromGroup, toGroup, indices) =>
                        {
                            a.SetTag<McPoisoned>(triggeredHandle.ToIndex(a));
                        }
                    )
                );

            a.SetTag<McPoisoned>(observedHandle.ToIndex(a));
            a.SubmitEntities();

            NAssert.AreEqual(
                2,
                a.Query().WithTags<McBase, McAlive, McPoisoned>().Count(),
                "Both observed and triggered should end up poisoned — triggered via cascaded callback."
            );

            sub.Dispose();
        }

        [Test]
        public void Coalescer_AfterWarmup_DoesNotAllocateGCMemory()
        {
#if TRECS_INTERNAL_CHECKS
            // TRECS_INTERNAL_CHECKS adds diagnostic TrecsAssert.That calls with
            // boxed-arg formatting on the coalescing hot path. Those allocs
            // are the whole point of the symbol — skip the zero-GC guarantee.
            NAssert.Ignore(
                "Zero-GC guarantee is release-mode only, not TRECS_INTERNAL_CHECKS-mode"
            );
#endif
            using var env = CreateMcEnv();
            var a = env.Accessor;

            var handle = a.AddEntity<McBase, McAlive>()
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.SubmitEntities();

            // Warmup — drive every code path the measured block will hit, so
            // any one-time allocations (lazy registry warmup, pool growth,
            // jit) happen here rather than under the measurement.
            // EntityHandle (not EntityIndex) is stable across partition moves.
            // Diagnostics showed exactly one cycle is sufficient to settle
            // every allocation path on the coalesced SetTag/UnsetTag hot path.
            a.SetTag<McPoisoned>(handle);
            a.SubmitEntities();
            a.UnsetTag<McPoisoned>(handle);
            a.SubmitEntities();

            NAssert.That(
                () =>
                {
                    a.SetTag<McPoisoned>(handle);
                    a.SubmitEntities();
                    a.UnsetTag<McPoisoned>(handle);
                    a.SubmitEntities();
                },
                NIs.Not.AllocatingGCMemory(),
                "Coalesced SetTag/UnsetTag + Submit cycle should be GC-free after warmup."
            );
        }
    }

    // Burst-compatible parallel-job that fires SetTag<McPoisoned> on each entity
    // index. Schedules ScheduleParallel with batch size 1, so Unity is free to
    // dispatch entities across multiple worker threads — exercising the
    // [NativeSetThreadIndex] _threadIndex dispatch into per-thread NativeBag
    // slots that the submission coalescer later drains.
    [BurstCompile]
    struct ParallelSetPoisonedJob : IJobFor
    {
        public NativeWorldAccessor World;
        public GroupIndex SourceGroup;

        public void Execute(int index)
        {
            World.SetTag<McPoisoned>(new EntityIndex(index, SourceGroup));
        }
    }

    [BurstCompile]
    struct ParallelSetDeadJob : IJobFor
    {
        public NativeWorldAccessor World;
        public GroupIndex SourceGroup;

        public void Execute(int index)
        {
            World.SetTag<McDead>(new EntityIndex(index, SourceGroup));
        }
    }

    [TestFixture]
    public class PartitionMultiThreadJobTests
    {
        TestEnvironment CreateMcEnv() =>
            EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);

        [Test]
        public void ParallelSetTagJob_MovesAllEntities()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;
            const int count = 64;

            for (int i = 0; i < count; i++)
            {
                a.AddEntity<McBase, McAlive>().Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            var sourceGroup = a.WorldInfo.GetSingleGroupWithTags<McBase, McAlive>();
            var nativeEcs = a.ToNative();

            // batch=1 lets Unity dispatch entities across multiple workers. Even
            // when it doesn't (low-core machines, EditMode), the test exercises
            // the per-thread bag dispatch via [NativeSetThreadIndex].
            new ParallelSetPoisonedJob { World = nativeEcs, SourceGroup = sourceGroup }
                .ScheduleParallel(count, 1, default)
                .Complete();
            a.SubmitEntities();

            NAssert.AreEqual(
                count,
                a.Query().WithTags<McBase, McAlive, McPoisoned>().Count(),
                "All entities should land in the poisoned partition after the parallel job + submission."
            );
        }

        [Test]
        public void TwoParallelJobsOnIndependentDims_CoalesceAcrossBagSlots()
        {
            using var env = CreateMcEnv();
            var a = env.Accessor;
            const int count = 64;

            for (int i = 0; i < count; i++)
            {
                a.AddEntity<McBase, McAlive>().Set(new TestInt { Value = i }).AssertComplete();
            }
            a.SubmitEntities();

            var sourceGroup = a.WorldInfo.GetSingleGroupWithTags<McBase, McAlive>();
            var nativeEcs = a.ToNative();

            // Two sequential parallel jobs on independent dims. Each job may
            // place ops in different bag slots for the same entity — at
            // submission, the coalescer's pending dict (keyed by EntityIndex)
            // merges them into one move regardless of which thread queued each
            // op.
            var poisoned = new ParallelSetPoisonedJob
            {
                World = nativeEcs,
                SourceGroup = sourceGroup,
            }.ScheduleParallel(count, 1, default);

            new ParallelSetDeadJob { World = nativeEcs, SourceGroup = sourceGroup }
                .ScheduleParallel(count, 1, poisoned)
                .Complete();

            a.SubmitEntities();

            NAssert.AreEqual(
                count,
                a.Query().WithTags<McBase, McDead, McPoisoned>().Count(),
                "All entities should have both dims set; cross-thread-bag ops on the same entity must coalesce."
            );
            NAssert.AreEqual(0, a.Query().WithTags<McBase, McAlive>().Count());
        }
    }
}

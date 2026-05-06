using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    partial struct SETestAView : IAspect, IRead<TestInt> { }

    partial struct SETestBView : IAspect, IRead<TestInt> { }

    /// <summary>
    /// Integration tests for the per-parameter <c>[SingleEntity]</c> attribute. Covers:
    /// <list type="bullet">
    ///   <item>Run-once methods (no <c>[ForEachEntity]</c>): the framework hoists the
    ///     singleton aspect/component before the user method body.</item>
    ///   <item>Mix with <c>[ForEachEntity]</c>: the singleton is hoisted out of the
    ///     iteration loop and used inside the body alongside per-entity values.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public partial class SingleEntityParameterTests
    {
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(QTestEntityA.Template, QTestEntityB.Template);

        // ─── 1. Run-once method with a single aspect singleton ───────────────────

        int _runOnceAspectSeen;

        void RunOnceAspect([SingleEntity(Tag = typeof(QId3))] in SETestBView singleton)
        {
            _runOnceAspectSeen = singleton.TestInt.Value;
        }

        [Test]
        public void RunOnce_AspectSingleton_ResolvedAndPassed()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            _runOnceAspectSeen = 0;
            RunOnceAspect(a);

            NAssert.AreEqual(42, _runOnceAspectSeen);
        }

        // ─── 2. Run-once method with two aspect singletons ───────────────────────

        int _twoSingletonsSum;

        void RunOnceTwoSingletons(
            [SingleEntity(Tag = typeof(QId1))] in SETestAView a1,
            [SingleEntity(Tag = typeof(QId3))] in SETestBView a2
        )
        {
            _twoSingletonsSum = a1.TestInt.Value + a2.TestInt.Value;
        }

        [Test]
        public void RunOnce_TwoAspectSingletons_BothResolved()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 7 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 35 }).AssertComplete();
            a.SubmitEntities();

            _twoSingletonsSum = 0;
            RunOnceTwoSingletons(a);

            NAssert.AreEqual(42, _twoSingletonsSum);
        }

        // ─── 3. [ForEachEntity] aspect iteration mixed with [SingleEntity] aspect

        readonly List<int> _mixedAspectResults = new();

        [ForEachEntity(Tag = typeof(QId1))]
        void IterateWithAspectSingleton(
            in SETestAView entity,
            [SingleEntity(Tag = typeof(QId3))] in SETestBView singleton
        )
        {
            _mixedAspectResults.Add(entity.TestInt.Value + singleton.TestInt.Value);
        }

        [Test]
        public void ForEachAspect_WithSingletonAspect_HoistsAndPassesPerEntity()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 100 }).AssertComplete();
            a.SubmitEntities();

            _mixedAspectResults.Clear();
            IterateWithAspectSingleton(a);

            NAssert.AreEqual(3, _mixedAspectResults.Count);
            NAssert.AreEqual(101, _mixedAspectResults[0]);
            NAssert.AreEqual(102, _mixedAspectResults[1]);
            NAssert.AreEqual(103, _mixedAspectResults[2]);
        }

        // ─── 4. [ForEachEntity] component iteration mixed with [SingleEntity] component

        readonly List<int> _mixedComponentResults = new();

        [ForEachEntity(Tag = typeof(QId1))]
        void IterateWithComponentSingleton(
            in TestInt value,
            [SingleEntity(Tag = typeof(QId3))] in TestInt singletonValue
        )
        {
            _mixedComponentResults.Add(value.Value + singletonValue.Value);
        }

        [Test]
        public void ForEachComponents_WithSingletonComponent_HoistsAndPassesPerEntity()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 1000 }).AssertComplete();
            a.SubmitEntities();

            _mixedComponentResults.Clear();
            IterateWithComponentSingleton(a);

            NAssert.AreEqual(3, _mixedComponentResults.Count);
            NAssert.AreEqual(1001, _mixedComponentResults[0]);
            NAssert.AreEqual(1002, _mixedComponentResults[1]);
            NAssert.AreEqual(1003, _mixedComponentResults[2]);
        }

        // ─── 5. Run-once method with a component singleton ───────────────────────

        int _runOnceComponentSeen;

        void RunOnceComponent([SingleEntity(Tag = typeof(QId1))] in TestInt value)
        {
            _runOnceComponentSeen = value.Value;
        }

        [Test]
        public void RunOnce_ComponentSingleton_ResolvedAsRefReadonly()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 999 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _runOnceComponentSeen = 0;
            RunOnceComponent(a);

            NAssert.AreEqual(999, _runOnceComponentSeen);
        }

        // ─── Gap 1: multi-tag inline form ────────────────────────────────────────

        int _multiTagSeen;

        // Tags = new[] { typeof(QId2), typeof(QCatA) } resolves to QTestEntityAB
        // (which has both); QTestEntityA has QCatA but not QId2, so it must not
        // be picked up.
        void RunOnceMultiTag(
            [SingleEntity(Tags = new[] { typeof(QId2), typeof(QCatA) })] in TestInt value
        )
        {
            _multiTagSeen = value.Value;
        }

        [Test]
        public void RunOnce_MultiTagSingleton_ResolvesUniqueIntersection()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityAB.Template
            );
            var a = env.Accessor;

            // QTestEntityA shares QCatA but not QId2 — must not match.
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // QTestEntityAB matches both QId2 and QCatA — the unique singleton.
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 77 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _multiTagSeen = 0;
            RunOnceMultiTag(a);

            NAssert.AreEqual(77, _multiTagSeen);
        }

        // ─── Gap 2: write-component singleton ────────────────────────────────────

        // ref TestInt value with [SingleEntity] resolves through the same
        // hoist-then-bind path as a read singleton, but binds a writable alias
        // so mutations land back in the component buffer.
        void RunOnceComponentWrite([SingleEntity(Tag = typeof(QId3))] ref TestInt value)
        {
            value.Value += 1000;
        }

        [Test]
        public void RunOnce_ComponentSingletonWrite_MutationLandsInBuffer()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 42 }).AssertComplete();
            a.SubmitEntities();

            RunOnceComponentWrite(a);

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId3>.Value);
            var buf = a.ComponentBuffer<TestInt>(group).Read;
            NAssert.AreEqual(1042, buf[0].Value);
        }

        // ─── Gap 3: RunOnce method that mixes [SingleEntity] with [PassThroughArgument] ─

        int _passThroughSeen;

        // The generator emits a wrapper that takes the accessor plus any
        // [PassThroughArgument] params and forwards them after hoisting the
        // singleton. Caller invocation: RunOncePassThrough(a, 17).
        void RunOncePassThrough(
            [SingleEntity(Tag = typeof(QId3))] in TestInt singleton,
            [PassThroughArgument] int multiplier
        )
        {
            _passThroughSeen = singleton.Value * multiplier;
        }

        [Test]
        public void RunOnce_SingletonWithPassThroughArgument_ForwardsCustomParam()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 6 }).AssertComplete();
            a.SubmitEntities();

            _passThroughSeen = 0;
            RunOncePassThrough(a, 7);

            NAssert.AreEqual(42, _passThroughSeen);
        }
    }

    // ─── 6. [WrapAsJob] with [SingleEntity] aspect parameter ─────────────────
    //
    // The job iterates QId1 entities (each with TestInt) and adds the singleton
    // QId3-tagged entity's TestInt.Value into theirs. Aspect singleton becomes
    // a hidden NativeFactory + EntityIndex pair on the generated job; Execute(int)
    // materializes the aspect via factory.Create(index) before calling the user
    // method. Reuses the SETestBView aspect declared at the top of this file.
    partial class SingleEntityWrapAsJobAspectSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void Increment(
            ref TestInt value,
            [SingleEntity(Tag = typeof(QId3))] in SETestBView singleton
        )
        {
            value.Value += singleton.TestInt.Value;
        }

        public void Execute()
        {
            Increment();
        }
    }

    // ─── 7. [WrapAsJob] with [SingleEntity] component parameter (read) ───────
    partial class SingleEntityWrapAsJobComponentSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(QId1))]
        [WrapAsJob]
        static void Increment(
            ref TestInt value,
            [SingleEntity(Tag = typeof(QId3))] in TestInt singletonValue
        )
        {
            value.Value += singletonValue.Value;
        }

        public void Execute()
        {
            Increment();
        }
    }

    // ─── 8. [SingleEntity] on hand-written job struct fields ────────────────
    //
    // The struct iterates QId1 entities and reads a singleton QId3-tagged entity
    // via a [SingleEntity] aspect field. The generator emits a hidden hoist of
    // SingleEntityIndex() before the per-group foreach, and assigns the field on
    // each per-group job instance from buffers fetched from the singleton's group.
    partial struct SingleEntityFieldAspectJob
    {
        [SingleEntity(Tag = typeof(QId3))]
        public SETestBView Singleton;

        [ForEachEntity(Tag = typeof(QId1))]
        void Execute(ref TestInt value)
        {
            value.Value += Singleton.TestInt.Value;
        }
    }

    // Component-typed [SingleEntity] field. The field type is the wrapper
    // (NativeComponentRead<T>); access goes through .Value.
    partial struct SingleEntityFieldComponentJob
    {
        [SingleEntity(Tag = typeof(QId3))]
        public NativeComponentRead<TestInt> SingletonValue;

        [ForEachEntity(Tag = typeof(QId1))]
        void Execute(ref TestInt value)
        {
            value.Value += SingletonValue.Value.Value;
        }
    }

    [TestFixture]
    public class SingleEntityFieldOnJobStructTests
    {
        [Test]
        public void JobStruct_AspectField_ResolvesAndApplies()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityB.Template
            );
            var a = env.Accessor;
            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i + 1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 100 }).AssertComplete();
            a.SubmitEntities();

            var handle = new SingleEntityFieldAspectJob().ScheduleParallel(a);
            handle.Complete();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var buf = a.ComponentBuffer<TestInt>(group).Read;
            NAssert.AreEqual(101, buf[0].Value);
            NAssert.AreEqual(102, buf[1].Value);
            NAssert.AreEqual(103, buf[2].Value);
        }

        [Test]
        public void JobStruct_ComponentField_ResolvesAndApplies()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityB.Template
            );
            var a = env.Accessor;
            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i + 1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 1000 }).AssertComplete();
            a.SubmitEntities();

            var handle = new SingleEntityFieldComponentJob().ScheduleParallel(a);
            handle.Complete();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var buf = a.ComponentBuffer<TestInt>(group).Read;
            NAssert.AreEqual(1001, buf[0].Value);
            NAssert.AreEqual(1002, buf[1].Value);
            NAssert.AreEqual(1003, buf[2].Value);
        }
    }

    [TestFixture]
    public class SingleEntityWrapAsJobTests
    {
        [Test]
        public void WrapAsJob_AspectSingleton_AppliedToEachIteratedEntity()
        {
            var system = new SingleEntityWrapAsJobAspectSystem();
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(system),
                QTestEntityA.Template,
                QTestEntityB.Template
            );

            var a = env.Accessor;
            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i + 1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 100 }).AssertComplete();
            a.SubmitEntities();

            env.StepFixedFrames(1);

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var buf = a.ComponentBuffer<TestInt>(group).Read;
            NAssert.AreEqual(101, buf[0].Value);
            NAssert.AreEqual(102, buf[1].Value);
            NAssert.AreEqual(103, buf[2].Value);
        }

        [Test]
        public void WrapAsJob_ComponentSingleton_AppliedToEachIteratedEntity()
        {
            var system = new SingleEntityWrapAsJobComponentSystem();
            using var env = EcsTestHelper.CreateEnvironment(
                b => b.AddSystem(system),
                QTestEntityA.Template,
                QTestEntityB.Template
            );

            var a = env.Accessor;
            for (int i = 0; i < 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i + 1 })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 1000 }).AssertComplete();
            a.SubmitEntities();

            env.StepFixedFrames(1);

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var buf = a.ComponentBuffer<TestInt>(group).Read;
            NAssert.AreEqual(1001, buf[0].Value);
            NAssert.AreEqual(1002, buf[1].Value);
            NAssert.AreEqual(1003, buf[2].Value);
        }
    }
}

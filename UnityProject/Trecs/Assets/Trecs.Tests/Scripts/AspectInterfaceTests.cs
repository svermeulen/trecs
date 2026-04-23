using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Aspect interface: shared component-access contract opted in via IAspect in the base list.
    // Source gen should cascade its IRead<>/IWrite<> declarations into any concrete aspect that
    // implements it.
    public partial interface IAiInt : IAspect, IRead<TestInt> { }

    // Multi-level chain: IAiIntFloat extends IAiInt and adds IRead<TestFloat>. Components should
    // merge into any concrete aspect that implements IAiIntFloat.
    public partial interface IAiIntFloat : IAiInt, IRead<TestFloat> { }

    // Aspect interface with a write component — exercises the ref (not ref readonly) path through
    // the generated property contract when called from a generic helper.
    public partial interface IAiWriteFloat : IAspect, IWrite<TestFloat> { }

    // Concrete aspects under test.

    // 1) Implements a single aspect interface — TestInt comes from IAiInt, nothing else.
    partial struct AiCascadeSingle : IAiInt { }

    // 2) Implements a multi-level chain — both TestInt and TestFloat should be available.
    partial struct AiCascadeChain : IAiIntFloat { }

    // 3) Implements an aspect interface and declares additional components directly — both
    //    sources should merge.
    partial struct AiCascadeMerge : IAiInt, IRead<TestFloat> { }

    // 4) Writable aspect — paired with the generic helper below.
    partial struct AiWritableFloat : IAiWriteFloat { }

    // Generic helpers constrained on aspect interfaces — the whole point of the feature.
    internal static class AiHelpers
    {
        public static int ReadInt<T>(in T aspect)
            where T : IAiInt
        {
            return aspect.TestInt.Value;
        }

        public static float ReadFloat<T>(in T aspect)
            where T : IAiIntFloat
        {
            return aspect.TestFloat.Value;
        }

        public static void DoubleFloat<T>(in T aspect)
            where T : IAiWriteFloat
        {
            ref var f = ref aspect.TestFloat;
            f.Value *= 2f;
        }
    }

    [TestFixture]
    public partial class AspectInterfaceTests
    {
        TestEnvironment CreateEnv() => EcsTestHelper.CreateEnvironment(QTestEntityA.Template);

        // An aspect struct that implements only an aspect interface (and nothing else) picks up
        // the interface's IRead<> components via the generator's cascade.
        [Test]
        public void SingleInterface_CascadesRead()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 42 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var values = new List<int>();
            foreach (var v in AiCascadeSingle.Query(a).WithTags<QCatA>())
            {
                values.Add(v.TestInt.Value);
            }

            NAssert.AreEqual(1, values.Count);
            NAssert.AreEqual(42, values[0]);
        }

        // Interface extending another aspect interface should surface the components of both
        // levels on any concrete aspect that implements the chain.
        [Test]
        public void NestedInterfaceChain_CascadesAllComponents()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 7 })
                .Set(new TestFloat { Value = 3.5f })
                .AssertComplete();
            a.SubmitEntities();

            int intSum = 0;
            float floatSum = 0f;
            foreach (var v in AiCascadeChain.Query(a).WithTags<QCatA>())
            {
                intSum += v.TestInt.Value;
                floatSum += v.TestFloat.Value;
            }

            NAssert.AreEqual(7, intSum);
            NAssert.AreEqual(3.5f, floatSum);
        }

        // Components declared directly on the aspect and components inherited via an aspect
        // interface should merge cleanly (no "already contains" errors from the generator).
        [Test]
        public void InterfaceAndDirectComponents_Merge()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 11 })
                .Set(new TestFloat { Value = 1.25f })
                .AssertComplete();
            a.SubmitEntities();

            var pairs = new List<(int i, float f)>();
            foreach (var v in AiCascadeMerge.Query(a).WithTags<QCatA>())
            {
                pairs.Add((v.TestInt.Value, v.TestFloat.Value));
            }

            NAssert.AreEqual(1, pairs.Count);
            NAssert.AreEqual(11, pairs[0].i);
            NAssert.AreEqual(1.25f, pairs[0].f);
        }

        // The main payoff of aspect interfaces: a generic helper constrained on the interface
        // works against any concrete aspect that implements it.
        [Test]
        public void GenericHelper_ReadsViaInterfaceContract()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 99 })
                .Set(new TestFloat { Value = 1f })
                .AssertComplete();
            a.SubmitEntities();

            int seen = 0;
            foreach (var v in AiCascadeSingle.Query(a).WithTags<QCatA>())
            {
                seen = AiHelpers.ReadInt(v);
            }
            NAssert.AreEqual(99, seen);

            float seenFloat = 0f;
            foreach (var v in AiCascadeChain.Query(a).WithTags<QCatA>())
            {
                seenFloat = AiHelpers.ReadFloat(v);
            }
            NAssert.AreEqual(1f, seenFloat);
        }

        // Write through an aspect-interface contract: a helper with ref-returning property
        // access should actually mutate the underlying component buffer.
        [Test]
        public void GenericHelper_MutatesViaInterfaceContract()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 0 })
                .Set(new TestFloat { Value = 2.5f })
                .AssertComplete();
            a.SubmitEntities();

            foreach (var v in AiWritableFloat.Query(a).WithTags<QCatA>())
            {
                AiHelpers.DoubleFloat(v);
            }

            var after = new List<float>();
            foreach (var v in AiWritableFloat.Query(a).WithTags<QCatA>())
            {
                after.Add(v.TestFloat.Value);
            }

            NAssert.AreEqual(1, after.Count);
            NAssert.AreEqual(5f, after[0]);
        }
    }
}

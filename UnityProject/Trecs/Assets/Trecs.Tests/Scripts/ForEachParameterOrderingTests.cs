using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;
using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Verifies the order-agnostic parameter parsing for system <c>[ForEachEntity]</c>
    /// and <c>[ForEachEntity]</c> methods, including the <c>[PassThroughArgument]</c>
    /// escape hatch for params whose type would otherwise be auto-detected
    /// (<c>IEntityComponent</c>, <c>EntityIndex</c>, <c>WorldAccessor</c>).
    /// </summary>
    [TestFixture]
    public partial class ForEachParameterOrderingTests
    {
        TestEnvironment CreateEnv() => EcsTestHelper.CreateEnvironment(QTestEntityA.Template);

        // ─── 1. Custom args (primitives) interspersed with components ────────────

        readonly List<int> _interspersedResults = new();

        // Custom int BEFORE the component, then component, then custom float AFTER.
        // Validates that arbitrary param ordering compiles and that the generator
        // emits the call in declaration order.
        [ForEachEntity(Tag = typeof(QId1))]
        void RunInterspersedCustomArgs(
            [PassThroughArgument] int multiplier,
            ref TestInt value,
            [PassThroughArgument] float bias
        )
        {
            _interspersedResults.Add((int)(value.Value * multiplier + bias));
        }

        [Test]
        public void Components_CustomArgsInArbitraryPositions_AreForwardedInOrder()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            _interspersedResults.Clear();
            RunInterspersedCustomArgs(a, multiplier: 10, bias: 0.5f);

            NAssert.AreEqual(3, _interspersedResults.Count);
            NAssert.AreEqual(10, _interspersedResults[0]);
            NAssert.AreEqual(20, _interspersedResults[1]);
            NAssert.AreEqual(30, _interspersedResults[2]);
        }

        // ─── 2. EntityIndex appearing before components ──────────────────────────

        readonly List<int> _entityIndexFirstIndices = new();

        // Loop EntityIndex appears as the FIRST parameter (before any component).
        // Under the old strictly-positional rule this would have been illegal.
        [ForEachEntity(Tag = typeof(QId1))]
        void RunEntityIndexFirst(EntityIndex entityIndex, in TestInt value)
        {
            _entityIndexFirstIndices.Add(entityIndex.Index);
        }

        [Test]
        public void Components_EntityIndexBeforeComponent_IsForwardedAsLoopIndex()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 4; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            _entityIndexFirstIndices.Clear();
            RunEntityIndexFirst(a);

            NAssert.AreEqual(4, _entityIndexFirstIndices.Count);
            NAssert.AreEqual(0, _entityIndexFirstIndices[0]);
            NAssert.AreEqual(1, _entityIndexFirstIndices[1]);
            NAssert.AreEqual(2, _entityIndexFirstIndices[2]);
            NAssert.AreEqual(3, _entityIndexFirstIndices[3]);
        }

        // ─── 3. [PassThroughArgument] EntityIndex (user-supplied, not loop's) ────

        readonly List<int> _userIndexValues = new();
        readonly List<int> _loopIndexValues = new();

        // Both a loop EntityIndex AND a user-supplied EntityIndex coexist.
        // The loop one is auto-detected; the user one carries [PassThroughArgument]
        // and is forwarded by name (its value is whatever the caller passed).
        [ForEachEntity(Tag = typeof(QId1))]
        void RunWithUserSuppliedEntityIndex(
            in TestInt value,
            EntityIndex entityIndex,
            [PassThroughArgument] EntityIndex userTarget
        )
        {
            _loopIndexValues.Add(entityIndex.Index);
            _userIndexValues.Add(userTarget.Index);
        }

        [Test]
        public void Components_PassThroughEntityIndex_IsForwardedFromCaller()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 2; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            _loopIndexValues.Clear();
            _userIndexValues.Clear();

            // The user-supplied EntityIndex carries an arbitrary marker value (123).
            // GroupIndex is irrelevant for this test — we only check the .Index round-trips.
            var fakeTarget = new EntityIndex(123, default);
            RunWithUserSuppliedEntityIndex(a, fakeTarget);

            NAssert.AreEqual(2, _loopIndexValues.Count);
            NAssert.AreEqual(0, _loopIndexValues[0]);
            NAssert.AreEqual(1, _loopIndexValues[1]);

            NAssert.AreEqual(2, _userIndexValues.Count);
            NAssert.AreEqual(123, _userIndexValues[0]);
            NAssert.AreEqual(123, _userIndexValues[1]);
        }

        // ─── 4. [PassThroughArgument] WorldAccessor (alt accessor) ───────────────

        readonly List<int> _altAccessorObservedValues = new();
        bool _altAccessorIsSame;

        // Both a loop WorldAccessor (auto-detected) AND a user-supplied alternate
        // WorldAccessor (marked [PassThroughArgument]). The user's accessor is just
        // forwarded by name — we don't actually need it to differ from the loop one;
        // the test only checks that it compiles and round-trips correctly.
        [ForEachEntity(Tag = typeof(QId1))]
        void RunWithAltAccessor(
            in TestInt value,
            WorldAccessor loopAccessor,
            [PassThroughArgument] WorldAccessor altAccessor
        )
        {
            _altAccessorObservedValues.Add(value.Value);
            _altAccessorIsSame = ReferenceEquals(loopAccessor, altAccessor);
        }

        [Test]
        public void Components_PassThroughWorldAccessor_IsForwardedFromCaller()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            _altAccessorObservedValues.Clear();
            _altAccessorIsSame = false;

            // Pass the same accessor as the alt accessor — easy & sufficient to
            // verify the param round-trips through the generated overload.
            RunWithAltAccessor(a, altAccessor: a);

            NAssert.AreEqual(3, _altAccessorObservedValues.Count);
            NAssert.AreEqual(1, _altAccessorObservedValues[0]);
            NAssert.AreEqual(2, _altAccessorObservedValues[1]);
            NAssert.AreEqual(3, _altAccessorObservedValues[2]);
            NAssert.IsTrue(_altAccessorIsSame);
        }

        // ─── 5. ForEachEntity (aspect mode): aspect param NOT first in declaration order ───

        readonly List<int> _aspectAfterCustomArgResults = new();

        // The aspect parameter is no longer required to be the first parameter.
        // Here we put a custom int BEFORE the aspect.
        [ForEachEntity(Tag = typeof(QId1))]
        void RunAspectAfterCustomArg([PassThroughArgument] int offset, in QSingleTagView view)
        {
            _aspectAfterCustomArgResults.Add(view.TestInt.Value + offset);
        }

        [Test]
        public void Aspect_AspectParamAfterCustomArg_IsForwardedInOrder()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            for (int i = 1; i <= 3; i++)
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            a.SubmitEntities();

            _aspectAfterCustomArgResults.Clear();
            RunAspectAfterCustomArg(a, offset: 100);

            NAssert.AreEqual(3, _aspectAfterCustomArgResults.Count);
            NAssert.AreEqual(101, _aspectAfterCustomArgResults[0]);
            NAssert.AreEqual(102, _aspectAfterCustomArgResults[1]);
            NAssert.AreEqual(103, _aspectAfterCustomArgResults[2]);
        }
    }
}

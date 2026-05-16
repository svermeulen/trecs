using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Plain aspect (no ITagged) — tags / exclusions come from the [ForEachEntity]
    // attribute on the call-site method below.
    partial struct QForEachWithoutView : IAspect, IRead<TestInt> { }

    [TestFixture]
    public partial class ForEachWithoutAttributeTests
    {
        // Environment with the three test templates — exercises every tag-combination
        // permutation that the Without/Withouts filters care about:
        //   QTestEntityA  : QCatA only
        //   QTestEntityAB : QCatA + QCatB
        //   QTestEntityB  : QCatB only
        TestEnvironment CreateEnv() =>
            EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityAB.Template,
                QTestEntityB.Template
            );

        // ── Single Without via positional ctor ──────────────────────────────────

        [ForEachEntity(typeof(QCatA), Without = typeof(QCatB))]
        void ProcessSingleWithout(in QForEachWithoutView view)
        {
            _singleResults.Add(view.TestInt.Value);
        }

        List<int> _singleResults = new List<int>();

        [Test]
        public void ForEachEntity_AttributeWithout_ExcludesMatchingTag()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // QCatA only — should be included (no QCatB).
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // QCatA + QCatB — should be excluded by Without=QCatB.
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            // QCatB only — fails the Tags=QCatA filter regardless of Without.
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 3 }).AssertComplete();
            a.SubmitEntities();

            _singleResults.Clear();
            ProcessSingleWithout(a);

            NAssert.AreEqual(
                1,
                _singleResults.Count,
                "Only QTestEntityA (QCatA without QCatB) should match"
            );
            NAssert.AreEqual(1, _singleResults[0]);
        }

        // ── Withouts (array) ────────────────────────────────────────────────────

        [ForEachEntity(typeof(QCatA), Withouts = new[] { typeof(QCatB) })]
        void ProcessWithoutsArray(in QForEachWithoutView view)
        {
            _arrayResults.Add(view.TestInt.Value);
        }

        List<int> _arrayResults = new List<int>();

        [Test]
        public void ForEachEntity_AttributeWithoutsArray_ExcludesMatchingTags()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _arrayResults.Clear();
            ProcessWithoutsArray(a);

            NAssert.AreEqual(
                1,
                _arrayResults.Count,
                "Withouts={QCatB} should behave identically to Without=QCatB"
            );
            NAssert.AreEqual(10, _arrayResults[0]);
        }

        // ── Without with named Tags property ────────────────────────────────────

        [ForEachEntity(Tags = new[] { typeof(QCatA) }, Without = typeof(QCatB))]
        void ProcessNamedTagsWithout(in QForEachWithoutView view)
        {
            _namedResults.Add(view.TestInt.Value);
        }

        List<int> _namedResults = new List<int>();

        [Test]
        public void ForEachEntity_NamedTagsAndWithout_ExcludesMatchingTag()
        {
            // Exercises the path where the attribute uses the named `Tags` property
            // (rather than the positional ctor) alongside `Without` — both should
            // be honoured.
            using var env = CreateEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 100 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 200 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _namedResults.Clear();
            ProcessNamedTagsWithout(a);

            NAssert.AreEqual(1, _namedResults.Count);
            NAssert.AreEqual(100, _namedResults[0]);
        }

        // ── Without matches nothing ─────────────────────────────────────────────

        [ForEachEntity(typeof(QCatA), Without = typeof(QCatB))]
        void ProcessEmptyExpected(in QForEachWithoutView view)
        {
            _emptyResults.Add(view.TestInt.Value);
        }

        List<int> _emptyResults = new List<int>();

        [Test]
        public void ForEachEntity_AttributeWithout_NoMatchingEntities_IteratesZeroTimes()
        {
            using var env = CreateEnv();
            var a = env.Accessor;

            // Only add the entity that the Without should reject.
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 42 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _emptyResults.Clear();
            ProcessEmptyExpected(a);

            NAssert.AreEqual(0, _emptyResults.Count);
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Aspects local to query composition tests
    partial struct QFilterTagView : IAspect, IRead<TestInt> { }

    partial struct QComponentFilterView : IAspect, IRead<TestInt, TestFloat> { }

    partial struct QMultiTagView : IAspect, IRead<TestInt> { }

    // Aspect with negative tag matching — excludes entities with QCatB
    partial struct QWithoutTagView : IAspect, IRead<TestInt> { }

    // Aspect with negative component matching — excludes entities with TestFloat
    partial struct QWithoutCompView : IAspect, IRead<TestInt> { }

    // Aspect with multiple IRead and IWrite base interfaces
    partial struct QMultiInterfaceView
        : IAspect,
            IRead<TestInt, TestFloat>,
            IRead<TestVec>,
            IWrite<TestBool, TestShort> { }

    [TestFixture]
    public partial class QueryCompositionTests
    {
        // Environment with EntityA + sets (for set tests)
        TestEnvironment CreateSetEnv() =>
            EcsTestHelper.CreateEnvironment(b => b.AddSet<QTestSetA>(), QTestEntityA.Template);

        // Environment with EntityA + EntityB (non-overlapping tags, for MatchByComponents)
        TestEnvironment CreateComponentFilterEnv() =>
            EcsTestHelper.CreateEnvironment(QTestEntityA.Template, QTestEntityB.Template);

        // Environment with all three templates (for multi-tag, tag merging tests)
        TestEnvironment CreateMultiTagEnv() =>
            EcsTestHelper.CreateEnvironment(
                QTestEntityA.Template,
                QTestEntityAB.Template,
                QTestEntityB.Template
            );

        // Environment with all templates + sets
        TestEnvironment CreateFullEnv() =>
            EcsTestHelper.CreateEnvironment(
                b => b.AddSet<QTestSetA>(),
                QTestEntityA.Template,
                QTestEntityAB.Template,
                QTestEntityB.Template
            );

        #region Set + Tags Composition

        [Test]
        public void FilterTag_Query_RespectsTagAndFilter()
        {
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var setA = a.Set<QTestSetA>();
            setA.Write.AddImmediate(new EntityIndex(0, group));
            a.SubmitEntities(); // Flush deferred set ops

            var results = new List<int>();
            foreach (var view in QFilterTagView.Query(a).WithTags<QCatA>().InSet<QTestSetA>())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(1, results.Count);
            NAssert.AreEqual(10, results[0]);
        }

        [Test]
        public void FilterTag_Query_SkipsEntitiesNotInSet()
        {
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var setA = a.Set<QTestSetA>();
            // Only entity 1 is in the set
            setA.Write.AddImmediate(new EntityIndex(1, group));
            a.SubmitEntities(); // Flush deferred set ops

            var results = new List<int>();
            foreach (var view in QFilterTagView.Query(a).WithTags<QCatA>().InSet<QTestSetA>())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(1, results.Count);
            NAssert.AreEqual(2, results[0]);
        }

        #endregion

        #region MatchByComponents

        [Test]
        public void MatchByComponents_SkipsGroupsMissingComponents()
        {
            using var env = CreateComponentFilterEnv();
            var a = env.Accessor;

            // EntityA has TestInt + TestFloat
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 100 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            // EntityB has only TestInt — should be skipped
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 200 }).AssertComplete();
            a.SubmitEntities();

            var results = new List<int>();
            foreach (var view in QComponentFilterView.Query(a).MatchByComponents())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(
                1,
                results.Count,
                "Only group with both TestInt and TestFloat should be iterated"
            );
            NAssert.AreEqual(100, results[0]);
        }

        [Test]
        public void MatchByComponents_IncludesAllGroupsWithComponents()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // EntityA and EntityAB both have TestInt + TestFloat
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat { Value = 2.0f })
                .AssertComplete();
            a.SubmitEntities();

            var results = new List<int>();
            foreach (var view in QComponentFilterView.Query(a).MatchByComponents())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(
                2,
                results.Count,
                "Both groups with TestInt + TestFloat should be iterated"
            );
            NAssert.Contains(10, results);
            NAssert.Contains(20, results);
        }

        #endregion

        #region Multiple IHasTags

        [Test]
        public void MultiTag_Query_RequiresBothTags()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // EntityA has QCatA only
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // EntityAB has QCatA + QCatB
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            // EntityB has QCatB only (no TestFloat)
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 3 }).AssertComplete();
            a.SubmitEntities();

            var results = new List<int>();
            foreach (var view in QMultiTagView.Query(a).WithTags<QCatA, QCatB>())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(
                1,
                results.Count,
                "Only entities with BOTH QCatA and QCatB should match"
            );
            NAssert.AreEqual(2, results[0]);
        }

        #endregion

        #region ForEachEntity Attribute + Aspect Tag Merging

        // QSingleTagView no longer has IHasTags — attribute specifies both tags
        // Should only find entities with BOTH tags
        [ForEachEntity(Tags = new[] { typeof(QCatA), typeof(QCatB) })]
        void ProcessMergedTags(in QSingleTagView view)
        {
            _forEachResults.Add(view.TestInt.Value);
        }

        List<int> _forEachResults = new List<int>();

        [Test]
        public void ForEachEntity_AspectMode_MergesAttributeAndAspectTags()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // EntityA has QCatA only
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // EntityAB has QCatA + QCatB
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _forEachResults.Clear();
            ProcessMergedTags(a);

            NAssert.AreEqual(
                1,
                _forEachResults.Count,
                "Only entities with both QCatA and QCatB should match"
            );
            NAssert.AreEqual(2, _forEachResults[0]);
        }

        #endregion

        #region ForEachEntity Set

        [ForEachEntity(Tags = new[] { typeof(QCatA) }, Set = typeof(QTestSetA))]
        void ProcessFilterForEach(in QFilterTagView view)
        {
            _forEachFilterResults.Add(view.TestInt.Value);
        }

        List<int> _forEachFilterResults = new List<int>();

        [Test]
        public void ForEachEntity_AspectMode_Filter_RespectsTagsAndFilter()
        {
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);

            var setA = a.Set<QTestSetA>();
            setA.Write.AddImmediate(new EntityIndex(0, group));
            a.SubmitEntities(); // Flush deferred set ops

            _forEachFilterResults.Clear();
            ProcessFilterForEach(a);

            NAssert.AreEqual(1, _forEachFilterResults.Count);
            NAssert.AreEqual(10, _forEachFilterResults[0]);
        }

        #endregion

        #region ForEachEntity Dynamic Criteria Composition

        // Method with attribute-baked tags. Callers can pass an arbitrary
        // QueryBuilder/SparseQueryBuilder; the attribute tags are merged additively.
        [ForEachEntity(Tags = new[] { typeof(QCatA) })]
        void ProcessQCatADynamic(in QFilterTagView view)
        {
            _dynamicResults.Add(view.TestInt.Value);
        }

        List<int> _dynamicResults = new List<int>();

        [Test]
        public void ForEachEntity_AspectMode_QueryBuilderArg_MergesAttributeTags()
        {
            // Attribute has Tags=[QCatA]. Pass an extra WithTags<QCatB> at the call site.
            // Should iterate entities matching BOTH QCatA AND QCatB.
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // EntityA: QCatA only  → excluded
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // EntityAB: QCatA + QCatB → included
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _dynamicResults.Clear();
            ProcessQCatADynamic(a.Query().WithTags<QCatB>());

            NAssert.AreEqual(1, _dynamicResults.Count);
            NAssert.AreEqual(2, _dynamicResults[0]);
        }

        [Test]
        public void ForEachEntity_AspectMode_SparseQueryBuilderArg_MergesAttributeTagsWithRuntimeSet()
        {
            // Attribute has Tags=[QCatA]. Pass a SparseQueryBuilder with InSet at the call site.
            // Should iterate entities in QCatA AND in the specified set.
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var setA = a.Set<QTestSetA>();
            setA.Write.AddImmediate(new EntityIndex(0, group));
            a.SubmitEntities();

            _dynamicResults.Clear();
            ProcessQCatADynamic(a.Query().InSet<QTestSetA>());

            NAssert.AreEqual(1, _dynamicResults.Count);
            NAssert.AreEqual(10, _dynamicResults[0]);
        }

        // Method with NO attribute criteria — exercises the fully open dynamic case.
        [ForEachEntity]
        void ProcessOpen(in QFilterTagView view)
        {
            _dynamicResults.Add(view.TestInt.Value);
        }

        [Test]
        public void ForEachEntity_AspectMode_NoAttribute_QueryBuilderArg_FiltersByCallSiteTagsOnly()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _dynamicResults.Clear();
            ProcessOpen(a.Query().WithTags<QCatB>());

            // Only EntityAB (QCatB) matches.
            NAssert.AreEqual(1, _dynamicResults.Count);
            NAssert.AreEqual(2, _dynamicResults[0]);
        }

        #endregion

        #region Aspect Query with Extra Tags

        [Test]
        public void AspectQuery_ExtraTag_MergesWithAspectTags()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // EntityA has QCatA only
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            // EntityAB has QCatA + QCatB
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            // QSingleTagView no longer has IHasTags<QCatA> — tags moved to call site
            // WithTags<QCatA, QCatB> requires both — should only match entities with BOTH
            var results = new List<int>();
            foreach (var view in QSingleTagView.Query(a).WithTags<QCatA, QCatB>())
            {
                results.Add(view.TestInt.Value);
            }

            NAssert.AreEqual(
                1,
                results.Count,
                "Only entities with both QCatA and QCatB should match"
            );
            NAssert.AreEqual(2, results[0]);
        }

        #endregion

        #region Fluent Query API — WithTags

        [Test]
        public void FluentQuery_WithTags_ReturnsMatchingEntities()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var results = new List<int>();
            foreach (var ei in a.Query().WithTags<QCatA, QCatB>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count, "Only QId2 entity has both QCatA and QCatB");
            NAssert.AreEqual(2, results[0]);
        }

        [Test]
        public void FluentQuery_WithTags_Count()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 3 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(
                2,
                a.Query().WithTags<QCatA>().Count(),
                "EntityA and EntityAB both have QCatA"
            );
            NAssert.AreEqual(
                2,
                a.Query().WithTags<QCatB>().Count(),
                "EntityAB and EntityB both have QCatB"
            );
            NAssert.AreEqual(
                1,
                a.Query().WithTags<QCatA, QCatB>().Count(),
                "Only EntityAB has both"
            );
        }

        #endregion

        #region Fluent Query API — WithoutTags

        [Test]
        public void FluentQuery_WithoutTags_ExcludesMatching()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 3 }).AssertComplete();
            a.SubmitEntities();

            // QCatA without QCatB => only EntityA (has QCatA but not QCatB)
            var results = new List<int>();
            foreach (var ei in a.Query().WithTags<QCatA>().WithoutTags<QCatB>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count, "Only EntityA has QCatA without QCatB");
            NAssert.AreEqual(1, results[0]);
        }

        #endregion

        #region Fluent Query API — WithComponents / WithoutComponents

        [Test]
        public void FluentQuery_WithComponents_FiltersGroups()
        {
            using var env = CreateComponentFilterEnv();
            var a = env.Accessor;

            // EntityA has TestInt + TestFloat
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 100 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            // EntityB has only TestInt
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 200 }).AssertComplete();
            a.SubmitEntities();

            var results = new List<int>();
            foreach (var ei in a.Query().WithComponents<TestInt, TestFloat>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count, "Only EntityA group has both TestInt and TestFloat");
            NAssert.AreEqual(100, results[0]);
        }

        [Test]
        public void FluentQuery_WithoutComponents_ExcludesGroups()
        {
            using var env = CreateComponentFilterEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 100 })
                .Set(new TestFloat { Value = 1.0f })
                .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 200 }).AssertComplete();
            a.SubmitEntities();

            // Entities with TestInt but WITHOUT TestFloat => only EntityB
            var results = new List<int>();
            foreach (
                var ei in a.Query()
                    .WithComponents<TestInt>()
                    .WithoutComponents<TestFloat>()
                    .EntityIndices()
            )
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count, "Only EntityB group has TestInt without TestFloat");
            NAssert.AreEqual(200, results[0]);
        }

        #endregion

        #region Fluent Query API — InSet

        [Test]
        public void FluentQuery_InSet_ReturnsFilteredEntities()
        {
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var setA = a.Set<QTestSetA>();
            setA.Write.AddImmediate(new EntityIndex(0, group));
            a.SubmitEntities(); // Flush deferred set ops

            var results = new List<int>();
            foreach (var ei in a.Query().InSet<QTestSetA>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count);
            NAssert.AreEqual(10, results[0]);
        }

        [Test]
        public void FluentQuery_TagsAndFilter_Combined()
        {
            using var env = CreateSetEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 20 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var group = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var setA = a.Set<QTestSetA>();
            setA.Write.AddImmediate(new EntityIndex(1, group));
            a.SubmitEntities(); // Flush deferred set ops

            var results = new List<int>();
            foreach (var ei in a.Query().WithTags<QCatA>().InSet<QTestSetA>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }

            NAssert.AreEqual(1, results.Count);
            NAssert.AreEqual(20, results[0]);
        }

        #endregion

        #region Fluent Query API — Single / TrySingle

        [Test]
        public void FluentQuery_Single_ReturnsSingleEntity()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 42 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var entity = a.Query().WithTags<QCatA, QCatB>().Single();
            NAssert.AreEqual(42, entity.Get<TestInt>().Read.Value);
        }

        [Test]
        public void FluentQuery_TrySingle_ReturnsFalseWhenEmpty()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;
            // No entities added

            bool found = a.Query().WithTags<QCatA, QCatB>().TrySingle(out _);
            NAssert.IsFalse(found);
        }

        [Test]
        public void FluentQuery_TrySingle_ReturnsTrueWhenExactlyOne()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 99 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            bool found = a.Query().WithTags<QCatA, QCatB>().TrySingle(out var entity);
            NAssert.IsTrue(found);
            NAssert.AreEqual(99, entity.Get<TestInt>().Read.Value);
        }

        #endregion

        #region Fluent Query API — Groups terminal

        [Test]
        public void FluentQuery_Groups_ReturnsResolvedGroups()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            var groups = a.Query().WithTags<QCatA>().Groups();
            // Both EntityA (QCatA) and EntityAB (QCatA+QCatB) groups match
            NAssert.AreEqual(2, groups.Count);
        }

        #endregion

        #region Empty Results

        [Test]
        public void FluentQuery_WithTags_NoMatches_CountIsZero()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // No entities added
            NAssert.AreEqual(0, a.Query().WithTags<QCatA>().Count());
        }

        [Test]
        public void FluentQuery_WithTags_NoMatches_EntityIndicesEmpty()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            int count = 0;
            foreach (var _ in a.Query().WithTags<QCatA>().EntityIndices())
            {
                count++;
            }
            NAssert.AreEqual(0, count);
        }

        [Test]
        public void FluentQuery_WithTags_NoMatches_GroupSlicesEmpty()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            int count = 0;
            foreach (var _ in a.Query().WithTags<QCatA>().GroupSlices())
            {
                count++;
            }
            NAssert.AreEqual(0, count);
        }

        [Test]
        public void FluentQuery_WithoutTags_ExcludesAll_ReturnsNothing()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            // Add entity with both QCatA and QCatB
            a.AddEntity(Tag<QId2>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            // Query for QCatA without QCatA - should return nothing (contradictory but exercises code path)
            // Actually query for QCatB without QCatA should exclude EntityAB
            var results = new List<int>();
            foreach (var ei in a.Query().WithTags<QCatB>().WithoutTags<QCatA>().EntityIndices())
            {
                results.Add(a.Component<TestInt>(ei).Read.Value);
            }
            NAssert.AreEqual(
                0,
                results.Count,
                "EntityAB has both QCatA and QCatB, so WithTags<QCatB>.WithoutTags<QCatA> should exclude it"
            );
        }

        #endregion

        #region Negative Matching (WithoutTags / WithoutComponents at call site)

        [Test]
        public void AspectQuery_WithoutTags_ExcludesMatching()
        {
            // QWithoutTagView is a plain aspect (IRead<TestInt> only)
            // Query chains .WithTags<QCatA>().WithoutTags<QCatB>()
            // EntityA has QCatA only, EntityAB has QCatA+QCatB
            // Should only match EntityA
            using var env = CreateMultiTagEnv();
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

            int count = 0;
            foreach (var view in QWithoutTagView.Query(a).WithTags<QCatA>().WithoutTags<QCatB>())
            {
                NAssert.AreEqual(10, view.TestInt.Value);
                count++;
            }
            NAssert.AreEqual(1, count);
        }

        [Test]
        public void AspectQuery_WithoutComponents_ExcludesMatching()
        {
            // QWithoutCompView is a plain aspect (IRead<TestInt> only)
            // Query chains .WithTags<QCatB>().WithoutComponents<TestFloat>()
            // CreateComponentFilterEnv has QTestEntityA (QCatA, TestInt+TestFloat) and QTestEntityB (QCatB, TestInt only)
            // Only QTestEntityB matches (has QCatB, no TestFloat)
            using var env = CreateComponentFilterEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 10 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId3>.Value).Set(new TestInt { Value = 30 }).AssertComplete();
            a.SubmitEntities();

            int count = 0;
            foreach (
                var ei in a.Query().WithTags<QCatB>().WithoutComponents<TestFloat>().EntityIndices()
            )
            {
                var view = new QWithoutCompView(a, ei);
                NAssert.AreEqual(30, view.TestInt.Value);
                count++;
            }
            NAssert.AreEqual(1, count);
        }

        #endregion

        #region ForEachEntity EntityIndex Parameter

        [ForEachEntity(Tags = new[] { typeof(QCatA) })]
        void ProcessWithEntityIndex(in QSingleTagView view, EntityIndex entityIndex)
        {
            _entityIndexResults.Add(entityIndex);
        }

        List<EntityIndex> _entityIndexResults = new List<EntityIndex>();

        [Test]
        public void ForEachEntity_AspectMode_EntityIndex_ReceivesCorrectIndices()
        {
            using var env = CreateMultiTagEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat())
                .AssertComplete();
            a.AddEntity(Tag<QId1>.Value)
                .Set(new TestInt { Value = 2 })
                .Set(new TestFloat())
                .AssertComplete();
            a.SubmitEntities();

            _entityIndexResults.Clear();
            ProcessWithEntityIndex(a);

            NAssert.AreEqual(2, _entityIndexResults.Count);
            // Entity indices should be valid (non-null group, sequential indices)
            NAssert.IsFalse(_entityIndexResults[0].IsNull);
            NAssert.IsFalse(_entityIndexResults[1].IsNull);
            NAssert.AreEqual(0, _entityIndexResults[0].Index);
            NAssert.AreEqual(1, _entityIndexResults[1].Index);
            // Both should be in the same group
            NAssert.AreEqual(_entityIndexResults[0].GroupIndex, _entityIndexResults[1].GroupIndex);
        }

        #endregion

        #region Multiple IRead / IWrite Interfaces

        TestEnvironment CreateMultiInterfaceEnv() =>
            EcsTestHelper.CreateEnvironment(QTestEntityAll.Template);

        [Test]
        public void MultiInterface_Aspect_ReadsAllComponents()
        {
            using var env = CreateMultiInterfaceEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId4>.Value)
                .Set(new TestInt { Value = 42 })
                .Set(new TestFloat { Value = 3.14f })
                .Set(new TestVec { X = 1.0f, Y = 2.0f })
                .Set(new TestBool { Value = true })
                .Set(new TestShort { Value = 7 })
                .AssertComplete();
            a.SubmitEntities();

            int count = 0;
            foreach (var view in QMultiInterfaceView.Query(a).WithTags<QCatA>())
            {
                // IRead<TestInt, TestFloat> components
                NAssert.AreEqual(42, view.TestInt.Value);
                NAssert.AreEqual(3.14f, view.TestFloat.Value, 0.001f);

                // IRead<TestVec> component
                NAssert.AreEqual(1.0f, view.TestVec.X, 0.001f);
                NAssert.AreEqual(2.0f, view.TestVec.Y, 0.001f);

                // IWrite<TestBool, TestShort> components (readable too)
                NAssert.AreEqual(true, view.TestBool.Value);
                NAssert.AreEqual(7, view.TestShort.Value);

                count++;
            }

            NAssert.AreEqual(1, count);
        }

        [Test]
        public void MultiInterface_Aspect_WritesComponents()
        {
            using var env = CreateMultiInterfaceEnv();
            var a = env.Accessor;

            a.AddEntity(Tag<QId4>.Value)
                .Set(new TestInt { Value = 1 })
                .Set(new TestFloat { Value = 1.0f })
                .Set(new TestVec { X = 0, Y = 0 })
                .Set(new TestBool { Value = false })
                .Set(new TestShort { Value = 0 })
                .AssertComplete();
            a.SubmitEntities();

            // Modify IWrite components through the aspect
            foreach (var view in QMultiInterfaceView.Query(a).WithTags<QCatA>())
            {
                view.TestBool = new TestBool { Value = true };
                view.TestShort = new TestShort { Value = 99 };
            }

            // Verify the writes persisted
            foreach (var view in QMultiInterfaceView.Query(a).WithTags<QCatA>())
            {
                NAssert.AreEqual(true, view.TestBool.Value);
                NAssert.AreEqual(99, view.TestShort.Value);
            }
        }

        #endregion
    }
}

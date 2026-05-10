using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public struct ItMutSetA : IEntitySet<QId1> { }

    public struct ItMutSetB : IEntitySet<QId1> { }

    public struct ItMutSetMulti : IEntitySet { }

    [TestFixture]
    public class SetIterationMutationTests
    {
        TestEnvironment CreateSingleGroupEnv() =>
            EcsTestHelper.CreateEnvironment(
                b =>
                {
                    b.AddSet<ItMutSetA>();
                    b.AddSet<ItMutSetB>();
                },
                QTestEntityA.Template
            );

        TestEnvironment CreateMultiGroupEnv() =>
            EcsTestHelper.CreateEnvironment(
                b => b.AddSet<ItMutSetMulti>(),
                QTestEntityA.Template,
                QTestEntityAB.Template
            );

        static GroupIndex SpawnA(WorldAccessor a, int count)
        {
            for (int i = 0; i < count; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt { Value = i })
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.SubmitEntities();
            return a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
        }

        // ── Safe boundaries: these scenarios must continue to work ─────────────

        [Test]
        public void Iterate_AddToDifferentSet_VisitsAllOriginalEntries()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 4; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            var visited = new List<int>();
            foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                visited.Add(ei.Index);
                a.Set<ItMutSetB>().Write.Add(new EntityIndex(ei.Index, group));
            }

            visited.Sort();
            NAssert.AreEqual(new[] { 0, 1, 2, 3 }, visited.ToArray());
            NAssert.AreEqual(4, a.Set<ItMutSetB>().Read.Count);
        }

        [Test]
        public void Iterate_RemoveFromDifferentSet_VisitsAllOriginalEntries()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 4; i++)
            {
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));
                a.Set<ItMutSetB>().Write.Add(new EntityIndex(i, group));
            }

            var visited = new List<int>();
            foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                visited.Add(ei.Index);
                a.Set<ItMutSetB>().Write.Remove(new EntityIndex(ei.Index, group));
            }

            visited.Sort();
            NAssert.AreEqual(new[] { 0, 1, 2, 3 }, visited.ToArray());
            NAssert.AreEqual(0, a.Set<ItMutSetB>().Read.Count);
        }

        [Test]
        public void Iterate_ClearOnDifferentSet_VisitsAllOriginalEntries()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 4; i++)
            {
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));
                a.Set<ItMutSetB>().Write.Add(new EntityIndex(i, group));
            }

            var visited = new List<int>();
            bool cleared = false;
            foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                visited.Add(ei.Index);
                if (!cleared)
                {
                    a.Set<ItMutSetB>().Write.Clear();
                    cleared = true;
                }
            }

            visited.Sort();
            NAssert.AreEqual(new[] { 0, 1, 2, 3 }, visited.ToArray());
            NAssert.AreEqual(0, a.Set<ItMutSetB>().Read.Count);
        }

        [Test]
        public void Iterate_AddToSameSetDifferentGroup_VisitsAllOriginalEntries()
        {
            using var env = CreateMultiGroupEnv();
            var a = env.Accessor;
            var groupA = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(Tag<QId2>.Value);

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.AddEntity(Tag<QId2>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            for (int i = 0; i < 3; i++)
                a.Set<ItMutSetMulti>().Write.Add(new EntityIndex(i, groupA));

            // Filter the query to QId1 so iteration walks only groupA's slice.
            // Mutating a *different* group's entry within the same set is safe —
            // it touches a different SetGroupEntry/dense dict.
            var visitedA = 0;
            foreach (var ei in a.Query().WithTags<QId1>().InSet<ItMutSetMulti>().EntityIndices())
            {
                NAssert.AreEqual(groupA.Index, ei.GroupIndex.Index);
                visitedA++;
                a.Set<ItMutSetMulti>().Write.Add(new EntityIndex(0, groupB));
            }

            NAssert.AreEqual(3, visitedA);
            NAssert.IsTrue(a.Set<ItMutSetMulti>().Read.Exists(new EntityIndex(0, groupB)));
        }

        [Test]
        public void Iterate_RemoveFromSameSetDifferentGroup_VisitsAllOriginalEntries()
        {
            using var env = CreateMultiGroupEnv();
            var a = env.Accessor;
            var groupA = a.WorldInfo.GetSingleGroupWithTags(Tag<QId1>.Value);
            var groupB = a.WorldInfo.GetSingleGroupWithTags(Tag<QId2>.Value);

            for (int i = 0; i < 3; i++)
            {
                a.AddEntity(Tag<QId1>.Value)
                    .Set(new TestInt())
                    .Set(new TestFloat())
                    .AssertComplete();
            }
            a.AddEntity(Tag<QId2>.Value).Set(new TestInt()).Set(new TestFloat()).AssertComplete();
            a.SubmitEntities();

            for (int i = 0; i < 3; i++)
                a.Set<ItMutSetMulti>().Write.Add(new EntityIndex(i, groupA));
            a.Set<ItMutSetMulti>().Write.Add(new EntityIndex(0, groupB));

            // Filter the query to QCatA so it only iterates groupA — leaves groupB alone.
            var visited = 0;
            foreach (var ei in a.Query().WithTags<QCatA>().InSet<ItMutSetMulti>().EntityIndices())
            {
                if (ei.GroupIndex.Index != groupA.Index)
                    continue;
                visited++;
                a.Set<ItMutSetMulti>().Write.Remove(new EntityIndex(0, groupB));
            }

            NAssert.AreEqual(3, visited);
            NAssert.IsFalse(a.Set<ItMutSetMulti>().Read.Exists(new EntityIndex(0, groupB)));
        }

        [Test]
        public void Iterate_DeferredSetAdd_NotVisibleUntilSubmit()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 3);

            a.Set<ItMutSetA>().Write.Add(new EntityIndex(0, group));
            a.Set<ItMutSetA>().Write.Add(new EntityIndex(1, group));

            var visited = new List<int>();
            foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                visited.Add(ei.Index);
                // Deferred — must not change what's iterated.
                a.Set<ItMutSetA>().Defer.Add(new EntityIndex(2, group));
            }
            visited.Sort();
            NAssert.AreEqual(
                new[] { 0, 1 },
                visited.ToArray(),
                "Deferred SetAdd must not be visible until SubmitEntities()."
            );
            NAssert.AreEqual(2, a.Set<ItMutSetA>().Read.Count);

            a.SubmitEntities();
            NAssert.AreEqual(3, a.Set<ItMutSetA>().Read.Count);
        }

        [Test]
        public void Iterate_DeferredSetRemove_NotVisibleUntilSubmit()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 3);

            for (int i = 0; i < 3; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            var visited = new List<int>();
            foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                visited.Add(ei.Index);
                a.Set<ItMutSetA>().Defer.Remove(ei);
            }
            visited.Sort();
            NAssert.AreEqual(
                new[] { 0, 1, 2 },
                visited.ToArray(),
                "Deferred SetRemove must not be visible until SubmitEntities()."
            );
            NAssert.AreEqual(3, a.Set<ItMutSetA>().Read.Count);

            a.SubmitEntities();
            NAssert.AreEqual(0, a.Set<ItMutSetA>().Read.Count);
        }

        [Test]
        public void NestedIteration_NoMutation_VisitsAllPairs()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 3);

            for (int i = 0; i < 3; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            int pairs = 0;
            foreach (var outerEi in a.Query().InSet<ItMutSetA>().EntityIndices())
            {
                foreach (var innerEi in a.Query().InSet<ItMutSetA>().EntityIndices())
                {
                    pairs++;
                }
            }

            NAssert.AreEqual(9, pairs);
        }

        // ── Iteration guard: mutating the same set + same group must throw ────

        [Test]
        public void Iterate_AddToSameSetSameGroup_Throws()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 2; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            NAssert.Catch<Exception>(() =>
            {
                foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
                {
                    a.Set<ItMutSetA>().Write.Add(new EntityIndex(2, group));
                }
            });
        }

        [Test]
        public void Iterate_RemoveFromSameSetSameGroup_Throws()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 4; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            NAssert.Catch<Exception>(() =>
            {
                foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
                {
                    a.Set<ItMutSetA>().Write.Remove(ei);
                }
            });
        }

        [Test]
        public void Iterate_ClearOnSameSet_Throws()
        {
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 3);

            for (int i = 0; i < 3; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            NAssert.Catch<Exception>(() =>
            {
                foreach (var ei in a.Query().InSet<ItMutSetA>().EntityIndices())
                {
                    a.Set<ItMutSetA>().Write.Clear();
                }
            });
        }

        [Test]
        public void Iterate_DirectSetReadEnumerator_Add_Throws()
        {
            // Same scenario but iterating Set<T>().Read directly rather than via Query.
            using var env = CreateSingleGroupEnv();
            var a = env.Accessor;
            var group = SpawnA(a, 4);

            for (int i = 0; i < 2; i++)
                a.Set<ItMutSetA>().Write.Add(new EntityIndex(i, group));

            NAssert.Catch<Exception>(() =>
            {
                foreach (var (indices, g) in a.Set<ItMutSetA>().Read)
                {
                    foreach (var idx in indices)
                    {
                        a.Set<ItMutSetA>().Write.Add(new EntityIndex(2, g));
                    }
                }
            });
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Tests that entity observation callbacks (OnAdded, OnRemoved, OnMoved) receive
    /// correct EntityRange indices, especially after swap-backs from concurrent
    /// moves and removes in the same frame.
    /// </summary>
    [TestFixture]
    public class CallbackEntityRangeTests
    {
        static readonly TagSet StateA = TagSet.FromTags(TestTags.Gamma, TestTags.StateA);
        static readonly TagSet StateB = TagSet.FromTags(TestTags.Gamma, TestTags.StateB);

        #region OnRemoved — entity data accessible at callback time

        [Test]
        public void OnRemoved_SingleEntity_RangeHasCorrectCount()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            a.AddEntity(StateA)
                .Set(new TestInt { Value = 42 })
                .Set(new TestVec { X = 1f, Y = 2f })
                .AssertComplete();
            a.SubmitEntities();

            int removedCount = 0;
            Group observedGroup = default;
            var sub = a
                .Events.InGroupsWithTags(StateA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        removedCount += indices.Count;
                        observedGroup = group;
                    }
                );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(StateA);
            a.RemoveEntity(new EntityIndex(0, groupA));
            a.SubmitEntities();

            NAssert.AreEqual(1, removedCount, "Should report 1 removed entity");
            NAssert.AreEqual(groupA, observedGroup, "Should fire for correct group");
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_MultipleEntities_RangeCountMatchesRemovals()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            int totalRemoved = 0;
            var sub = a
                .Events.InGroupsWithTags(StateA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        totalRemoved += indices.Count;
                    }
                );

            // Remove 3 entities
            a.RemoveEntity(handles[0]);
            a.RemoveEntity(handles[2]);
            a.RemoveEntity(handles[4]);
            a.SubmitEntities();

            NAssert.AreEqual(3, totalRemoved, "Callback should report 3 removed entities");
            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateA), "2 entities should survive");
            sub.Dispose();
        }

        [Test]
        public void OnRemoved_WithConcurrentMoves_RangeStillCorrect()
        {
            // Moves happen before removes in submission.
            // The removal range should still point to the correct entities.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[6];
            for (int i = 0; i < 6; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            int removedCount = 0;
            var sub = a
                .Events.InGroupsWithTags(StateA)
                .OnRemoved(
                    (group, indices) =>
                    {
                        removedCount += indices.End - indices.Start;
                    }
                );

            // Move entities 0, 1 to StateB; remove entities 4, 5
            a.MoveTo(handles[0].ToIndex(a), StateB);
            a.MoveTo(handles[1].ToIndex(a), StateB);
            a.RemoveEntity(handles[4]);
            a.RemoveEntity(handles[5]);
            a.SubmitEntities();

            NAssert.AreEqual(2, removedCount, "Should report exactly 2 removed entities");
            NAssert.AreEqual(
                2,
                a.CountEntitiesWithTags(StateA),
                "2 entities should remain in StateA"
            );
            NAssert.AreEqual(2, a.CountEntitiesWithTags(StateB), "2 entities moved to StateB");
            sub.Dispose();
        }

        #endregion

        #region OnMoved — range points to destination group

        [Test]
        public void OnMoved_SingleEntity_RangeInDestinationGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            a.AddEntity(StateA).Set(new TestInt { Value = 77 }).Set(new TestVec()).AssertComplete();
            a.SubmitEntities();

            int observedValue = -1;
            Group observedToGroup = default;
            EntityRange observedRange = default;

            var sub = a
                .Events.InGroupsWithTags(StateB)
                .OnMoved(
                    (fromGroup, toGroup, indices) =>
                    {
                        observedToGroup = toGroup;
                        observedRange = indices;
                        for (int i = indices.Start; i < indices.End; i++)
                            observedValue = a.Component<TestInt>(
                                new EntityIndex(i, toGroup)
                            ).Read.Value;
                    }
                );

            var groupA = a.WorldInfo.GetSingleGroupWithTags(StateA);
            a.MoveTo(new EntityIndex(0, groupA), StateB);
            a.SubmitEntities();

            var groupB = a.WorldInfo.GetSingleGroupWithTags(StateB);
            NAssert.AreEqual(groupB, observedToGroup);
            NAssert.AreEqual(1, observedRange.Count);
            NAssert.AreEqual(
                77,
                observedValue,
                "Should read moved entity data in destination group"
            );
            sub.Dispose();
        }

        [Test]
        public void OnMoved_MultipleEntities_RangeCoversAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[5];
            for (int i = 0; i < 5; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            var movedValues = new List<int>();
            var sub = a
                .Events.InGroupsWithTags(StateB)
                .OnMoved(
                    (fromGroup, toGroup, indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                            movedValues.Add(
                                a.Component<TestInt>(new EntityIndex(i, toGroup)).Read.Value
                            );
                    }
                );

            // Move 3 entities
            a.MoveTo(handles[0].ToIndex(a), StateB);
            a.MoveTo(handles[2].ToIndex(a), StateB);
            a.MoveTo(handles[4].ToIndex(a), StateB);
            a.SubmitEntities();

            NAssert.AreEqual(3, movedValues.Count);
            movedValues.Sort();
            NAssert.AreEqual(0, movedValues[0]);
            NAssert.AreEqual(20, movedValues[1]);
            NAssert.AreEqual(40, movedValues[2]);
            sub.Dispose();
        }

        [Test]
        public void OnMoved_WithConcurrentRemoves_RangeOnlyCountsMoved()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[6];
            for (int i = 0; i < 6; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            int movedCount = 0;
            var sub = a
                .Events.InGroupsWithTags(StateB)
                .OnMoved(
                    (fromGroup, toGroup, indices) =>
                    {
                        movedCount += indices.Count;
                    }
                );

            // Move 0,1 to StateB; remove 4,5
            a.MoveTo(handles[0].ToIndex(a), StateB);
            a.MoveTo(handles[1].ToIndex(a), StateB);
            a.RemoveEntity(handles[4]);
            a.RemoveEntity(handles[5]);
            a.SubmitEntities();

            NAssert.AreEqual(2, movedCount, "Move callback should only count moved entities");
            sub.Dispose();
        }

        #endregion

        #region OnAdded — range points to newly added entities

        [Test]
        public void OnAdded_MultipleEntities_RangeCoversAll()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var addedValues = new List<int>();
            var sub = a
                .Events.InGroupsWithTags(StateA)
                .OnAdded(
                    (group, indices) =>
                    {
                        for (int i = indices.Start; i < indices.End; i++)
                            addedValues.Add(
                                a.Component<TestInt>(new EntityIndex(i, group)).Read.Value
                            );
                    }
                );

            for (int i = 0; i < 4; i++)
            {
                a.AddEntity(StateA)
                    .Set(new TestInt { Value = i * 10 })
                    .Set(new TestVec())
                    .AssertComplete();
            }
            a.SubmitEntities();

            NAssert.AreEqual(4, addedValues.Count);
            addedValues.Sort();
            NAssert.AreEqual(0, addedValues[0]);
            NAssert.AreEqual(10, addedValues[1]);
            NAssert.AreEqual(20, addedValues[2]);
            NAssert.AreEqual(30, addedValues[3]);
            sub.Dispose();
        }

        [Test]
        public void OnAdded_WithConcurrentRemoves_AddRangeUnaffected()
        {
            // Removes are processed before adds. New entities should be
            // at the end of the group's arrays, after any swap-backs.
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.WithStates);
            var a = env.Accessor;

            var handles = new EntityHandle[3];
            for (int i = 0; i < 3; i++)
            {
                handles[i] = a.AddEntity(StateA)
                    .Set(new TestInt { Value = i })
                    .Set(new TestVec())
                    .AssertComplete()
                    .Handle;
            }
            a.SubmitEntities();

            int addedCount = 0;
            var sub = a
                .Events.InGroupsWithTags(StateA)
                .OnAdded(
                    (group, indices) =>
                    {
                        addedCount += indices.Count;
                    }
                );

            // Remove entity 0, add 2 new entities — all in same submission
            a.RemoveEntity(handles[0]);
            a.AddEntity(StateA)
                .Set(new TestInt { Value = 100 })
                .Set(new TestVec())
                .AssertComplete();
            a.AddEntity(StateA)
                .Set(new TestInt { Value = 101 })
                .Set(new TestVec())
                .AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(2, addedCount, "OnAdded should fire for the 2 new entities");
            NAssert.AreEqual(4, a.CountEntitiesWithTags(StateA), "3 - 1 + 2 = 4");
            sub.Dispose();
        }

        #endregion
    }
}

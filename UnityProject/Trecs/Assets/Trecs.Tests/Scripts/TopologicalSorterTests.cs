using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TopologicalSorterTests
    {
        static IterableHashSet<int> SetOf(params int[] values)
        {
            var set = new IterableHashSet<int>(values.Length);
            foreach (var v in values)
                set.Add(v);
            return set;
        }

        [Test]
        public void LinearChain_ReturnsCorrectOrder()
        {
            var items = new List<string> { "A", "B", "C" };

            var deps = new Dictionary<int, IterableHashSet<int>>
            {
                { 0, new IterableHashSet<int>() },
                { 1, SetOf(0) },
                { 2, SetOf(1) },
            };

            var result = TopologicalSorter.Run(
                items,
                i => deps[items.IndexOf(i)],
                _ => new[] { 0 },
                s => s
            );

            NAssert.AreEqual(new List<int> { 0, 1, 2 }, result);
        }

        [Test]
        public void NoDependencies_ReturnsSortedBySortKeys()
        {
            var items = new List<string> { "C", "A", "B" };
            var empty = new IterableHashSet<int>();

            var result = TopologicalSorter.Run(items, _ => empty, s => new[] { (int)s[0] }, s => s);

            NAssert.AreEqual(3, result.Count);
        }

        [Test]
        public void SimpleCycle_ThrowsWithAccurateCycleMessage()
        {
            // Graph: 0 -> 1 -> 2 -> 1 (cycle between 1 and 2)
            var items = new List<string> { "Root", "NodeA", "NodeB" };

            var deps = new Dictionary<int, IterableHashSet<int>>
            {
                { 0, new IterableHashSet<int>() },
                { 1, SetOf(0, 2) },
                { 2, SetOf(1) },
            };

            var ex = NAssert.Throws<InvalidOperationException>(() =>
                TopologicalSorter.Run(items, i => deps[items.IndexOf(i)], _ => new[] { 0 }, s => s)
            );

            NAssert.IsTrue(
                ex.Message.Contains("NodeA") && ex.Message.Contains("NodeB"),
                $"Cycle message should mention the cycle participants (NodeA, NodeB). Got: {ex.Message}"
            );

            NAssert.IsFalse(
                ex.Message.Contains("Root"),
                $"Cycle message should NOT include non-cycle node 'Root'. Got: {ex.Message}"
            );
        }

        [Test]
        public void DirectSelfCycle_ThrowsWithAccurateMessage()
        {
            // Graph: 0 -> 0 (self-loop)
            var items = new List<string> { "SelfLoop" };

            var deps = new Dictionary<int, IterableHashSet<int>> { { 0, SetOf(0) } };

            var ex = NAssert.Throws<InvalidOperationException>(() =>
                TopologicalSorter.Run(items, i => deps[items.IndexOf(i)], _ => new[] { 0 }, s => s)
            );

            NAssert.IsTrue(
                ex.Message.Contains("SelfLoop"),
                $"Cycle message should mention the self-loop node. Got: {ex.Message}"
            );
        }

        [Test]
        public void LongerCycle_OnlyShowsCycleNodes()
        {
            // Graph: 0 -> 1 -> 2 -> 3 -> 1 (cycle: 1->2->3->1, root: 0)
            var items = new List<string> { "Root", "CycA", "CycB", "CycC" };

            var deps = new Dictionary<int, IterableHashSet<int>>
            {
                { 0, new IterableHashSet<int>() },
                { 1, SetOf(0, 3) },
                { 2, SetOf(1) },
                { 3, SetOf(2) },
            };

            var ex = NAssert.Throws<InvalidOperationException>(() =>
                TopologicalSorter.Run(items, i => deps[items.IndexOf(i)], _ => new[] { 0 }, s => s)
            );

            NAssert.IsTrue(
                ex.Message.Contains("CycA")
                    && ex.Message.Contains("CycB")
                    && ex.Message.Contains("CycC"),
                $"Cycle message should mention all cycle participants. Got: {ex.Message}"
            );

            NAssert.IsFalse(
                ex.Message.Contains("Root"),
                $"Cycle message should NOT include non-cycle node 'Root'. Got: {ex.Message}"
            );
        }
    }
}

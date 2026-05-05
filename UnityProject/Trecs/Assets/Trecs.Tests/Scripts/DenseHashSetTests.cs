using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class DenseHashSetTests
    {
        #region Add / Contains

        [Test]
        public void TestBasic()
        {
            var set = new DenseHashSet<int>(32);
            NAssert.AreEqual(0, set.Count);
            set.Add(1);
            set.Add(2);
            set.Add(3);
            NAssert.AreEqual(3, set.Count);
            NAssert.IsTrue(set.Contains(2));
            NAssert.IsTrue(!set.Contains(4));
        }

        [Test]
        public void TestAddingExistingItem()
        {
            var set = new DenseHashSet<int>();

            // First add should return true
            bool added = set.Add(42);
            NAssert.IsTrue(added);
            NAssert.AreEqual(1, set.Count);

            // Second add of same value should return false
            added = set.Add(42);
            NAssert.IsTrue(!added);
            NAssert.AreEqual(1, set.Count);
        }

        [Test]
        public void TestEquality()
        {
            // This test ensures that the HashSet correctly uses IEquatable<T> for equality
            var set = new DenseHashSet<Vector2>();

            var v1 = new Vector2(1.0f, 2.0f);
            var v1Copy = new Vector2(1.0f, 2.0f); // Same values, different instance

            set.Add(v1);

            NAssert.AreEqual(1, set.Count);
            NAssert.IsTrue(set.Contains(v1Copy)); // Should use value equality, not reference equality

            bool added = set.Add(v1Copy);
            NAssert.IsTrue(!added); // Shouldn't add duplicate value
            NAssert.AreEqual(1, set.Count);
        }

        #endregion

        #region Remove

        [Test]
        public void TestRemove()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            // Remove existing item
            bool removed = set.TryRemove(2);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(2, set.Count);
            NAssert.IsTrue(!set.Contains(2));

            // Remove non-existing item
            removed = set.TryRemove(5);
            NAssert.IsTrue(!removed);
            NAssert.AreEqual(2, set.Count);
        }

        #endregion

        #region Clear / Recycle

        [Test]
        public void TestClear()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            set.Clear();
            NAssert.AreEqual(0, set.Count);
            NAssert.IsTrue(!set.Contains(1));
            NAssert.IsTrue(!set.Contains(2));
            NAssert.IsTrue(!set.Contains(3));
        }

        [Test]
        public void TestRecycle()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            set.Recycle();

            NAssert.AreEqual(0, set.Count);
            NAssert.IsTrue(!set.Contains(1));
            NAssert.IsTrue(!set.Contains(2));
            NAssert.IsTrue(!set.Contains(3));

            // Add new items after recycling
            set.Add(4);
            set.Add(5);

            NAssert.AreEqual(2, set.Count);
            NAssert.IsTrue(set.Contains(4));
            NAssert.IsTrue(set.Contains(5));
        }

        #endregion

        #region Set Operations

        [Test]
        public void TestUnionWith()
        {
            var set1 = new DenseHashSet<int>();
            set1.Add(1);
            set1.Add(2);
            set1.Add(3);

            var set2 = new DenseHashSet<int>();
            set2.Add(3);
            set2.Add(4);
            set2.Add(5);

            // Perform union
            set1.UnionWith(set2);

            NAssert.AreEqual(5, set1.Count);
            NAssert.IsTrue(set1.Contains(1));
            NAssert.IsTrue(set1.Contains(2));
            NAssert.IsTrue(set1.Contains(3));
            NAssert.IsTrue(set1.Contains(4));
            NAssert.IsTrue(set1.Contains(5));
        }

        [Test]
        public void TestIntersectWith()
        {
            var set1 = new DenseHashSet<int>();
            set1.Add(1);
            set1.Add(2);
            set1.Add(3);

            var set2 = new DenseHashSet<int>();
            set2.Add(2);
            set2.Add(3);
            set2.Add(4);

            // Perform intersection
            set1.IntersectWith(set2);

            NAssert.AreEqual(2, set1.Count);
            NAssert.IsTrue(!set1.Contains(1));
            NAssert.IsTrue(set1.Contains(2));
            NAssert.IsTrue(set1.Contains(3));
            NAssert.IsTrue(!set1.Contains(4));
        }

        [Test]
        public void TestExceptWith()
        {
            var set1 = new DenseHashSet<int>();
            set1.Add(1);
            set1.Add(2);
            set1.Add(3);
            set1.Add(4);

            var set2 = new DenseHashSet<int>();
            set2.Add(2);
            set2.Add(4);

            // Perform except
            set1.ExceptWith(set2);

            NAssert.AreEqual(2, set1.Count);
            NAssert.IsTrue(set1.Contains(1));
            NAssert.IsTrue(!set1.Contains(2));
            NAssert.IsTrue(set1.Contains(3));
            NAssert.IsTrue(!set1.Contains(4));
        }

        #endregion

        #region Enumeration

        [Test]
        public void TestIteration()
        {
            var set = new DenseHashSet<int>();

            // Add items in a specific order
            set.Add(3);
            set.Add(1);
            set.Add(4);
            set.Add(2);

            // Collect all values from the set
            List<int> collectedValues = new List<int>();
            foreach (int value in set)
            {
                collectedValues.Add(value);
            }

            // Verify all expected items were iterated
            NAssert.AreEqual(4, collectedValues.Count); // Make sure we iterated all elements
            NAssert.IsTrue(collectedValues.Contains(1));
            NAssert.IsTrue(collectedValues.Contains(2));
            NAssert.IsTrue(collectedValues.Contains(3));
            NAssert.IsTrue(collectedValues.Contains(4));
        }

        [Test]
        public void TestIterationAfterRemove()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);
            set.Add(4);

            // Remove an item in the middle
            set.RemoveMustExist(2);

            // Collect all values after removal
            List<int> collectedValues = new List<int>();
            foreach (var value in set)
            {
                collectedValues.Add(value);
            }

            // Verify all expected items were iterated
            NAssert.AreEqual(3, collectedValues.Count); // Make sure we iterated all elements
            NAssert.IsTrue(collectedValues.Contains(1));
            NAssert.IsTrue(!collectedValues.Contains(2));
            NAssert.IsTrue(collectedValues.Contains(3));
            NAssert.IsTrue(collectedValues.Contains(4));
        }

        [Test]
        public void TestCopyElementsToArray()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            int[] array = new int[3];
            set.CopyElementsTo(array);

            // We can't guarantee exact order for all operations, but we can ensure all elements are copied
            NAssert.IsTrue(array.Contains(1));
            NAssert.IsTrue(array.Contains(2));
            NAssert.IsTrue(array.Contains(3));
        }

        [Test]
        public void TestCopyElementsToSvList()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            var list = new List<int>();
            set.CopyElementsTo(list);

            NAssert.AreEqual(3, list.Count);
            NAssert.IsTrue(list.Contains(1));
            NAssert.IsTrue(list.Contains(2));
            NAssert.IsTrue(list.Contains(3));
        }

        [Test]
        public void TestReadOnlyHashSet()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
            set.Add(3);

            ReadOnlyDenseHashSet<int> readOnlySet = set;

            // Test Count and Contains methods
            NAssert.AreEqual(3, readOnlySet.Count);
            NAssert.IsTrue(readOnlySet.Contains(2));
            NAssert.IsTrue(!readOnlySet.Contains(4));

            // Test iteration
            int sum = 0;
            foreach (int value in readOnlySet)
            {
                sum += value;
            }

            NAssert.AreEqual(6, sum); // 1 + 2 + 3
        }

        #endregion

        #region Capacity

        [Test]
        public void TestTrimAndCapacity()
        {
            var set = new DenseHashSet<int>(100);

            // Add a few items
            set.Add(1);
            set.Add(2);
            set.Add(3);

            // Trim the capacity
            set.Trim();

            // Ensure items still exist
            NAssert.AreEqual(3, set.Count);
            NAssert.IsTrue(set.Contains(1));
            NAssert.IsTrue(set.Contains(2));
            NAssert.IsTrue(set.Contains(3));
        }

        #endregion

        #region Stress / Edge Cases

        [Test]
        public void TestStressWithManyItems()
        {
            const int COUNT = 10000;
            var set = new DenseHashSet<int>();

            // Add many items
            for (int i = 0; i < COUNT; i++)
            {
                set.Add(i);
            }

            NAssert.AreEqual(COUNT, set.Count);

            // Check all items exist
            for (int i = 0; i < COUNT; i++)
            {
                NAssert.IsTrue(set.Contains(i));
            }

            // Remove half the items
            for (int i = 0; i < COUNT; i += 2)
            {
                set.RemoveMustExist(i);
            }

            NAssert.AreEqual(COUNT / 2, set.Count);

            // Check remaining items
            for (int i = 1; i < COUNT; i += 2)
            {
                NAssert.IsTrue(set.Contains(i));
            }
        }

        [Test]
        public void TestWithStructKeyType()
        {
            // Test with a struct type to ensure it works correctly
            var set = new DenseHashSet<Vector2>();

            var v1 = new Vector2(1.0f, 2.0f);
            var v2 = new Vector2(3.0f, 4.0f);
            var v3 = new Vector2(5.0f, 6.0f);

            set.Add(v1);
            set.Add(v2);
            set.Add(v3);

            NAssert.AreEqual(3, set.Count);
            NAssert.IsTrue(set.Contains(v2));
            NAssert.IsTrue(!set.Contains(new Vector2(7.0f, 8.0f)));

            set.RemoveMustExist(v2);

            NAssert.AreEqual(2, set.Count);
            NAssert.IsTrue(!set.Contains(v2));
        }

        [Test]
        public void TestDispose()
        {
            var set = new DenseHashSet<int>();
            set.Add(1);
            set.Add(2);
        }

        #endregion
    }
}

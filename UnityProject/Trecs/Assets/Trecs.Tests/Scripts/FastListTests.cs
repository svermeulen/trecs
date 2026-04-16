using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class FastListTests
    {
        #region Construction

        [Test]
        public void TestConstructors()
        {
            // Default constructor
            var list1 = new FastList<int>();
            NAssert.AreEqual(0, list1.Count);

            // Constructor with initial size
            var list2 = new FastList<int>(10);
            NAssert.AreEqual(0, list2.Count);
            NAssert.AreEqual(10, list2.Capacity);

            // Constructor with array
            var list3 = new FastList<int>(1, 2, 3, 4);
            NAssert.AreEqual(4, list3.Count);
            NAssert.AreEqual(1, list3[0]);
            NAssert.AreEqual(4, list3[3]);

            // Constructor with collection
            var source = new List<int> { 5, 6, 7 };
            var list4 = new FastList<int>(source);
            NAssert.AreEqual(3, list4.Count);
            NAssert.AreEqual(5, list4[0]);
            NAssert.AreEqual(7, list4[2]);

            // Constructor with collection and extra size
            var list5 = new FastList<int>(source, 10);
            NAssert.AreEqual(3, list5.Count);
            NAssert.IsTrue(list5.Capacity >= 13);

            // Constructor from another FastList
            var list6 = new FastList<int>(list3);
            NAssert.AreEqual(4, list6.Count);
            NAssert.AreEqual(1, list6[0]);
            NAssert.AreEqual(4, list6[3]);
        }

        [Test]
        public void TestConstructorValidation()
        {
            // Test negative initialSize
            NAssert.Catch(() => new FastList<int>(-1));
            NAssert.Catch(() => new FastList<int>(-100));

            // Test null array parameter
            NAssert.Catch(() => new FastList<int>((int[])null));

            // Test null ICollection parameter
            NAssert.Catch(() => new FastList<int>((ICollection<int>)null));

            // Test null ICollection with negative extraSize
            var validCollection = new List<int> { 1, 2, 3 };
            NAssert.Catch(() => new FastList<int>(null, 5));
            NAssert.Catch(() => new FastList<int>(validCollection, -1));

            // Valid cases should work
            var list1 = new FastList<int>(0); // Zero is valid
            NAssert.AreEqual(0, list1.Count);

            var list2 = new FastList<int>(validCollection, 0); // Zero extraSize is valid
            NAssert.AreEqual(3, list2.Count);
        }

        [Test]
        public void TestStaticFactoryMethods()
        {
            // PreInit creates list with count set
            var list1 = FastList<int>.PreInit(5);
            NAssert.AreEqual(5, list1.Count);
            for (int i = 0; i < 5; i++)
            {
                NAssert.AreEqual(0, list1[i]);
            }

            // Fill creates and fills with instances
            var list2 = FastList<TestClass>.Fill<TestClass>(3);
            NAssert.AreEqual(3, list2.Count);
            for (int i = 0; i < 3; i++)
            {
                NAssert.IsTrue(list2[i] != null);
                NAssert.AreEqual(0, list2[i].Value);
            }

            // PreFill allocates but doesn't set count
            var list3 = FastList<TestClass>.PreFill<TestClass>(3);
            NAssert.AreEqual(0, list3.Count);
            NAssert.AreEqual(3, list3.Capacity);
        }

        [Test]
        public void TestExplicitCastFromArray()
        {
            int[] array = { 1, 2, 3, 4, 5 };
            var list = (FastList<int>)array;

            NAssert.AreEqual(5, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(5, list[4]);

            // List is independent of array
            array[0] = 100;
            NAssert.AreEqual(1, list[0]);
        }

        [Test]
        public void TestSpanConstructor()
        {
            int[] array = { 1, 2, 3, 4, 5 };
            Span<int> span = new Span<int>(array, 1, 3);

            var list = new FastList<int>(span);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(2, list[0]);
            NAssert.AreEqual(3, list[1]);
            NAssert.AreEqual(4, list[2]);
        }

        [Test]
        public void TestArraySegmentConstructor()
        {
            int[] array = { 1, 2, 3, 4, 5 };
            ArraySegment<int> segment = new ArraySegment<int>(array, 1, 3);

            var list = new FastList<int>(segment);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(2, list[0]);
            NAssert.AreEqual(3, list[1]);
            NAssert.AreEqual(4, list[2]);
        }

        #endregion

        #region Add / Insert

        [Test]
        public void TestBasicOperations()
        {
            var list = new FastList<int>(32);
            NAssert.AreEqual(0, list.Count);
            list.Add(1);
            list.Add(2);
            list.Add(3);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(2, list[1]);
            NAssert.AreEqual(3, list[2]);
        }

        [Test]
        public void TestAdd()
        {
            var list = new FastList<int>();

            // Test chaining Add
            list.Add(1).Add(2).Add(3);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(2, list[1]);
            NAssert.AreEqual(3, list[2]);

            // Test Add with automatic resize
            for (int i = 4; i <= 100; i++)
            {
                list.Add(i);
            }
            NAssert.AreEqual(100, list.Count);
            NAssert.AreEqual(100, list[99]);
        }

        [Test]
        public void TestAddAt()
        {
            var list = new FastList<int>(10);

            // AddAt extends the count if necessary
            list.AddAt(5, 50);
            NAssert.AreEqual(6, list.Count);
            NAssert.AreEqual(50, list[5]);

            // Intermediate values are default
            NAssert.AreEqual(0, list[0]);
            NAssert.AreEqual(0, list[4]);

            // Can overwrite existing values
            list.AddAt(2, 20);
            NAssert.AreEqual(20, list[2]);
        }

        [Test]
        public void TestAddRange()
        {
            var list = new FastList<int>();
            list.Add(1);

            // AddRange with array
            list.AddRange(new int[] { 2, 3, 4 });
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(4, list[3]);

            // AddRange with another FastList
            var list2 = new FastList<int>(5, 6, 7);
            list.AddRange(list2);
            NAssert.AreEqual(7, list.Count);
            NAssert.AreEqual(7, list[6]);

            // AddRange with IEnumerable
            list.AddRange(new List<int> { 8, 9, 10 });
            NAssert.AreEqual(10, list.Count);
            NAssert.AreEqual(10, list[9]);
        }

        [Test]
        public void TestAddRangeOptimization()
        {
            var list = new FastList<int>(2); // Small initial capacity

            // Test with ICollection (should pre-allocate)
            var collection = new List<int>();
            for (int i = 0; i < 100; i++)
            {
                collection.Add(i);
            }

            list.AddRange(collection); // Should pre-allocate space for 100 items
            NAssert.AreEqual(100, list.Count);
            NAssert.IsTrue(list.Capacity >= 100); // Should have grown efficiently

            // Test with non-ICollection IEnumerable (can't pre-allocate)
            var enumerable = System.Linq.Enumerable.Where(collection, x => x < 50);
            var list2 = new FastList<int>();
            list2.AddRange(enumerable);

            NAssert.AreEqual(50, list2.Count);
        }

        [Test]
        public void TestInsertAt()
        {
            var list = new FastList<int>(1, 2, 3);

            // Insert at beginning
            list.InsertAt(0, 0);
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(0, list[0]);
            NAssert.AreEqual(1, list[1]);

            // Insert at middle
            list.InsertAt(2, 10);
            NAssert.AreEqual(5, list.Count);
            NAssert.AreEqual(10, list[2]);
            NAssert.AreEqual(2, list[3]);

            // Insert at end
            list.InsertAt(5, 20);
            NAssert.AreEqual(6, list.Count);
            NAssert.AreEqual(20, list[5]);
        }

        [Test]
        public void TestPushPop()
        {
            var list = new FastList<int>();

            // Push returns the index
            int index1 = list.Push(10);
            NAssert.AreEqual(0, index1);
            int index2 = list.Push(20);
            NAssert.AreEqual(1, index2);
            int index3 = list.Push(30);
            NAssert.AreEqual(2, index3);

            NAssert.AreEqual(3, list.Count);

            // Peek doesn't remove
            ref readonly int peeked = ref list.Peek();
            NAssert.AreEqual(30, peeked);
            NAssert.AreEqual(3, list.Count);

            // Pop removes and returns
            ref readonly int popped1 = ref list.Pop();
            NAssert.AreEqual(30, popped1);
            NAssert.AreEqual(2, list.Count);

            ref readonly int popped2 = ref list.Pop();
            NAssert.AreEqual(20, popped2);
            NAssert.AreEqual(1, list.Count);

            ref readonly int popped3 = ref list.Pop();
            NAssert.AreEqual(10, popped3);
            NAssert.AreEqual(0, list.Count);
        }

        [Test]
        public void TestGetOrCreate()
        {
            var list = new FastList<string>();

            // GetOrCreate extends list and creates value if default
            ref string value1 = ref list.GetOrCreate(2, () => "created");
            NAssert.AreEqual("created", value1);
            NAssert.AreEqual(3, list.Count); // Extended to index 2

            // Doesn't recreate if not default
            ref string value2 = ref list.GetOrCreate(2, () => "new");
            NAssert.AreEqual("created", value2); // Still the old value

            // Creates at new index
            ref string value3 = ref list.GetOrCreate(5, () => "another");
            NAssert.AreEqual("another", value3);
            NAssert.AreEqual(6, list.Count);
        }

        #endregion

        #region Remove

        [Test]
        public void TestRemoveAt()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // Remove from middle
            list.RemoveAt(2);
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(2, list[1]);
            NAssert.AreEqual(4, list[2]);
            NAssert.AreEqual(5, list[3]);

            // Remove from beginning
            list.RemoveAt(0);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(2, list[0]);
            NAssert.AreEqual(4, list[1]);
            NAssert.AreEqual(5, list[2]);

            // Remove from end
            list.RemoveAt(2);
            NAssert.AreEqual(2, list.Count);
            NAssert.AreEqual(2, list[0]);
            NAssert.AreEqual(4, list[1]);
        }

        [Test]
        public void TestUnorderedRemoveAt()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // UnorderedRemoveAt swaps with last element
            bool swapped = list.UnorderedRemoveAt(1);
            NAssert.IsTrue(swapped);
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(5, list[1]); // Was swapped with last
            NAssert.AreEqual(3, list[2]);
            NAssert.AreEqual(4, list[3]);

            // Removing last element doesn't swap
            swapped = list.UnorderedRemoveAt(3);
            NAssert.IsFalse(swapped);
            NAssert.AreEqual(3, list.Count);
        }

        [Test]
        public void TestRemove()
        {
            // Test Remove with value types
            var list = new FastList<int>(1, 2, 3, 4, 3, 5);

            // Remove first occurrence of 3
            bool removed = list.Remove(3);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(5, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(2, list[1]);
            NAssert.AreEqual(4, list[2]); // 4 shifted left
            NAssert.AreEqual(3, list[3]); // Second 3 still there
            NAssert.AreEqual(5, list[4]);

            // Remove non-existent item
            removed = list.Remove(10);
            NAssert.IsFalse(removed);
            NAssert.AreEqual(5, list.Count);

            // Remove last item
            removed = list.Remove(5);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(4, list.Count);

            // Remove first item
            removed = list.Remove(1);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(2, list[0]);
        }

        [Test]
        public void TestRemoveWithReferenceTypes()
        {
            var obj1 = new TestClass { Value = 1 };
            var obj2 = new TestClass { Value = 2 };
            var obj3 = new TestClass { Value = 3 };
            var obj4 = new TestClass { Value = 2 }; // Same value as obj2 but different instance

            var list = new FastList<TestClass>();
            list.Add(obj1);
            list.Add(obj2);
            list.Add(obj3);
            list.Add(obj2); // Add same instance again

            // Remove by reference - should remove first occurrence
            bool removed = list.Remove(obj2);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(obj1, list[0]);
            NAssert.AreEqual(obj3, list[1]);
            NAssert.AreEqual(obj2, list[2]); // Second occurrence still there

            // obj4 has same Value but is different instance - should not be found
            removed = list.Remove(obj4);
            NAssert.IsFalse(removed);
            NAssert.AreEqual(3, list.Count);

            // Remove null from list without nulls
            removed = list.Remove(null);
            NAssert.IsFalse(removed);

            // Add null and then remove it
            list.Add(null);
            NAssert.AreEqual(4, list.Count);
            removed = list.Remove(null);
            NAssert.IsTrue(removed);
            NAssert.AreEqual(3, list.Count);
        }

        [Test]
        public void TestRemoveAll()
        {
            // Test RemoveAll with value types
            var list = new FastList<int>(1, 2, 3, 2, 4, 2, 5);

            // Remove all occurrences of 2
            int removedCount = list.RemoveAll(2);
            NAssert.AreEqual(3, removedCount);
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(1, list[0]);
            NAssert.AreEqual(3, list[1]);
            NAssert.AreEqual(4, list[2]);
            NAssert.AreEqual(5, list[3]);

            // Remove non-existent item
            removedCount = list.RemoveAll(10);
            NAssert.AreEqual(0, removedCount);
            NAssert.AreEqual(4, list.Count);

            // Remove all of single occurrence
            removedCount = list.RemoveAll(4);
            NAssert.AreEqual(1, removedCount);
            NAssert.AreEqual(3, list.Count);

            // Remove all from list with all same values
            list.Clear();
            list.Add(7).Add(7).Add(7).Add(7);
            removedCount = list.RemoveAll(7);
            NAssert.AreEqual(4, removedCount);
            NAssert.AreEqual(0, list.Count);
        }

        [Test]
        public void TestRemoveAllWithReferenceTypes()
        {
            var obj1 = new TestClass { Value = 1 };
            var obj2 = new TestClass { Value = 2 };
            var obj3 = new TestClass { Value = 3 };

            var list = new FastList<TestClass>();
            list.Add(obj1);
            list.Add(obj2);
            list.Add(obj1); // Same instance
            list.Add(obj3);
            list.Add(obj2); // Same instance
            list.Add(obj1); // Same instance

            // Remove all occurrences of obj1
            int removedCount = list.RemoveAll(obj1);
            NAssert.AreEqual(3, removedCount);
            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(obj2, list[0]);
            NAssert.AreEqual(obj3, list[1]);
            NAssert.AreEqual(obj2, list[2]);

            // Test with nulls
            list.Add(null);
            list.Add(null);
            removedCount = list.RemoveAll(null);
            NAssert.AreEqual(2, removedCount);
            NAssert.AreEqual(3, list.Count);
        }

        [Test]
        public void TestRemoveOnEmptyList()
        {
            var list = new FastList<int>();

            // Remove from empty list
            bool removed = list.Remove(1);
            NAssert.IsFalse(removed);
            NAssert.AreEqual(0, list.Count);

            // RemoveAll from empty list
            int removedCount = list.RemoveAll(1);
            NAssert.AreEqual(0, removedCount);
            NAssert.AreEqual(0, list.Count);
        }

        [Test]
        public void TestRemovePerformance()
        {
            // Test that RemoveAll is more efficient than multiple Remove calls
            var list = new FastList<int>();

            // Add many items with duplicates
            for (int i = 0; i < 100; i++)
            {
                list.Add(i % 10); // Will have 10 of each digit 0-9
            }

            // RemoveAll should be efficient for removing multiple items
            int removedCount = list.RemoveAll(5);
            NAssert.AreEqual(10, removedCount);
            NAssert.AreEqual(90, list.Count);

            // Verify no 5s remain
            NAssert.IsFalse(list.Contains(5));
        }

        [Test]
        public void TestReferenceClearingBehavior()
        {
            // Test with reference type to verify clearing behavior
            var list = new FastList<TestClass>();
            list.Add(new TestClass { Value = 1 });
            list.Add(new TestClass { Value = 2 });
            list.Add(new TestClass { Value = 3 });

            // Get direct access to buffer for testing
            var buffer = list.ToArrayFast(out int count);
            NAssert.IsTrue(buffer[0] != null);
            NAssert.IsTrue(buffer[1] != null);
            NAssert.IsTrue(buffer[2] != null);

            // RemoveAt should clear the removed element
            list.RemoveAt(1);
            buffer = list.ToArrayFast(out count);
            NAssert.IsTrue(buffer[0] != null); // Still there
            NAssert.IsTrue(buffer[1] != null); // Element 2 moved here
            NAssert.IsTrue(buffer[2] == null); // Should be cleared
            NAssert.AreEqual(2, count);

            // Clear should clear all elements (for reference types)
            list.Clear();
            buffer = list.ToArrayFast(out count);
            NAssert.AreEqual(0, count);
            // Note: We can't easily test if references were cleared since buffer
            // might be larger than count, but Clear() should handle this
        }

        #endregion

        #region Indexer / Access

        [Test]
        public void TestIndexers()
        {
            var list = new FastList<int>();
            list.Add(10);
            list.Add(20);
            list.Add(30);

            // Test int indexer
            NAssert.AreEqual(10, list[0]);
            NAssert.AreEqual(20, list[1]);
            NAssert.AreEqual(30, list[2]);

            // Modify through indexer
            list[1] = 25;
            NAssert.AreEqual(25, list[1]);
        }

        [Test]
        public void TestContains()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            NAssert.IsTrue(list.Contains(3));
            NAssert.IsTrue(list.Contains(5));
            NAssert.IsFalse(list.Contains(6));
            NAssert.IsFalse(list.Contains(0));
        }

        [Test]
        public void TestReadOnlySvList()
        {
            var list = new FastList<int>(1, 2, 3);
            ReadOnlyFastList<int> readOnlyList = list;

            // Can read
            NAssert.AreEqual(3, readOnlyList.Count);
            NAssert.AreEqual(1, readOnlyList[0]);
            NAssert.IsTrue(readOnlyList.Contains(2));

            // Can iterate
            int sum = 0;
            foreach (ref int value in readOnlyList)
            {
                sum += value;
            }
            NAssert.AreEqual(6, sum);

            // Changes to original list are reflected
            list.Add(4);
            NAssert.AreEqual(4, readOnlyList.Count);
            NAssert.AreEqual(4, readOnlyList[3]);
        }

        [Test]
        public void TestLocalReadOnlySvListBoundsChecking()
        {
            var list = new FastList<int>(1, 2, 3);
            var localReadOnly = new LocalReadOnlyFastList<int>(list);

            // Valid access should work
            NAssert.AreEqual(1, localReadOnly[0]);
            NAssert.AreEqual(3, localReadOnly[2]);

            // Out of bounds should assert
            try
            {
                var _ = localReadOnly[3];
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            { /* Expected */
            }

            try
            {
                var _ = localReadOnly[-1];
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            { /* Expected */
            }

            try
            {
                var _ = localReadOnly[100];
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            { /* Expected */
            }

            // Test empty list
            var emptyList = new FastList<int>();
            var emptyReadOnly = new LocalReadOnlyFastList<int>(emptyList);

            try
            {
                var _ = emptyReadOnly[0];
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            { /* Expected */
            }
        }

        [Test]
        public void TestPeekPopOnEmptyList()
        {
            var list = new FastList<int>();

            // Peek on empty list should assert
            NAssert.Catch(() => list.Peek());

            // Pop on empty list should assert
            NAssert.Catch(() => list.Pop());

            // Add one item then pop it, then pop again
            list.Add(42);
            var popped = list.Pop();
            NAssert.AreEqual(42, popped);
            NAssert.AreEqual(0, list.Count);

            // Second pop should assert
            NAssert.Catch(() => list.Pop());
        }

        #endregion

        #region Clear / Recycle

        [Test]
        public void TestClear()
        {
            var list = new FastList<int>(1, 2, 3);

            list.Clear();
            NAssert.AreEqual(0, list.Count);
            NAssert.IsFalse(list.Contains(1));
            NAssert.IsFalse(list.Contains(2));
            NAssert.IsFalse(list.Contains(3));

            // Can add after clear
            list.Add(4);
            NAssert.AreEqual(1, list.Count);
            NAssert.AreEqual(4, list[0]);
        }

        [Test]
        public void TestMemClear()
        {
            var list = new FastList<string>("a", "b", "c");
            list.SetCountTo(5);

            list.MemClear();

            // MemClear zeroes the entire buffer
            NAssert.AreEqual(5, list.Count); // Count unchanged
            for (int i = 0; i < list.Count; i++)
            {
                NAssert.IsTrue(list[i] == null);
            }
        }

        [Test]
        public void TestReuseOneSlot()
        {
            // Test with reference type
            var list = new FastList<TestClass>(4);

            // Pre-fill the buffer with some non-null objects
            list.Add(new TestClass { Value = 1 });
            list.Add(new TestClass { Value = 2 });

            // ReuseOneSlot will check if buffer[_count] is non-null
            // Since we only added 2 items to a buffer of 4, buffer[2] is null
            bool reused = list.ReuseOneSlot<TestClass>(out TestClass result);
            NAssert.IsFalse(reused); // Should return false because buffer[2] is null
            NAssert.AreEqual(2, list.Count); // Count unchanged
            NAssert.IsTrue(result == null);

            // Manually set buffer[2] to a non-null value
            list.SetCountTo(3);
            list[2] = new TestClass { Value = 3 };
            list.SetCountTo(2); // Reset count back

            // Now ReuseOneSlot should succeed
            reused = list.ReuseOneSlot<TestClass>(out result);
            NAssert.IsTrue(reused); // Should return true because buffer[2] is non-null
            NAssert.AreEqual(3, list.Count); // Count incremented
            NAssert.IsTrue(result != null);
            NAssert.AreEqual(3, result.Value);

            // Set up buffer[3] - need to extend count first
            list.SetCountTo(4);
            list[3] = new TestClass { Value = 4 };
            list.SetCountTo(3); // Reset count back

            // ReuseOneSlot again
            reused = list.ReuseOneSlot<TestClass>(out result);
            NAssert.IsTrue(reused);
            NAssert.AreEqual(4, list.Count);
            NAssert.AreEqual(4, result.Value);

            // No more space in buffer
            reused = list.ReuseOneSlot<TestClass>(out result);
            NAssert.IsFalse(reused);
            NAssert.AreEqual(4, list.Count);
        }

        [Test]
        public void TestReuseOneSlotWithValueType()
        {
            // Test with value type - different behavior
            var list = new FastList<int>(3);
            list.Add(10);

            // For value types, ReuseOneSlot always succeeds if there's space
            bool reused = list.ReuseOneSlot<int>(out int result);
            NAssert.IsTrue(reused);
            NAssert.AreEqual(2, list.Count);
            NAssert.AreEqual(0, result); // Default value for int

            reused = list.ReuseOneSlot<int>(out result);
            NAssert.IsTrue(reused);
            NAssert.AreEqual(3, list.Count);

            // No more space
            reused = list.ReuseOneSlot<int>(out result);
            NAssert.IsFalse(reused);
            NAssert.AreEqual(3, list.Count);
        }

        #endregion

        #region Capacity

        [Test]
        public void TestCapacityManagement()
        {
            var list = new FastList<int>(5);
            NAssert.AreEqual(5, list.Capacity);

            // EnsureCapacity
            list.EnsureCapacity(10);
            NAssert.IsTrue(list.Capacity >= 10);

            // IncreaseCapacityBy
            int currentCapacity = list.Capacity;
            list.IncreaseCapacityBy(5);
            NAssert.IsTrue(list.Capacity > currentCapacity);

            // Add some items
            list.Add(1).Add(2).Add(3);

            // Trim reduces capacity to count
            list.Trim();
            NAssert.AreEqual(list.Count, list.Capacity);
            NAssert.AreEqual(3, list.Count);
        }

        [Test]
        public void TestCountManagement()
        {
            var list = new FastList<int>();

            // SetCountTo
            list.SetCountTo(5);
            NAssert.AreEqual(5, list.Count);
            // Values are default
            for (int i = 0; i < 5; i++)
            {
                NAssert.AreEqual(0, list[i]);
            }

            // EnsureCountIsAtLeast
            list.EnsureCountIsAtLeast(3); // No change
            NAssert.AreEqual(5, list.Count);

            list.EnsureCountIsAtLeast(10); // Increases
            NAssert.AreEqual(10, list.Count);

            // IncrementCountBy
            list.IncrementCountBy(5);
            NAssert.AreEqual(15, list.Count);

            // TrimCount
            list.TrimCount(10);
            NAssert.AreEqual(10, list.Count);
        }

        [Test]
        public void TestCapacityGrowthAlgorithm()
        {
            // Test the specific growth algorithm: newLength = (int)((length + 1) * 1.5f)
            var list = new FastList<int>(1);
            NAssert.AreEqual(1, list.Capacity, "Initial capacity should be 1");

            // Force growth: capacity 1 -> (1+1)*1.5 = 3
            list.Add(1);
            list.Add(2); // Should trigger growth
            NAssert.AreEqual(3, list.Capacity, $"Capacity should be 3, but was {list.Capacity}");

            // Force next growth: capacity 3 -> (3+1)*1.5 = 6
            list.Add(3);
            list.Add(4); // Should trigger growth
            NAssert.AreEqual(6, list.Capacity, $"Capacity should be 6, but was {list.Capacity}");

            // Test growth from empty list (Array.Empty)
            var emptyList = new FastList<int>();
            NAssert.AreEqual(0, emptyList.Capacity, "Empty list should have 0 capacity");

            // Force growth from 0: (0+1)*1.5 = 1.5 -> 1
            emptyList.Add(1);
            NAssert.AreEqual(
                1,
                emptyList.Capacity,
                $"Empty list growth should be 1, but was {emptyList.Capacity}"
            );

            // Force next growth from 1: (1+1)*1.5 = 3
            emptyList.Add(2);
            NAssert.AreEqual(
                3,
                emptyList.Capacity,
                $"Second growth should be 3, but was {emptyList.Capacity}"
            );
        }

        #endregion

        #region Enumeration

        [Test]
        public void TestIteration()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // Using custom enumerator
            int sum = 0;
            int count = 0;
            foreach (ref int value in list)
            {
                sum += value;
                count++;
                // Can modify through ref
                value *= 2;
            }

            NAssert.AreEqual(5, count);
            NAssert.AreEqual(15, sum);

            // Values were modified
            NAssert.AreEqual(2, list[0]);
            NAssert.AreEqual(10, list[4]);
        }

        [Test]
        public void TestIEnumerableIteration()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // Using IEnumerable interface
            List<int> collected = new List<int>();
            foreach (int value in (IEnumerable<int>)list)
            {
                collected.Add(value);
            }

            NAssert.AreEqual(5, collected.Count);
            NAssert.AreEqual(1, collected[0]);
            NAssert.AreEqual(5, collected[4]);
        }

        [Test]
        public void TestEnumeratorEdgeCases()
        {
            var list = new FastList<int>(1, 2, 3);

            // Test accessing Current before MoveNext
            var enumerator = list.GetEnumerator();
            try
            {
                var _ = enumerator.Current;
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            {
                // Expected
            }

            // Test normal enumeration
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.AreEqual(1, enumerator.Current);
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.AreEqual(2, enumerator.Current);
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.AreEqual(3, enumerator.Current);

            // Test accessing Current after enumeration completes
            NAssert.IsFalse(enumerator.MoveNext());
            try
            {
                var _ = enumerator.Current;
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            {
                // Expected
            }

            // Test Reset functionality
            enumerator.Reset();
            try
            {
                var _ = enumerator.Current;
                NAssert.IsTrue(false, "Should have thrown exception");
            }
            catch (Trecs.TrecsException)
            {
                // Expected
            }
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.AreEqual(1, enumerator.Current); // Should start over
        }

        [Test]
        public void TestIEnumeratorEdgeCases()
        {
            var list = new FastList<int>(1, 2, 3);

            // Test IEnumerable enumerator edge cases
            var enumerator = ((IEnumerable<int>)list).GetEnumerator();
            NAssert.Catch(() =>
            {
                var _ = enumerator.Current;
            }); // Before MoveNext

            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.AreEqual(1, enumerator.Current);

            // Finish enumeration
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.IsTrue(enumerator.MoveNext());
            NAssert.IsFalse(enumerator.MoveNext());

            NAssert.Catch(() =>
            {
                var _ = enumerator.Current;
            }); // After enumeration

            enumerator.Dispose(); // Should not throw
        }

        [Test]
        public void TestConcurrentModificationDetection()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // Test modification during enumeration should be detected
            NAssert.Catch(() =>
            {
                foreach (ref int value in list)
                {
                    if (value == 3)
                    {
                        list.Add(6); // This should trigger the assertion
                    }
                }
            });

            // Test modification during IEnumerable enumeration
            list.Clear();
            list.Add(1);
            list.Add(2);
            list.Add(3);

            NAssert.Catch(() =>
            {
                foreach (int value in (IEnumerable<int>)list)
                {
                    if (value == 2)
                    {
                        list.RemoveAt(0); // This should trigger the assertion
                    }
                }
            });
        }

        #endregion

        #region Copy / Conversion

        [Test]
        public void TestToArray()
        {
            var list = new FastList<int>(1, 2, 3, 4, 5);

            // ToArray creates a copy
            int[] array = list.ToArray();
            NAssert.AreEqual(5, array.Length);
            NAssert.AreEqual(1, array[0]);
            NAssert.AreEqual(5, array[4]);

            // Modifying array doesn't affect list
            array[0] = 100;
            NAssert.AreEqual(1, list[0]);
        }

        [Test]
        public void TestToArrayFast()
        {
            var list = new FastList<int>(1, 2, 3);

            // ToArrayFast returns internal buffer
            int[] buffer = list.ToArrayFast(out int count);
            NAssert.AreEqual(3, count);
            NAssert.AreEqual(1, buffer[0]);
            NAssert.AreEqual(3, buffer[2]);

            // Buffer may be larger than count
            NAssert.IsTrue(buffer.Length >= count);
        }

        [Test]
        public void TestCopyTo()
        {
            var list = new FastList<int>(1, 2, 3);
            int[] array = new int[10];

            list.CopyTo(array, 2);

            NAssert.AreEqual(0, array[0]);
            NAssert.AreEqual(0, array[1]);
            NAssert.AreEqual(1, array[2]);
            NAssert.AreEqual(2, array[3]);
            NAssert.AreEqual(3, array[4]);
            NAssert.AreEqual(0, array[5]);
        }

        [Test]
        public void TestShallowClone()
        {
            // Test basic cloning
            var original = new FastList<int>(1, 2, 3, 4, 5);
            var clone = original.ShallowClone();

            // Verify clone has same elements
            NAssert.AreEqual(original.Count, clone.Count);
            for (int i = 0; i < original.Count; i++)
            {
                NAssert.AreEqual(original[i], clone[i]);
            }

            // Verify clone is independent - modifying clone doesn't affect original
            clone[0] = 100;
            NAssert.AreEqual(1, original[0]);
            NAssert.AreEqual(100, clone[0]);

            // Adding to clone doesn't affect original
            clone.Add(6);
            NAssert.AreEqual(6, clone.Count);
            NAssert.AreEqual(5, original.Count);

            // Removing from original doesn't affect clone
            original.RemoveAt(0);
            NAssert.AreEqual(4, original.Count);
            NAssert.AreEqual(6, clone.Count);
        }

        [Test]
        public void TestShallowCloneEmptyList()
        {
            // Test cloning empty list
            var original = new FastList<int>();
            var clone = original.ShallowClone();

            NAssert.AreEqual(0, clone.Count);
            NAssert.AreEqual(0, clone.Capacity);

            // Adding to clone doesn't affect original
            clone.Add(1);
            NAssert.AreEqual(1, clone.Count);
            NAssert.AreEqual(0, original.Count);
        }

        [Test]
        public void TestShallowCloneWithReferenceTypes()
        {
            // Test that it's a shallow clone - references are copied, not objects
            var obj1 = new TestClass { Value = 1 };
            var obj2 = new TestClass { Value = 2 };
            var obj3 = new TestClass { Value = 3 };

            var original = new FastList<TestClass>();
            original.Add(obj1);
            original.Add(obj2);
            original.Add(obj3);

            var clone = original.ShallowClone();

            // Verify same references
            NAssert.AreEqual(3, clone.Count);
            NAssert.IsTrue(ReferenceEquals(clone[0], obj1));
            NAssert.IsTrue(ReferenceEquals(clone[1], obj2));
            NAssert.IsTrue(ReferenceEquals(clone[2], obj3));

            // Modifying object affects both lists (shallow clone)
            obj1.Value = 100;
            NAssert.AreEqual(100, original[0].Value);
            NAssert.AreEqual(100, clone[0].Value);

            // But replacing reference in one list doesn't affect the other
            var obj4 = new TestClass { Value = 4 };
            clone[1] = obj4;
            NAssert.IsTrue(ReferenceEquals(original[1], obj2));
            NAssert.IsTrue(ReferenceEquals(clone[1], obj4));
        }

        [Test]
        public void TestShallowCloneCapacity()
        {
            // Test that clone has exact capacity for elements
            var original = new FastList<int>(100); // Large capacity
            original.Add(1);
            original.Add(2);
            original.Add(3);

            var clone = original.ShallowClone();

            // Clone should have capacity equal to count, not original capacity
            NAssert.AreEqual(3, clone.Count);
            NAssert.AreEqual(3, clone.Capacity);
            NAssert.AreEqual(100, original.Capacity);
        }

        [Test]
        public void TestShallowCloneWithPartiallyFilledList()
        {
            // Test cloning a list that has been partially filled and modified
            var original = new FastList<int>();

            // Add and remove some items
            for (int i = 0; i < 10; i++)
            {
                original.Add(i);
            }
            original.RemoveAt(5);
            original.RemoveAt(0);
            original.Add(100);

            var clone = original.ShallowClone();

            // Verify clone has correct elements
            NAssert.AreEqual(original.Count, clone.Count);
            for (int i = 0; i < original.Count; i++)
            {
                NAssert.AreEqual(original[i], clone[i]);
            }

            // Verify independence
            original.Clear();
            NAssert.AreEqual(0, original.Count);
            NAssert.AreEqual(9, clone.Count);
        }

        [Test]
        public void TestShallowCloneWithNulls()
        {
            // Test cloning list with null references
            var original = new FastList<TestClass>();
            original.Add(new TestClass { Value = 1 });
            original.Add(null);
            original.Add(new TestClass { Value = 2 });
            original.Add(null);

            var clone = original.ShallowClone();

            NAssert.AreEqual(4, clone.Count);
            NAssert.IsTrue(clone[0] != null);
            NAssert.AreEqual(1, clone[0].Value);
            NAssert.IsTrue(clone[1] == null);
            NAssert.IsTrue(clone[2] != null);
            NAssert.AreEqual(2, clone[2].Value);
            NAssert.IsTrue(clone[3] == null);
        }

        [Test]
        public void TestShallowCloneAfterCapacityChanges()
        {
            // Test cloning after various capacity operations
            var original = new FastList<int>(5);
            original.Add(1);
            original.Add(2);

            // Increase capacity
            original.EnsureCapacity(20);
            original.Add(3);

            // Trim
            original.Trim();

            var clone = original.ShallowClone();

            // Clone should have exact capacity for its elements
            NAssert.AreEqual(3, clone.Count);
            NAssert.AreEqual(3, clone.Capacity);
            NAssert.AreEqual(1, clone[0]);
            NAssert.AreEqual(2, clone[1]);
            NAssert.AreEqual(3, clone[2]);
        }

        #endregion

        #region Stress / Edge Cases

        [Test]
        public void TestEmptyListOperations()
        {
            var list = new FastList<int>();

            NAssert.AreEqual(0, list.Count);
            NAssert.IsFalse(list.Contains(0));

            // ToArray on empty list
            var array = list.ToArray();
            NAssert.AreEqual(0, array.Length);

            // Iteration on empty list
            int count = 0;
            foreach (var item in list)
            {
                count++;
            }
            NAssert.AreEqual(0, count);

            // Clear on empty list
            list.Clear();
            NAssert.AreEqual(0, list.Count);
        }

        [Test]
        public void TestWithStructType()
        {
            var list = new FastList<TestStruct>();

            var v1 = new TestStruct(1.0f, 2.0f);
            var v2 = new TestStruct(3.0f, 4.0f);
            var v3 = new TestStruct(5.0f, 6.0f);

            list.Add(v1).Add(v2).Add(v3);

            NAssert.AreEqual(3, list.Count);
            NAssert.AreEqual(v1, list[0]);
            NAssert.AreEqual(v2, list[1]);
            NAssert.AreEqual(v3, list[2]);

            // Modify through ref
            list[1] = new TestStruct(10.0f, 20.0f);
            NAssert.AreEqual(10.0f, list[1].X);
            NAssert.AreEqual(20.0f, list[1].Y);
        }

        [Test]
        public void TestShallowCloneWithStructTypes()
        {
            // Test with value types (structs)
            var original = new FastList<TestStruct>();
            original.Add(new TestStruct(1, 2));
            original.Add(new TestStruct(3, 4));
            original.Add(new TestStruct(5, 6));

            var clone = original.ShallowClone();

            // Verify values are copied
            NAssert.AreEqual(3, clone.Count);
            NAssert.AreEqual(new TestStruct(1, 2), clone[0]);
            NAssert.AreEqual(new TestStruct(3, 4), clone[1]);
            NAssert.AreEqual(new TestStruct(5, 6), clone[2]);

            // Modifying struct in clone doesn't affect original (value types)
            clone[0] = new TestStruct(10, 20);
            NAssert.AreEqual(new TestStruct(1, 2), original[0]);
            NAssert.AreEqual(new TestStruct(10, 20), clone[0]);
        }

        [Test]
        public void TestStressWithManyItems()
        {
            const int COUNT = 10000;
            var list = new FastList<int>();

            // Add many items
            for (int i = 0; i < COUNT; i++)
            {
                list.Add(i);
            }

            NAssert.AreEqual(COUNT, list.Count);

            // Check all items exist
            for (int i = 0; i < COUNT; i++)
            {
                NAssert.AreEqual(i, list[i]);
            }

            // Remove half the items from end
            for (int i = 0; i < COUNT / 2; i++)
            {
                list.RemoveAt(list.Count - 1);
            }

            NAssert.AreEqual(COUNT / 2, list.Count);

            // Check remaining items
            for (int i = 0; i < COUNT / 2; i++)
            {
                NAssert.AreEqual(i, list[i]);
            }
        }

        [Test]
        public void TestIntegerOverflowEdgeCases()
        {
            // Test large counts (but not overflow in practice)
            var list = new FastList<int>();

            // Test SetCountTo with large value (memory permitting)
            try
            {
                list.SetCountTo(1000);
                NAssert.AreEqual(1000, list.Count);
            }
            catch (OutOfMemoryException)
            {
                // Expected on systems with limited memory
            }

            // Test IncrementCountBy
            list.Clear();
            list.IncrementCountBy(100);
            NAssert.AreEqual(100, list.Count);

            list.IncrementCountBy(50);
            NAssert.AreEqual(150, list.Count);
        }

        [Test]
        public void TestShallowCloneLargeList()
        {
            // Test cloning a large list
            const int size = 10000;
            var original = new FastList<int>();

            for (int i = 0; i < size; i++)
            {
                original.Add(i);
            }

            var clone = original.ShallowClone();

            // Verify all elements copied correctly
            NAssert.AreEqual(size, clone.Count);
            for (int i = 0; i < size; i++)
            {
                NAssert.AreEqual(i, clone[i]);
            }

            // Verify independence
            original[0] = -1;
            clone[size - 1] = -1;
            NAssert.AreEqual(-1, original[0]);
            NAssert.AreEqual(0, clone[0]);
            NAssert.AreEqual(size - 1, original[size - 1]);
            NAssert.AreEqual(-1, clone[size - 1]);
        }

        [Test]
        public void TestShallowClonePerformance()
        {
            // Test that clone is efficient (uses Array.Copy)
            var original = new FastList<int>();

            // Create a list with pattern
            for (int i = 0; i < 1000; i++)
            {
                original.Add(i * 2);
            }

            var clone = original.ShallowClone();

            // Verify pattern preserved
            for (int i = 0; i < 1000; i++)
            {
                NAssert.AreEqual(i * 2, clone[i]);
            }
        }

        #endregion

        private class TestClass
        {
            public int Value { get; set; }
        }

        private struct TestStruct : IEquatable<TestStruct>
        {
            public float X;
            public float Y;

            public TestStruct(float x, float y)
            {
                X = x;
                Y = y;
            }

            public bool Equals(TestStruct other) => X == other.X && Y == other.Y;

            public override bool Equals(object obj) => obj is TestStruct other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(X, Y);

            public static bool operator ==(TestStruct left, TestStruct right) => left.Equals(right);

            public static bool operator !=(TestStruct left, TestStruct right) =>
                !left.Equals(right);
        }
    }
}

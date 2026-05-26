using NUnit.Framework;
using Trecs.Collections;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeIterableDictionaryTests
    {
        #region Construction

        [Test]
        public void Constructor_CreatesEmpty()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            NAssert.IsTrue(dict.IsCreated);
            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty);

            dict.Dispose();
        }

        #endregion

        #region Add / Set

        [Test]
        public void Add_NewKey_IncreasesCount()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.5f);

            NAssert.AreEqual(1, dict.Count);
            NAssert.IsFalse(dict.IsEmpty);

            dict.Dispose();
        }

        [Test]
        public void Add_MultipleKeys_AllPresent()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);
            dict.Add(3, 3.0f);

            NAssert.AreEqual(3, dict.Count);
            NAssert.IsTrue(dict.ContainsKey(1));
            NAssert.IsTrue(dict.ContainsKey(2));
            NAssert.IsTrue(dict.ContainsKey(3));

            dict.Dispose();
        }

        [Test]
        public void TryAdd_NewKey_ReturnsTrue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            bool added = dict.TryAdd(1, 1.5f, out var index);

            NAssert.IsTrue(added);
            NAssert.AreEqual(0, index);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void TryAdd_DuplicateKey_ReturnsFalse()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.0f);
            bool added = dict.TryAdd(1, 2.0f, out _);

            NAssert.IsFalse(added);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Set_ExistingKey_UpdatesValue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.0f);
            dict.Set(1, 99.0f);

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual(99.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region ContainsKey / TryGetValue / Indexer

        [Test]
        public void ContainsKey_Existing_ReturnsTrue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(42, 3.14f);

            NAssert.IsTrue(dict.ContainsKey(42));

            dict.Dispose();
        }

        [Test]
        public void ContainsKey_Missing_ReturnsFalse()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            NAssert.IsFalse(dict.ContainsKey(42));

            dict.Dispose();
        }

        [Test]
        public void TryGetValue_Existing_ReturnsTrueAndValue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 3.14f);

            bool found = dict.TryGetValue(1, out var value);

            NAssert.IsTrue(found);
            NAssert.AreEqual(3.14f, value, 0.001f);

            dict.Dispose();
        }

        [Test]
        public void TryGetValue_Missing_ReturnsFalse()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            bool found = dict.TryGetValue(1, out _);

            NAssert.IsFalse(found);

            dict.Dispose();
        }

        [Test]
        public void Indexer_ReturnsCorrectValue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(5, 50.0f);

            NAssert.AreEqual(50.0f, dict[5], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region GetValueByRef / GetOrAdd

        [Test]
        public void GetValueByRef_ModifiesInPlace()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);

            ref var value = ref dict.GetValueByRef(1);
            value = 99.0f;

            NAssert.AreEqual(99.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void GetOrAdd_NewKey_AddsDefault()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            ref var value = ref dict.GetOrAdd(1);
            value = 42.0f;

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual(42.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void GetOrAdd_ExistingKey_ReturnsExisting()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);

            ref var value = ref dict.GetOrAdd(1);

            NAssert.AreEqual(10.0f, value, 0.001f);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region Remove

        [Test]
        public void Remove_Existing_ReturnsTrueAndDecreases()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);

            bool removed = dict.Remove(1);

            NAssert.IsTrue(removed);
            NAssert.AreEqual(1, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(1));
            NAssert.IsTrue(dict.ContainsKey(2));

            dict.Dispose();
        }

        [Test]
        public void Remove_Missing_ReturnsFalse()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);

            bool removed = dict.Remove(99);

            NAssert.IsFalse(removed);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Remove_WithOutput_ReturnsIndexAndValue()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);

            bool removed = dict.Remove(1, out var index, out var value);

            NAssert.IsTrue(removed);
            NAssert.AreEqual(0, index);
            NAssert.AreEqual(10.0f, value, 0.001f);
            NAssert.AreEqual(0, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region Clear

        [Test]
        public void Clear_RemovesAll()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);
            dict.Add(3, 3.0f);

            dict.Clear();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty);
            NAssert.IsFalse(dict.ContainsKey(1));

            dict.Dispose();
        }

        #endregion

        #region Enumeration

        [Test]
        public void Enumeration_IteratesAllPairs()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            int count = 0;
            float sum = 0;
            foreach (var (key, value) in dict)
            {
                count++;
                sum += value;
            }

            NAssert.AreEqual(3, count);
            NAssert.AreEqual(6.0f, sum, 0.001f);

            dict.Dispose();
        }

        [Test]
        public void Keys_IteratesAllKeys()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            int keySum = 0;
            int keyCount = 0;
            foreach (var key in dict.Keys)
            {
                keySum += key;
                keyCount++;
            }

            NAssert.AreEqual(3, keyCount);
            NAssert.AreEqual(60, keySum);

            dict.Dispose();
        }

        #endregion

        #region Capacity

        [Test]
        public void EnsureCapacity_IncreasesCapacity()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.EnsureCapacity(100);

            // Should be able to add 100 items without reallocation
            for (int i = 0; i < 100; i++)
            {
                dict.Add(i, i * 0.5f);
            }

            NAssert.AreEqual(100, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Trim_ReducesToCount()
        {
            var dict = new NativeIterableDictionary<int, float>(100, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);

            dict.Trim();

            NAssert.AreEqual(2, dict.Count);
            NAssert.AreEqual(1.0f, dict[1], 0.001f);
            NAssert.AreEqual(2.0f, dict[2], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region SetRange

        [Test]
        public void SetRange_IteratesCorrectSubset()
        {
            var dict = new NativeIterableDictionary<int, float>(8, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);
            dict.Add(40, 4.0f);
            dict.Add(50, 5.0f);

            var enumerator = dict.GetEnumerator();
            enumerator.SetRange(2, 2);

            int count = 0;
            float sum = 0;
            while (enumerator.MoveNext())
            {
                count++;
                sum += enumerator.Current.Value;
            }

            NAssert.AreEqual(2, count, "SetRange(2, 2) should iterate exactly 2 elements");
            NAssert.AreEqual(3.0f + 4.0f, sum, 0.001f);

            dict.Dispose();
        }

        #endregion

        #region Recycle

        [Test]
        public void Recycle_ClearsAll()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);
            dict.Add(3, 3.0f);

            dict.Recycle();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty);
            NAssert.IsFalse(dict.ContainsKey(1));
            NAssert.IsFalse(dict.ContainsKey(2));
            NAssert.IsFalse(dict.ContainsKey(3));

            dict.Dispose();
        }

        [Test]
        public void Recycle_CanAddAfter()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);

            dict.Recycle();

            dict.Add(10, 10.0f);
            dict.Add(20, 20.0f);
            dict.Add(1, 99.0f);

            NAssert.AreEqual(3, dict.Count);
            NAssert.AreEqual(10.0f, dict[10], 0.001f);
            NAssert.AreEqual(20.0f, dict[20], 0.001f);
            NAssert.AreEqual(99.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void Recycle_EmptyDict_NoOp()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict.Recycle();

            NAssert.AreEqual(0, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region Indexer Set

        [Test]
        public void IndexerSet_NewKey_Adds()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);

            dict[5] = 50.0f;

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual(50.0f, dict[5], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void IndexerSet_ExistingKey_Updates()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(5, 50.0f);

            dict[5] = 99.0f;

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual(99.0f, dict[5], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region GetValueAtIndexByRef

        [Test]
        public void GetValueAtIndexByRef_ModifiesInPlace()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);
            dict.Add(2, 20.0f);

            ref var val = ref dict.GetValueAtIndexByRef(1);
            val = 99.0f;

            NAssert.AreEqual(99.0f, dict[2], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region UnsafeValues

        [Test]
        public void UnsafeValues_ReturnsCorrectBuffer()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);
            dict.Add(2, 20.0f);
            dict.Add(3, 30.0f);

            var buffer = dict.UnsafeValues;

            NAssert.AreEqual(3, buffer.Length);
            NAssert.AreEqual(10.0f, buffer[0], 0.001f);
            NAssert.AreEqual(20.0f, buffer[1], 0.001f);
            NAssert.AreEqual(30.0f, buffer[2], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region IncreaseCapacityBy

        [Test]
        public void IncreaseCapacityBy_AllowsMoreAdds()
        {
            var dict = new NativeIterableDictionary<int, float>(2, Allocator.Temp);

            dict.IncreaseCapacityBy(50);

            for (int i = 0; i < 52; i++)
                dict.Add(i, i * 0.1f);

            NAssert.AreEqual(52, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region Hash Collision Handling

        [Test]
        public void Collisions_SameHashDifferentKeys_AllAccessible()
        {
            // Use keys that are likely to collide in a small bucket count
            var dict = new NativeIterableDictionary<int, int>(1, Allocator.Temp);

            // Add many keys to force collisions
            for (int i = 0; i < 20; i++)
                dict.Add(i, i * 100);

            // All should be accessible
            for (int i = 0; i < 20; i++)
            {
                NAssert.IsTrue(dict.ContainsKey(i), $"Key {i} should exist");
                NAssert.AreEqual(i * 100, dict[i]);
            }

            dict.Dispose();
        }

        [Test]
        public void Collisions_RemoveFromMiddleOfChain()
        {
            // Use tiny initial size to force collisions
            var dict = new NativeIterableDictionary<int, int>(1, Allocator.Temp);

            dict.Add(0, 0);
            dict.Add(1, 100);
            dict.Add(2, 200);
            dict.Add(3, 300);
            dict.Add(4, 400);

            // Remove elements that might be in middle of chains
            dict.Remove(2);

            NAssert.AreEqual(4, dict.Count);
            NAssert.IsTrue(dict.ContainsKey(0));
            NAssert.IsTrue(dict.ContainsKey(1));
            NAssert.IsFalse(dict.ContainsKey(2));
            NAssert.IsTrue(dict.ContainsKey(3));
            NAssert.IsTrue(dict.ContainsKey(4));

            NAssert.AreEqual(0, dict[0]);
            NAssert.AreEqual(100, dict[1]);
            NAssert.AreEqual(300, dict[3]);
            NAssert.AreEqual(400, dict[4]);

            dict.Dispose();
        }

        #endregion

        #region Remove Swap-Back

        [Test]
        public void Remove_NonLastElement_SwapBackPreservesValues()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(10, 100);
            dict.Add(20, 200);
            dict.Add(30, 300);
            dict.Add(40, 400);
            dict.Add(50, 500);

            // Remove first element — last element (50) should swap into slot 0
            dict.Remove(10);

            NAssert.AreEqual(4, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(10));
            NAssert.AreEqual(200, dict[20]);
            NAssert.AreEqual(300, dict[30]);
            NAssert.AreEqual(400, dict[40]);
            NAssert.AreEqual(500, dict[50]);

            dict.Dispose();
        }

        [Test]
        public void Remove_LastElement_NoSwapNeeded()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(10, 100);
            dict.Add(20, 200);
            dict.Add(30, 300);

            // Remove last-added element — no swap needed
            dict.Remove(30);

            NAssert.AreEqual(2, dict.Count);
            NAssert.AreEqual(100, dict[10]);
            NAssert.AreEqual(200, dict[20]);
            NAssert.IsFalse(dict.ContainsKey(30));

            dict.Dispose();
        }

        [Test]
        public void Remove_AllFromFront_OneByOne()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            for (int i = 0; i < 10; i++)
                dict.Add(i, i * 10);

            // Always remove the key that was originally first
            // This exercises swap-back repeatedly
            for (int i = 0; i < 10; i++)
            {
                dict.Remove(i);
                for (int j = i + 1; j < 10; j++)
                {
                    NAssert.IsTrue(dict.ContainsKey(j), $"Key {j} missing after removing {i}");
                    NAssert.AreEqual(j * 10, dict[j]);
                }
            }

            NAssert.AreEqual(0, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Remove_Interleaved_AddRemoveAdd()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            dict.Add(1, 10);
            dict.Add(2, 20);
            dict.Add(3, 30);
            dict.Remove(2);
            dict.Add(4, 40);
            dict.Remove(1);
            dict.Add(5, 50);
            dict.Remove(3);
            dict.Add(6, 60);

            NAssert.AreEqual(3, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(1));
            NAssert.IsFalse(dict.ContainsKey(2));
            NAssert.IsFalse(dict.ContainsKey(3));
            NAssert.IsTrue(dict.ContainsKey(4));
            NAssert.IsTrue(dict.ContainsKey(5));
            NAssert.IsTrue(dict.ContainsKey(6));
            NAssert.AreEqual(40, dict[4]);
            NAssert.AreEqual(50, dict[5]);
            NAssert.AreEqual(60, dict[6]);

            dict.Dispose();
        }

        #endregion

        #region Struct Copy Safety

        [Test]
        public void StructCopy_SharesDataBuffer()
        {
            // UnsafeList copies share the data Ptr but NOT the Length field.
            // Modifying existing values is visible through copies; structural
            // changes (Add/Remove) are NOT because Length is a struct field.
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);

            var copy = dict;

            // Modify an existing value through the copy's indexer
            copy[1] = 99;

            // Visible through original (shared data pointer)
            NAssert.AreEqual(99, dict[1]);

            dict.Dispose();
        }

        [Test]
        public void StructCopy_SharesHeader()
        {
            // Header* (Collisions, FastModBucketsMultiplier) is shared between copies
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);

            var copy = dict;

            // Both see same Count (Length hasn't diverged yet)
            NAssert.AreEqual(1, copy.Count);
            NAssert.IsTrue(copy.ContainsKey(1));
            NAssert.AreEqual(10, copy[1]);

            dict.Dispose();
        }

        #endregion

        #region Deterministic Iteration

        [Test]
        public void Iteration_OrderMatchesInsertion()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);

            int[] keys = { 42, 7, 99, 3, 55, 12 };
            for (int i = 0; i < keys.Length; i++)
                dict.Add(keys[i], i);

            int idx = 0;
            foreach (var (key, value) in dict)
            {
                NAssert.AreEqual(
                    keys[idx],
                    key,
                    $"Key at index {idx} should be {keys[idx]} but was {key}"
                );
                NAssert.AreEqual(idx, value);
                idx++;
            }

            NAssert.AreEqual(keys.Length, idx);

            dict.Dispose();
        }

        [Test]
        public void Iteration_AfterRemove_StillDeterministic()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);
            dict.Add(10, 0);
            dict.Add(20, 1);
            dict.Add(30, 2);
            dict.Add(40, 3);
            dict.Add(50, 4);

            // Remove middle element — last element (50) swaps into that slot
            dict.Remove(20);

            // Expected order after removing key 20 (index 1):
            // 50 moved to index 1, so order is: 10, 50, 30, 40
            var iteratedKeys = new int[4];
            int i = 0;
            foreach (var (key, _) in dict)
                iteratedKeys[i++] = key;

            NAssert.AreEqual(4, i);
            NAssert.AreEqual(10, iteratedKeys[0]);
            NAssert.AreEqual(50, iteratedKeys[1]);
            NAssert.AreEqual(30, iteratedKeys[2]);
            NAssert.AreEqual(40, iteratedKeys[3]);

            dict.Dispose();
        }

        [Test]
        public void Keys_OrderMatchesIteration()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);
            dict.Add(100, 1);
            dict.Add(200, 2);
            dict.Add(300, 3);

            var keysFromPairs = new int[3];
            var keysFromEnum = new int[3];

            int idx = 0;
            foreach (var (key, _) in dict)
                keysFromPairs[idx++] = key;

            idx = 0;
            foreach (var key in dict.Keys)
                keysFromEnum[idx++] = key;

            NAssert.AreEqual(keysFromPairs[0], keysFromEnum[0]);
            NAssert.AreEqual(keysFromPairs[1], keysFromEnum[1]);
            NAssert.AreEqual(keysFromPairs[2], keysFromEnum[2]);

            dict.Dispose();
        }

        #endregion

        #region Rehash

        [Test]
        public void Rehash_ManyAdds_AllKeysAccessible()
        {
            // Start with tiny capacity to force multiple rehashes
            var dict = new NativeIterableDictionary<int, int>(1, Allocator.Temp);

            for (int i = 0; i < 500; i++)
                dict.Add(i, i * 7);

            NAssert.AreEqual(500, dict.Count);

            for (int i = 0; i < 500; i++)
            {
                NAssert.IsTrue(dict.ContainsKey(i), $"Key {i} missing after rehash");
                NAssert.AreEqual(i * 7, dict[i]);
            }

            dict.Dispose();
        }

        [Test]
        public void Rehash_IterationOrderPreserved()
        {
            var dict = new NativeIterableDictionary<int, int>(1, Allocator.Temp);

            // Add enough to trigger multiple rehashes
            int[] insertOrder = new int[50];
            for (int i = 0; i < 50; i++)
            {
                int key = (i * 37) % 1000;
                insertOrder[i] = key;
                dict.Add(key, i);
            }

            // Verify iteration order matches insertion order
            int idx = 0;
            foreach (var (key, _) in dict)
            {
                NAssert.AreEqual(insertOrder[idx], key, $"Iteration order mismatch at index {idx}");
                idx++;
            }

            dict.Dispose();
        }

        #endregion

        #region Clear Then Reuse

        [Test]
        public void Clear_ThenReuse_WorksCorrectly()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            for (int i = 0; i < 20; i++)
                dict.Add(i, i);

            dict.Clear();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(0));

            // Re-add with overlapping and new keys
            for (int i = 10; i < 30; i++)
                dict.Add(i, i * 2);

            NAssert.AreEqual(20, dict.Count);
            for (int i = 10; i < 30; i++)
                NAssert.AreEqual(i * 2, dict[i]);

            dict.Dispose();
        }

        [Test]
        public void Recycle_ThenReuse_WorksCorrectly()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            for (int i = 0; i < 20; i++)
                dict.Add(i, i);

            dict.Recycle();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(0));

            for (int i = 10; i < 30; i++)
                dict.Add(i, i * 2);

            NAssert.AreEqual(20, dict.Count);
            for (int i = 10; i < 30; i++)
                NAssert.AreEqual(i * 2, dict[i]);

            dict.Dispose();
        }

        #endregion

        #region TryGetIndex / GetIndex

        [Test]
        public void TryGetIndex_ReturnsCorrectDenseIndex()
        {
            var dict = new NativeIterableDictionary<int, float>(8, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            NAssert.IsTrue(dict.TryGetIndex(10, out var idx0));
            NAssert.AreEqual(0, idx0);

            NAssert.IsTrue(dict.TryGetIndex(20, out var idx1));
            NAssert.AreEqual(1, idx1);

            NAssert.IsTrue(dict.TryGetIndex(30, out var idx2));
            NAssert.AreEqual(2, idx2);

            NAssert.IsFalse(dict.TryGetIndex(99, out _));

            dict.Dispose();
        }

        [Test]
        public void GetIndex_AfterRemove_ReflectsSwap()
        {
            var dict = new NativeIterableDictionary<int, float>(8, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            dict.Remove(10);

            // Key 30 should have swapped to index 0
            NAssert.IsTrue(dict.TryGetIndex(30, out var idx30));
            NAssert.AreEqual(0, idx30);

            NAssert.IsTrue(dict.TryGetIndex(20, out var idx20));
            NAssert.AreEqual(1, idx20);

            dict.Dispose();
        }

        #endregion

        #region GetOrAdd Overloads

        [Test]
        public void GetOrAdd_WithBuilder_CallsBuilderOnlyForNew()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);

            int callCount = 0;
            ref var existing = ref dict.GetOrAdd(
                1,
                () =>
                {
                    callCount++;
                    return 99;
                }
            );
            NAssert.AreEqual(10, existing);
            NAssert.AreEqual(0, callCount);

            ref var added = ref dict.GetOrAdd(
                2,
                () =>
                {
                    callCount++;
                    return 42;
                }
            );
            NAssert.AreEqual(42, added);
            NAssert.AreEqual(1, callCount);

            dict.Dispose();
        }

        [Test]
        public void GetOrAdd_WithOutIndex_ReturnsCorrectIndex()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);

            ref var val1 = ref dict.GetOrAdd(1, out int idx1);
            NAssert.AreEqual(0, idx1);
            NAssert.AreEqual(10, val1);

            ref var val2 = ref dict.GetOrAdd(2, out int idx2);
            NAssert.AreEqual(1, idx2);
            val2 = 20;
            NAssert.AreEqual(20, dict[2]);

            dict.Dispose();
        }

        #endregion

        #region Struct Copy Full Sharing

        [Test]
        public void StructCopy_AddOnCopy_VisibleThroughOriginal()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);

            var copy = dict;
            copy.Add(2, 20);

            NAssert.AreEqual(2, dict.Count);
            NAssert.IsTrue(dict.ContainsKey(2));
            NAssert.AreEqual(20, dict[2]);

            dict.Dispose();
        }

        [Test]
        public void StructCopy_RemoveOnCopy_VisibleThroughOriginal()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);

            var copy = dict;
            copy.Remove(1);

            NAssert.AreEqual(1, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(1));
            NAssert.IsTrue(dict.ContainsKey(2));

            dict.Dispose();
        }

        [Test]
        public void StructCopy_ClearOnCopy_VisibleThroughOriginal()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);

            var copy = dict;
            copy.Clear();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty);

            dict.Dispose();
        }

        #endregion

        #region Set Correctness

        [Test]
        public void Set_NonExistentKey_DoesNotAdd()
        {
            var dict = new NativeIterableDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);

            // Set on non-existent key should not increase count
            // (in release it's a no-op to the wrong slot; in debug it throws)
            int countBefore = dict.Count;
            dict.Set(99, 5.0f);
            NAssert.AreEqual(countBefore, dict.Count);
            NAssert.IsFalse(dict.ContainsKey(99));

            dict.Dispose();
        }

        #endregion

        #region Constructor Edge Cases

        [Test]
        public void Constructor_SizeZero_WorksCorrectly()
        {
            var dict = new NativeIterableDictionary<int, int>(0, Allocator.Temp);

            NAssert.IsTrue(dict.IsCreated);
            NAssert.AreEqual(0, dict.Count);

            dict.Add(1, 10);
            dict.Add(2, 20);

            NAssert.AreEqual(2, dict.Count);
            NAssert.AreEqual(10, dict[1]);
            NAssert.AreEqual(20, dict[2]);

            dict.Dispose();
        }

        [Test]
        public void Constructor_LargeSize_WorksCorrectly()
        {
            var dict = new NativeIterableDictionary<int, int>(10000, Allocator.Temp);

            for (int i = 0; i < 10000; i++)
                dict.Add(i, i);

            NAssert.AreEqual(10000, dict.Count);
            NAssert.AreEqual(0, dict[0]);
            NAssert.AreEqual(9999, dict[9999]);

            dict.Dispose();
        }

        #endregion

        #region SetRange Edge Cases

        [Test]
        public void SetRange_StartZero_CountAll()
        {
            var dict = new NativeIterableDictionary<int, float>(8, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            var enumerator = dict.GetEnumerator();
            enumerator.SetRange(0, 3);

            int count = 0;
            float sum = 0;
            while (enumerator.MoveNext())
            {
                count++;
                sum += enumerator.Current.Value;
            }

            NAssert.AreEqual(3, count);
            NAssert.AreEqual(6.0f, sum, 0.001f);

            dict.Dispose();
        }

        [Test]
        public void SetRange_LastElement_Only()
        {
            var dict = new NativeIterableDictionary<int, float>(8, Allocator.Temp);
            dict.Add(10, 1.0f);
            dict.Add(20, 2.0f);
            dict.Add(30, 3.0f);

            var enumerator = dict.GetEnumerator();
            enumerator.SetRange(2, 1);

            int count = 0;
            while (enumerator.MoveNext())
                count++;

            NAssert.AreEqual(1, count);
            NAssert.AreEqual(3.0f, enumerator.Current.Value, 0.001f);

            dict.Dispose();
        }

        #endregion

        #region KeyValuePairFast Ref Modification

        [Test]
        public void Enumerator_ValueRef_ModifiesInPlace()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);
            dict.Add(3, 30);

            var enumerator = dict.GetEnumerator();
            while (enumerator.MoveNext())
            {
                enumerator.Current.Value *= 2;
            }

            NAssert.AreEqual(20, dict[1]);
            NAssert.AreEqual(40, dict[2]);
            NAssert.AreEqual(60, dict[3]);

            dict.Dispose();
        }

        #endregion

        #region Trim After Removes

        [Test]
        public void Trim_AfterRemoves_ShrinksProperly()
        {
            var dict = new NativeIterableDictionary<int, int>(100, Allocator.Temp);
            for (int i = 0; i < 50; i++)
                dict.Add(i, i);

            // Remove most
            for (int i = 10; i < 50; i++)
                dict.Remove(i);

            NAssert.AreEqual(10, dict.Count);

            dict.Trim();

            // Verify data integrity after trim
            for (int i = 0; i < 10; i++)
            {
                NAssert.IsTrue(dict.ContainsKey(i), $"Key {i} should exist after trim");
            }
            NAssert.AreEqual(10, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region EnsureCapacity Edge Cases

        [Test]
        public void EnsureCapacity_SmallerThanCurrent_NoOp()
        {
            var dict = new NativeIterableDictionary<int, int>(100, Allocator.Temp);
            for (int i = 0; i < 50; i++)
                dict.Add(i, i);

            // Should be no-op — capacity already >= 100
            dict.EnsureCapacity(10);

            // Everything still works
            for (int i = 0; i < 50; i++)
                NAssert.AreEqual(i, dict[i]);

            dict.Dispose();
        }

        #endregion

        #region Rehash Preserves Lookup After Many Collisions

        [Test]
        public void ManyCollisions_ThenRehash_AllAccessible()
        {
            // Start with absolute minimum to maximize collisions before rehash
            var dict = new NativeIterableDictionary<int, int>(0, Allocator.Temp);

            for (int i = 0; i < 100; i++)
                dict.Add(i * 7, i);

            // Verify all entries survive repeated rehashes
            for (int i = 0; i < 100; i++)
            {
                NAssert.IsTrue(dict.ContainsKey(i * 7), $"Key {i * 7} missing");
                NAssert.AreEqual(i, dict[i * 7]);
            }

            dict.Dispose();
        }

        #endregion

        #region Remove Then Add Same Key

        [Test]
        public void Remove_ThenAddSameKey_WorksCorrectly()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);
            dict.Add(3, 30);

            dict.Remove(2);
            dict.Add(2, 200);

            NAssert.AreEqual(3, dict.Count);
            NAssert.AreEqual(10, dict[1]);
            NAssert.AreEqual(200, dict[2]);
            NAssert.AreEqual(30, dict[3]);

            dict.Dispose();
        }

        [Test]
        public void Remove_ThenAddSameKey_Repeated()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            for (int round = 0; round < 20; round++)
            {
                dict.Add(42, round);
                NAssert.AreEqual(round, dict[42]);
                dict.Remove(42);
                NAssert.IsFalse(dict.ContainsKey(42));
            }

            NAssert.AreEqual(0, dict.Count);

            dict.Dispose();
        }

        #endregion

        #region Stress

        [Test]
        public void Stress_AddRemoveMany_ConsistentState()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);

            // Add 100 items
            for (int i = 0; i < 100; i++)
            {
                dict.Add(i, i * 10);
            }

            NAssert.AreEqual(100, dict.Count);

            // Remove even keys
            for (int i = 0; i < 100; i += 2)
            {
                dict.Remove(i);
            }

            NAssert.AreEqual(50, dict.Count);

            // Verify odd keys remain with correct values
            for (int i = 1; i < 100; i += 2)
            {
                NAssert.IsTrue(dict.ContainsKey(i));
                NAssert.AreEqual(i * 10, dict[i]);
            }

            dict.Dispose();
        }

        [Test]
        public void Stress_AddAfterRemove_ReusesSlots()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);

            for (int i = 0; i < 10; i++)
                dict.Add(i, i);

            // Remove all
            for (int i = 0; i < 10; i++)
                dict.Remove(i);

            NAssert.AreEqual(0, dict.Count);

            // Re-add with different values
            for (int i = 0; i < 10; i++)
                dict.Add(i, i * 100);

            NAssert.AreEqual(10, dict.Count);

            for (int i = 0; i < 10; i++)
                NAssert.AreEqual(i * 100, dict[i]);

            dict.Dispose();
        }

        [Test]
        public void Stress_RemoveReverse_AllCorrect()
        {
            var dict = new NativeIterableDictionary<int, int>(16, Allocator.Temp);

            for (int i = 0; i < 100; i++)
                dict.Add(i, i);

            // Remove in reverse order (always removing the last — no swap needed)
            for (int i = 99; i >= 0; i--)
            {
                NAssert.IsTrue(dict.Remove(i));
                NAssert.AreEqual(i, dict.Count);
            }

            dict.Dispose();
        }

        [Test]
        public void Stress_RandomRemovePattern_Consistent()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            // Add 200 items
            for (int i = 0; i < 200; i++)
                dict.Add(i, i);

            // Remove keys divisible by 3
            for (int i = 0; i < 200; i += 3)
                dict.Remove(i);

            // Verify remaining
            int expectedCount = 0;
            for (int i = 0; i < 200; i++)
            {
                if (i % 3 == 0)
                {
                    NAssert.IsFalse(dict.ContainsKey(i), $"Key {i} should have been removed");
                }
                else
                {
                    NAssert.IsTrue(dict.ContainsKey(i), $"Key {i} should still exist");
                    NAssert.AreEqual(i, dict[i]);
                    expectedCount++;
                }
            }
            NAssert.AreEqual(expectedCount, dict.Count);

            // Add more after removes
            for (int i = 200; i < 300; i++)
                dict.Add(i, i);

            for (int i = 200; i < 300; i++)
                NAssert.AreEqual(i, dict[i]);

            dict.Dispose();
        }

        [Test]
        public void Stress_RepeatedClearAndFill()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            for (int round = 0; round < 10; round++)
            {
                for (int i = 0; i < 50; i++)
                    dict.Add(i + round * 1000, i);

                NAssert.AreEqual(50, dict.Count);

                for (int i = 0; i < 50; i++)
                    NAssert.AreEqual(i, dict[i + round * 1000]);

                dict.Clear();
                NAssert.AreEqual(0, dict.Count);
            }

            dict.Dispose();
        }

        [Test]
        public void Stress_InterleavedAddRemoveWithCollisions()
        {
            var dict = new NativeIterableDictionary<int, int>(1, Allocator.Temp);

            for (int round = 0; round < 5; round++)
            {
                // Add batch
                for (int i = round * 100; i < (round + 1) * 100; i++)
                    dict.Add(i, i);

                // Remove half of previous batch
                if (round > 0)
                {
                    for (int i = (round - 1) * 100; i < (round - 1) * 100 + 50; i++)
                        dict.Remove(i);
                }
            }

            // Verify all expected keys are present
            for (int round = 0; round < 5; round++)
            {
                int start = round * 100;
                int removeEnd = (round < 4) ? start + 50 : start;

                for (int i = start; i < start + 100; i++)
                {
                    if (round < 4 && i < start + 50)
                        NAssert.IsFalse(dict.ContainsKey(i), $"Key {i} should be removed");
                    else
                        NAssert.IsTrue(dict.ContainsKey(i), $"Key {i} should exist");
                }
            }

            dict.Dispose();
        }

        [Test]
        public void Stress_LargeScale_1000Items()
        {
            var dict = new NativeIterableDictionary<int, long>(16, Allocator.Temp);

            for (int i = 0; i < 1000; i++)
                dict.Add(i, (long)i * i);

            NAssert.AreEqual(1000, dict.Count);

            // Verify random access
            NAssert.AreEqual(0L, dict[0]);
            NAssert.AreEqual(250000L, dict[500]);
            NAssert.AreEqual(998001L, dict[999]);

            // Remove every 3rd
            for (int i = 0; i < 1000; i += 3)
                dict.Remove(i);

            // Verify remaining
            for (int i = 0; i < 1000; i++)
            {
                if (i % 3 == 0)
                    NAssert.IsFalse(dict.ContainsKey(i));
                else
                {
                    NAssert.IsTrue(dict.ContainsKey(i));
                    NAssert.AreEqual((long)i * i, dict[i]);
                }
            }

            dict.Dispose();
        }

        #endregion

        #region UnsafeValues After Modification

        [Test]
        public void UnsafeValues_AfterAdd_ReflectsNewValues()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);

            var buffer1 = dict.UnsafeValues;
            NAssert.AreEqual(2, buffer1.Length);

            dict.Add(3, 30);

            var buffer2 = dict.UnsafeValues;
            NAssert.AreEqual(3, buffer2.Length);
            NAssert.AreEqual(30, buffer2[2]);

            dict.Dispose();
        }

        [Test]
        public void UnsafeValues_AfterRemove_ReflectsSwap()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);
            dict.Add(3, 30);

            dict.Remove(1);

            var buffer = dict.UnsafeValues;
            NAssert.AreEqual(2, buffer.Length);
            // After removing index 0, last element (30) swaps into slot 0
            NAssert.AreEqual(30, buffer[0]);
            NAssert.AreEqual(20, buffer[1]);

            dict.Dispose();
        }

        #endregion

        #region Iteration Order After Complex Operations

        [Test]
        public void IterationOrder_AfterMultipleRemovesAndAdds_Correct()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(1, 10);
            dict.Add(2, 20);
            dict.Add(3, 30);
            dict.Add(4, 40);
            dict.Add(5, 50);

            // Remove from middle: 5 swaps to index of 3
            dict.Remove(3);
            // Remove from front: 4 swaps to index of 1 (was index 3, but 5 moved there...
            // actually 4 is still at its original index 3... let me think:
            // After Remove(3): [1,2,5,4] (5 was last, swapped to index 2 where 3 was)
            dict.Remove(1);
            // After Remove(1): [4,2,5] (4 was last at index 3, swapped to index 0 where 1 was)

            dict.Add(6, 60);
            dict.Add(7, 70);
            // Now: [4,2,5,6,7]

            int[] expectedKeys = { 4, 2, 5, 6, 7 };
            int idx = 0;
            foreach (var (key, _) in dict)
            {
                NAssert.AreEqual(expectedKeys[idx], key, $"Mismatch at position {idx}");
                idx++;
            }
            NAssert.AreEqual(5, idx);

            dict.Dispose();
        }

        #endregion

        #region GetOrAdd With Index

        [Test]
        public void GetOrAdd_WithIndex_ConsecutiveAdds_CorrectIndices()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);

            ref var v0 = ref dict.GetOrAdd(10, out int idx0);
            v0 = 100;
            ref var v1 = ref dict.GetOrAdd(20, out int idx1);
            v1 = 200;
            ref var v2 = ref dict.GetOrAdd(30, out int idx2);
            v2 = 300;

            NAssert.AreEqual(0, idx0);
            NAssert.AreEqual(1, idx1);
            NAssert.AreEqual(2, idx2);

            // Existing key returns same index
            ref var v0Again = ref dict.GetOrAdd(10, out int idx0Again);
            NAssert.AreEqual(0, idx0Again);
            NAssert.AreEqual(100, v0Again);

            dict.Dispose();
        }

        #endregion

        #region Dispose Idempotency

        [Test]
        public void IsCreated_AfterDispose_ReturnsFalse()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(1, 10);

            dict.Dispose();

            NAssert.IsFalse(dict.IsCreated);
        }

        #endregion

        #region TryAdd Returns Correct Index For Existing

        [Test]
        public void TryAdd_ExistingKey_ReturnsExistingIndex()
        {
            var dict = new NativeIterableDictionary<int, int>(4, Allocator.Temp);
            dict.Add(10, 100);
            dict.Add(20, 200);
            dict.Add(30, 300);

            bool added = dict.TryAdd(20, 999, out int index);

            NAssert.IsFalse(added);
            NAssert.AreEqual(1, index);
            NAssert.AreEqual(200, dict[20]);

            dict.Dispose();
        }

        #endregion

        #region Keys After Structural Modifications

        [Test]
        public void Keys_AfterRemove_ReflectsSwapBack()
        {
            var dict = new NativeIterableDictionary<int, int>(8, Allocator.Temp);
            dict.Add(10, 1);
            dict.Add(20, 2);
            dict.Add(30, 3);
            dict.Add(40, 4);

            dict.Remove(20);
            // 40 swaps to index 1

            int[] expectedKeys = { 10, 40, 30 };
            int idx = 0;
            foreach (var key in dict.Keys)
            {
                NAssert.AreEqual(expectedKeys[idx], key, $"Key mismatch at index {idx}");
                idx++;
            }
            NAssert.AreEqual(3, idx);

            dict.Dispose();
        }

        #endregion
    }
}

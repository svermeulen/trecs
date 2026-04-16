using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class NativeDenseDictionaryTests
    {
        #region Construction

        [Test]
        public void Constructor_CreatesEmpty()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            NAssert.IsTrue(dict.IsCreated);
            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty());

            dict.Dispose();
        }

        #endregion

        #region Add / Set

        [Test]
        public void Add_NewKey_IncreasesCount()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.5f);

            NAssert.AreEqual(1, dict.Count);
            NAssert.IsFalse(dict.IsEmpty());

            dict.Dispose();
        }

        [Test]
        public void Add_MultipleKeys_AllPresent()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            bool added = dict.TryAdd(1, 1.5f, out var index);

            NAssert.IsTrue(added);
            NAssert.AreEqual(0, index);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void TryAdd_DuplicateKey_ReturnsFalse()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            dict.Add(1, 1.0f);
            bool added = dict.TryAdd(1, 2.0f, out _);

            NAssert.IsFalse(added);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Set_ExistingKey_UpdatesValue()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(42, 3.14f);

            NAssert.IsTrue(dict.ContainsKey(42));

            dict.Dispose();
        }

        [Test]
        public void ContainsKey_Missing_ReturnsFalse()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            NAssert.IsFalse(dict.ContainsKey(42));

            dict.Dispose();
        }

        [Test]
        public void TryGetValue_Existing_ReturnsTrueAndValue()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 3.14f);

            bool found = dict.TryGetValue(1, out var value);

            NAssert.IsTrue(found);
            NAssert.AreEqual(3.14f, value, 0.001f);

            dict.Dispose();
        }

        [Test]
        public void TryGetValue_Missing_ReturnsFalse()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            bool found = dict.TryGetValue(1, out _);

            NAssert.IsFalse(found);

            dict.Dispose();
        }

        [Test]
        public void Indexer_ReturnsCorrectValue()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(5, 50.0f);

            NAssert.AreEqual(50.0f, dict[5], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region GetValueByRef / GetOrAdd

        [Test]
        public void GetValueByRef_ModifiesInPlace()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 10.0f);

            ref var value = ref dict.GetValueByRef(1);
            value = 99.0f;

            NAssert.AreEqual(99.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void GetOrAdd_NewKey_AddsDefault()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

            ref var value = ref dict.GetOrAdd(1);
            value = 42.0f;

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual(42.0f, dict[1], 0.001f);

            dict.Dispose();
        }

        [Test]
        public void GetOrAdd_ExistingKey_ReturnsExisting()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);

            bool removed = dict.Remove(99);

            NAssert.IsFalse(removed);
            NAssert.AreEqual(1, dict.Count);

            dict.Dispose();
        }

        [Test]
        public void Remove_WithOutput_ReturnsIndexAndValue()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);
            dict.Add(3, 3.0f);

            dict.Clear();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty());
            NAssert.IsFalse(dict.ContainsKey(1));

            dict.Dispose();
        }

        #endregion

        #region Enumeration

        [Test]
        public void Enumeration_IteratesAllPairs()
        {
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);
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
            var dict = new NativeDenseDictionary<int, float>(4, Allocator.Temp);

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
            var dict = new NativeDenseDictionary<int, float>(100, Allocator.Temp);
            dict.Add(1, 1.0f);
            dict.Add(2, 2.0f);

            dict.Trim();

            NAssert.AreEqual(2, dict.Count);
            NAssert.AreEqual(1.0f, dict[1], 0.001f);
            NAssert.AreEqual(2.0f, dict[2], 0.001f);

            dict.Dispose();
        }

        #endregion

        #region Stress

        [Test]
        public void Stress_AddRemoveMany_ConsistentState()
        {
            var dict = new NativeDenseDictionary<int, int>(16, Allocator.Temp);

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
            var dict = new NativeDenseDictionary<int, int>(16, Allocator.Temp);

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

        #endregion
    }
}

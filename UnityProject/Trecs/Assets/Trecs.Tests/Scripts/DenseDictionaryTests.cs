using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class DenseDictionaryTests
    {
        private struct CollidingKey : IEquatable<CollidingKey>
        {
            public int Value;

            public CollidingKey(int value)
            {
                Value = value;
            }

            public bool Equals(CollidingKey other)
            {
                return Value == other.Value;
            }

            public override bool Equals(object obj)
            {
                return obj is CollidingKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return 42; // Intentional collision
            }
        }

        private class TestClass
        {
            public string Value { get; set; } = "";

            public void Reset()
            {
                Value = "";
            }
        }

        private enum TestEnum
        {
            First = 0,
            Second = 1,
            Third = 2,
            Fourth = 3,
        }

        private struct EnumWrapper : IEquatable<EnumWrapper>
        {
            public TestEnum Value { get; }

            public EnumWrapper(TestEnum value)
            {
                Value = value;
            }

            // Implicit conversion from enum to wrapper
            public static implicit operator EnumWrapper(TestEnum value)
            {
                return new EnumWrapper(value);
            }

            // Implicit conversion from wrapper to enum
            public static implicit operator TestEnum(EnumWrapper wrapper)
            {
                return wrapper.Value;
            }

            public bool Equals(EnumWrapper other)
            {
                return Value == other.Value;
            }

            public override bool Equals(object obj)
            {
                return obj is EnumWrapper other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (int)Value;
            }

            public override string ToString()
            {
                return Value.ToString();
            }
        }

        #region Construction

        [Test]
        public void Constructor_WithSize_CreatesEmptyDictionary()
        {
            var dict = new DenseDictionary<int, string>(10);
            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty(), "Dictionary should be empty");
        }

        [Test]
        public void Constructor_Default_CreatesEmptyDictionary()
        {
            var dict = new DenseDictionary<int, string>();
            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty(), "Dictionary should be empty");
        }

        #endregion

        #region Add / Set

        [Test]
        public void Add_NewKey_AddsSuccessfully()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            NAssert.AreEqual(1, dict.Count);
            NAssert.IsFalse(dict.IsEmpty(), "Dictionary should not be empty");
            NAssert.IsTrue(dict.ContainsKey(1), "Dictionary should contain key 1");
            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void Add_DuplicateKey_ThrowsException()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            NAssert.Catch(() => dict.Add(1, "duplicate"));
        }

        [Test]
        public void TryAdd_NewKey_ReturnsTrue()
        {
            var dict = new DenseDictionary<int, string>();
            var result = dict.TryAdd(1, "one", out var index);

            NAssert.IsTrue(result, "TryAdd should return true for new key");
            NAssert.AreEqual(0, index);
            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void TryAdd_DuplicateKey_ReturnsFalse()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            var result = dict.TryAdd(1, "duplicate", out var index);

            NAssert.IsFalse(result, "TryAdd should return false for duplicate key");
            NAssert.AreEqual(0, index);
            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void Set_ExistingKey_UpdatesValue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            // Set method expects the key to already exist and updates it
            dict.Set(1, "updated");

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("updated", dict[1]);
        }

        #endregion

        #region ContainsKey / TryGetValue / Indexer

        [Test]
        public void ContainsKey_ExistingKey_ReturnsTrue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            NAssert.IsTrue(dict.ContainsKey(1), "Dictionary should contain key 1");
        }

        [Test]
        public void ContainsKey_NonExistingKey_ReturnsFalse()
        {
            var dict = new DenseDictionary<int, string>();
            NAssert.IsFalse(dict.ContainsKey(1), "Dictionary should not contain key 1");
        }

        [Test]
        public void TryGetValue_ExistingKey_ReturnsTrue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            var result = dict.TryGetValue(1, out var value);

            NAssert.IsTrue(result, "TryGetValue should return true for existing key");
            NAssert.AreEqual("one", value);
        }

        [Test]
        public void TryGetValue_NonExistingKey_ReturnsFalse()
        {
            var dict = new DenseDictionary<int, string>();
            var result = dict.TryGetValue(1, out var value);

            NAssert.IsFalse(result, "TryGetValue should return false for non-existing key");
            NAssert.AreEqual(default(string), value);
        }

        [Test]
        public void Indexer_Get_ExistingKey_ReturnsValue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void Indexer_Set_NewKey_AddsValue()
        {
            var dict = new DenseDictionary<int, string>();
            dict[1] = "one";

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void Indexer_Set_ExistingKey_UpdatesValue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict[1] = "updated";

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("updated", dict[1]);
        }

        #endregion

        #region GetValueByRef / GetOrAdd

        [Test]
        public void GetValueByRef_ExistingKey_ReturnsReference()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            ref var value = ref dict.GetValueByRef(1);
            value = "updated";

            NAssert.AreEqual("updated", dict[1]);
        }

        [Test]
        public void GetOrAdd_NewKey_AddsAndReturnsReference()
        {
            var dict = new DenseDictionary<int, string>();
            ref var value = ref dict.GetOrAdd(1);
            value = "one";

            NAssert.AreEqual(1, dict.Count);
            NAssert.AreEqual("one", dict[1]);
        }

        [Test]
        public void GetOrAdd_ExistingKey_ReturnsExistingReference()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            ref var value = ref dict.GetOrAdd(1);

            NAssert.AreEqual("one", value);
            NAssert.AreEqual(1, dict.Count);
        }

        [Test]
        public void GetOrAdd_WithBuilder_NewKey_CreatesValue()
        {
            var dict = new DenseDictionary<int, string>();
            ref var value = ref dict.GetOrAdd(1, () => "created");

            NAssert.AreEqual("created", value);
            NAssert.AreEqual(1, dict.Count);
        }

        [Test]
        public void GetOrAdd_WithBuilder_ExistingKey_ReturnsExisting()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "existing");

            ref var value = ref dict.GetOrAdd(1, () => "created");

            NAssert.AreEqual("existing", value);
            NAssert.AreEqual(1, dict.Count);
        }

        [Test]
        public void GetOrAdd_WithIndex_ReturnsCorrectIndex()
        {
            var dict = new DenseDictionary<int, string>();
            ref var value = ref dict.GetOrAdd(1, out var index);
            value = "one";

            NAssert.AreEqual(0, index);
            NAssert.AreEqual("one", dict[1]);
        }

        #endregion

        #region Remove

        [Test]
        public void Remove_ExistingKey_RemovesAndReturnsTrue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            var result = dict.TryRemove(1);

            NAssert.IsTrue(result, "Remove should return true for existing key");
            NAssert.AreEqual(0, dict.Count);
            NAssert.IsFalse(
                dict.ContainsKey(1),
                "Dictionary should not contain key 1 after removal"
            );
        }

        [Test]
        public void Remove_NonExistingKey_ReturnsFalse()
        {
            var dict = new DenseDictionary<int, string>();
            var result = dict.TryRemove(1);

            NAssert.IsFalse(result, "Remove should return false for non-existing key");
            NAssert.AreEqual(0, dict.Count);
        }

        [Test]
        public void Remove_WithOutput_ReturnsIndexAndValue()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");

            var result = dict.TryRemove(1, out var index, out var value);

            NAssert.IsTrue(result, "Remove should return true for existing key");
            NAssert.AreEqual(0, index);
            NAssert.AreEqual("one", value);
            NAssert.AreEqual(0, dict.Count);
        }

        #endregion

        #region Clear / Recycle

        [Test]
        public void Clear_RemovesAllItems()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");

            dict.Clear();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty(), "Dictionary should be empty after clear");
            NAssert.IsFalse(dict.ContainsKey(1), "Dictionary should not contain key 1 after clear");
            NAssert.IsFalse(dict.ContainsKey(2), "Dictionary should not contain key 2 after clear");
        }

        [Test]
        public void Recycle_ResetsCount()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");

            dict.Recycle();

            NAssert.AreEqual(0, dict.Count);
            NAssert.IsTrue(dict.IsEmpty(), "Dictionary should be empty after recycle");
        }

        #endregion

        #region Enumeration

        [Test]
        public void GetEnumerator_IteratesOverKeyValuePairs()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");
            dict.Add(3, "three");

            var pairs = new List<(int key, string value)>();
            foreach (var kvp in dict)
            {
                pairs.Add((kvp.Key, kvp.Value));
            }

            NAssert.AreEqual(3, pairs.Count);
            NAssert.IsTrue(pairs.Contains((1, "one")), "Should contain (1, 'one')");
            NAssert.IsTrue(pairs.Contains((2, "two")), "Should contain (2, 'two')");
            NAssert.IsTrue(pairs.Contains((3, "three")), "Should contain (3, 'three')");
        }

        [Test]
        public void Keys_ReturnsAllKeys()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");
            dict.Add(3, "three");

            var keys = new List<int>();
            foreach (var key in dict.Keys)
            {
                keys.Add(key);
            }

            NAssert.AreEqual(3, keys.Count);
            NAssert.IsTrue(keys.Contains(1), "Should contain key 1");
            NAssert.IsTrue(keys.Contains(2), "Should contain key 2");
            NAssert.IsTrue(keys.Contains(3), "Should contain key 3");
        }

        [Test]
        public void UnsafeGetValues_ReturnsArrayWithCount()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");

            var values = dict.UnsafeGetValues(out var count);

            NAssert.AreEqual(2, count);
            var valuesList = new List<string>();
            for (int i = 0; i < count; i++)
            {
                valuesList.Add(values[i]);
            }
            NAssert.IsTrue(valuesList.Contains("one"), "Should contain 'one'");
            NAssert.IsTrue(valuesList.Contains("two"), "Should contain 'two'");
        }

        #endregion

        #region Capacity

        [Test]
        public void EnsureCapacity_IncreasesCapacity()
        {
            var dict = new DenseDictionary<int, string>();
            var initialCapacity = dict.UnsafeValues.Length;

            dict.EnsureCapacity(100);

            NAssert.IsTrue(
                dict.UnsafeValues.Length >= Math.Max(initialCapacity, 100),
                "Capacity should be at least 100"
            );
        }

        [Test]
        public void IncreaseCapacityBy_IncreasesCapacity()
        {
            var dict = new DenseDictionary<int, string>();
            var initialCapacity = dict.UnsafeValues.Length;

            dict.IncreaseCapacityBy(50);

            NAssert.IsTrue(
                dict.UnsafeValues.Length > initialCapacity,
                "Capacity should be greater than initial"
            );
        }

        [Test]
        public void Trim_ReducesCapacityToCount()
        {
            var dict = new DenseDictionary<int, string>();
            dict.EnsureCapacity(100);
            dict.Add(1, "one");
            dict.Add(2, "two");

            dict.Trim();

            NAssert.AreEqual(2, dict.UnsafeValues.Length);
        }

        #endregion

        #region Set Operations

        [Test]
        public void Union_MergesWithOtherDictionary()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");

            var other = new DenseDictionary<int, string>();
            other.Add(2, "updated_two");
            other.Add(3, "three");

            dict.Union(other);

            NAssert.AreEqual(3, dict.Count);
            NAssert.AreEqual("one", dict[1]);
            NAssert.AreEqual("updated_two", dict[2]);
            NAssert.AreEqual("three", dict[3]);
        }

        [Test]
        public void Intersect_KeepsOnlyCommonKeys()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");
            dict.Add(3, "three");

            var other = new DenseDictionary<int, string>();
            other.Add(2, "other_two");
            other.Add(3, "other_three");
            other.Add(4, "four");

            dict.Intersect(other);

            NAssert.AreEqual(2, dict.Count);
            NAssert.IsFalse(
                dict.ContainsKey(1),
                "Dictionary should not contain key 1 after intersect"
            );
            NAssert.IsTrue(dict.ContainsKey(2), "Dictionary should contain key 2 after intersect");
            NAssert.IsTrue(dict.ContainsKey(3), "Dictionary should contain key 3 after intersect");
            NAssert.AreEqual("two", dict[2]);
            NAssert.AreEqual("three", dict[3]);
        }

        [Test]
        public void Exclude_RemovesCommonKeys()
        {
            var dict = new DenseDictionary<int, string>();
            dict.Add(1, "one");
            dict.Add(2, "two");
            dict.Add(3, "three");

            var other = new DenseDictionary<int, string>();
            other.Add(2, "other_two");
            other.Add(4, "four");

            dict.Exclude(other);

            NAssert.AreEqual(2, dict.Count);
            NAssert.IsTrue(dict.ContainsKey(1), "Dictionary should contain key 1 after exclude");
            NAssert.IsFalse(
                dict.ContainsKey(2),
                "Dictionary should not contain key 2 after exclude"
            );
            NAssert.IsTrue(dict.ContainsKey(3), "Dictionary should contain key 3 after exclude");
            NAssert.AreEqual("one", dict[1]);
            NAssert.AreEqual("three", dict[3]);
        }

        #endregion

        #region Stress / Edge Cases

        [Test]
        public void MultipleOperations_MaintainConsistency()
        {
            var dict = new DenseDictionary<int, string>();

            for (int i = 0; i < 100; i++)
            {
                dict.Add(i, $"value_{i}");
            }

            NAssert.AreEqual(100, dict.Count);

            for (int i = 0; i < 50; i++)
            {
                dict.RemoveMustExist(i * 2);
            }

            NAssert.AreEqual(50, dict.Count);

            for (int i = 1; i < 100; i += 2)
            {
                NAssert.IsTrue(dict.ContainsKey(i), $"Dictionary should contain key {i}");
                NAssert.AreEqual($"value_{i}", dict[i]);
            }
        }

        [Test]
        public void CollisionHandling_WorksCorrectly()
        {
            var dict = new DenseDictionary<CollidingKey, string>();

            var key1 = new CollidingKey(1);
            var key2 = new CollidingKey(2);
            var key3 = new CollidingKey(3);

            dict.Add(key1, "one");
            dict.Add(key2, "two");
            dict.Add(key3, "three");

            NAssert.AreEqual(3, dict.Count);
            NAssert.AreEqual("one", dict[key1]);
            NAssert.AreEqual("two", dict[key2]);
            NAssert.AreEqual("three", dict[key3]);

            dict.RemoveMustExist(key2);

            NAssert.AreEqual(2, dict.Count);
            NAssert.AreEqual("one", dict[key1]);
            NAssert.AreEqual("three", dict[key3]);
            NAssert.IsFalse(dict.ContainsKey(key2), "Dictionary should not contain removed key");
        }

        [Test]
        public void RecycleOrAdd_WithReferenceType_CreatesOrRecycles()
        {
            var dict = new DenseDictionary<int, TestClass>();

            ref var value1 = ref dict.RecycleOrAdd<TestClass>(
                1,
                () => new TestClass { Value = "first" },
                (ref TestClass obj) => obj.Reset()
            );
            NAssert.AreEqual("first", value1.Value);

            dict.Recycle();

            ref var value2 = ref dict.RecycleOrAdd<TestClass>(
                1,
                () => new TestClass { Value = "second" },
                (ref TestClass obj) => obj.Reset()
            );
            NAssert.AreEqual("", value2.Value);
        }

        [Test]
        public void EnumKeys_WorkCorrectlyDirectly()
        {
            // Using int keys to simulate enum behavior without the IEquatable requirement
            var dict = new DenseDictionary<int, string>();

            // All operations work seamlessly
            dict[0] = "first";
            dict[1] = "second";

            NAssert.AreEqual("first", dict[0]);
            NAssert.IsTrue(dict.ContainsKey(0), "Should contain 0");

            dict.RemoveMustExist(0);
            NAssert.IsFalse(dict.ContainsKey(0), "Should not contain 0 after removal");

            // Test with different int values
            var priorityDict = new DenseDictionary<int, int>();
            priorityDict[1] = 1;
            priorityDict[2] = 5;
            priorityDict[3] = 10;

            NAssert.AreEqual(10, priorityDict[3]);
        }

        [Test]
        public void EnumKeys_WorkCorrectly()
        {
            // With implicit conversions, using EnumWrapper is much cleaner
            var dict = new DenseDictionary<EnumWrapper, string>();

            // Can pass enum values directly - they're implicitly converted to EnumWrapper
            dict.Add(TestEnum.First, "first value");
            dict.Add(TestEnum.Second, "second value");
            dict.Add(TestEnum.Third, "third value");

            NAssert.AreEqual(3, dict.Count);

            // Can use enum values directly for indexing
            NAssert.AreEqual("first value", dict[TestEnum.First]);
            NAssert.AreEqual("second value", dict[TestEnum.Second]);
            NAssert.AreEqual("third value", dict[TestEnum.Third]);

            // ContainsKey works with enum values directly
            NAssert.IsTrue(
                dict.ContainsKey(TestEnum.First),
                "Dictionary should contain TestEnum.First"
            );
            NAssert.IsTrue(
                dict.ContainsKey(TestEnum.Second),
                "Dictionary should contain TestEnum.Second"
            );
            NAssert.IsFalse(
                dict.ContainsKey(TestEnum.Fourth),
                "Dictionary should not contain TestEnum.Fourth"
            );

            // TryGetValue with direct enum
            var result = dict.TryGetValue(TestEnum.Second, out var value);
            NAssert.IsTrue(result, "TryGetValue should return true for existing enum key");
            NAssert.AreEqual("second value", value);

            // Remove with direct enum
            dict.RemoveMustExist(TestEnum.Second);
            NAssert.AreEqual(2, dict.Count);
            NAssert.IsFalse(
                dict.ContainsKey(TestEnum.Second),
                "Dictionary should not contain removed enum key"
            );

            // Update value with direct enum key
            dict[TestEnum.First] = "updated first";
            NAssert.AreEqual("updated first", dict[TestEnum.First]);

            // Iteration - keys are implicitly converted back to enum
            var pairs = new List<(TestEnum key, string value)>();
            foreach (var kvp in dict)
            {
                TestEnum enumKey = kvp.Key; // Implicit conversion back to enum
                pairs.Add((enumKey, kvp.Value));
            }

            NAssert.AreEqual(2, pairs.Count);
            NAssert.IsTrue(
                pairs.Contains((TestEnum.First, "updated first")),
                "Should contain updated First entry"
            );
            NAssert.IsTrue(
                pairs.Contains((TestEnum.Third, "third value")),
                "Should contain Third entry"
            );
        }

        [Test]
        public void DirectReferenceTypeKeys_StringKeys_WorksWithoutWrapper()
        {
            var stringDict = new DenseDictionary<string, int>();

            stringDict["hello"] = 1;
            stringDict["world"] = 2;
            stringDict["test"] = 3;

            NAssert.AreEqual(3, stringDict.Count);
            NAssert.AreEqual(1, stringDict["hello"]);
            NAssert.AreEqual(2, stringDict["world"]);
            NAssert.AreEqual(3, stringDict["test"]);
            NAssert.IsTrue(stringDict.ContainsKey("hello"), "Should contain hello key");
            NAssert.IsFalse(stringDict.ContainsKey("missing"), "Should not contain missing key");
        }

        [Test]
        public void DirectReferenceTypeKeys_WorksDirectly()
        {
            var stringDict = new DenseDictionary<string, int>();

            stringDict["test1"] = 1;
            stringDict["test2"] = 2;
            stringDict["test3"] = 3;

            NAssert.AreEqual(3, stringDict.Count);
            NAssert.AreEqual(1, stringDict["test1"]);
            NAssert.AreEqual(2, stringDict["test2"]);
            NAssert.AreEqual(3, stringDict["test3"]);
            NAssert.IsTrue(stringDict.ContainsKey("test1"), "Should contain test1 key");
            NAssert.IsFalse(stringDict.ContainsKey("missing"), "Should not contain missing key");
        }

        [Test]
        public void DirectIntKeys_WorksDirectly()
        {
            var intDict = new DenseDictionary<int, string>();

            intDict[0] = "1st";
            intDict[1] = "2nd";
            intDict[2] = "3rd";

            NAssert.AreEqual(3, intDict.Count);
            NAssert.AreEqual("1st", intDict[0]);
            NAssert.AreEqual("2nd", intDict[1]);
            NAssert.AreEqual("3rd", intDict[2]);
            NAssert.IsTrue(intDict.ContainsKey(0), "Should contain 0 key");
        }

        #endregion
    }
}

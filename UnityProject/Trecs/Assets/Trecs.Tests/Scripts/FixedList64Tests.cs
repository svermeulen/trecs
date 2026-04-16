using NUnit.Framework;
using Trecs.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class FixedList64Tests
    {
        private struct LargeTestStruct
        {
            public int A,
                B,
                C,
                D;
            public int E,
                F,
                G,
                H;
            public float X,
                Y,
                Z,
                W;
        }

        #region Equality

        [Test]
        public void TestEqualitySameContent()
        {
            var list1 = new FixedList64<int> { Count = 3 };
            list1.GetRef(0) = 5;
            list1.GetRef(1) = 10;
            list1.GetRef(2) = 15;

            var list2 = new FixedList64<int> { Count = 3 };
            list2.GetRef(0) = 5;
            list2.GetRef(1) = 10;
            list2.GetRef(2) = 15;

            NAssert.IsTrue(list1 == list2);
            NAssert.IsTrue(!(list1 != list2));
        }

        [Test]
        public void TestEqualityIgnoresGarbageInUnusedSlots()
        {
            // This test verifies the fix: only Count + active elements are compared
            var list1 = new FixedList64<int> { Count = 2 };
            list1.GetRef(0) = 5;
            list1.GetRef(1) = 10;
            list1.Buffer.GetRef(2) = 999; // garbage in unused slot - access Buffer directly

            var list2 = new FixedList64<int> { Count = 2 };
            list2.GetRef(0) = 5;
            list2.GetRef(1) = 10;
            list2.Buffer.GetRef(2) = 777; // different garbage in unused slot - access Buffer directly

            NAssert.IsTrue(
                list1 == list2,
                "Lists with same content but different garbage should be equal"
            );
        }

        [Test]
        public void TestEqualityDifferentCount()
        {
            var list1 = new FixedList64<int> { Count = 2 };
            list1.GetRef(0) = 5;
            list1.GetRef(1) = 10;

            var list2 = new FixedList64<int> { Count = 3 };
            list2.GetRef(0) = 5;
            list2.GetRef(1) = 10;
            list2.GetRef(2) = 15;

            NAssert.IsTrue(list1 != list2);
            NAssert.IsTrue(!(list1 == list2));
        }

        [Test]
        public void TestEqualityDifferentContent()
        {
            var list1 = new FixedList64<int> { Count = 3 };
            list1.GetRef(0) = 5;
            list1.GetRef(1) = 10;
            list1.GetRef(2) = 15;

            var list2 = new FixedList64<int> { Count = 3 };
            list2.GetRef(0) = 5;
            list2.GetRef(1) = 99; // different
            list2.GetRef(2) = 15;

            NAssert.IsTrue(list1 != list2);
            NAssert.IsTrue(!(list1 == list2));
        }

        [Test]
        public void TestEqualityEmptyLists()
        {
            var list1 = new FixedList64<int> { Count = 0 };
            var list2 = new FixedList64<int> { Count = 0 };

            NAssert.IsTrue(list1 == list2);
        }

        [Test]
        public void TestEqualityWithLargeStruct()
        {
            var struct1 = new LargeTestStruct
            {
                A = 1,
                B = 2,
                C = 3,
                D = 4,
            };
            var struct2 = new LargeTestStruct
            {
                A = 1,
                B = 2,
                C = 3,
                D = 4,
            };

            var list1 = new FixedList64<LargeTestStruct> { Count = 1 };
            list1.GetRef(0) = struct1;

            var list2 = new FixedList64<LargeTestStruct> { Count = 1 };
            list2.GetRef(0) = struct2;

            NAssert.IsTrue(list1 == list2);
        }

        #endregion

        #region Access / Modification

        [Test]
        public void TestGetRefModification()
        {
            var list = new FixedList64<int> { Count = 3 };
            list.GetRef(0) = 5;
            list.GetRef(1) = 10;
            list.GetRef(2) = 15;

            ref int value = ref list.GetRef(1);
            NAssert.AreEqual(10, value);

            // Modify through reference
            value = 99;
            NAssert.AreEqual(99, list.Get(1));
        }

        [Test]
        public void TestGet()
        {
            var list = new FixedList64<int> { Count = 3 };
            list.GetRef(0) = 5;
            list.GetRef(1) = 10;
            list.GetRef(2) = 15;

            ref readonly int value = ref list.Get(1);
            NAssert.AreEqual(10, value);
        }

        [Test]
        public void TestWithLargeStruct()
        {
            // Test with a larger struct to verify ref semantics work correctly
            var largeStruct = new LargeTestStruct
            {
                A = 1,
                B = 2,
                C = 3,
                D = 4,
                E = 5,
                F = 6,
                G = 7,
                H = 8,
            };

            var list = new FixedList64<LargeTestStruct> { Count = 1 };
            list.GetRef(0) = largeStruct;

            // Modify through reference
            ref var item = ref list.GetRef(0);
            item.A = 999;

            NAssert.AreEqual(999, list.Get(0).A);
        }

        #endregion

        #region Edge Cases

        [Test]
        public void TestEqualityAtMaxCapacity()
        {
            var list1 = new FixedList64<int> { Count = 64 };
            var list2 = new FixedList64<int> { Count = 64 };

            for (int i = 0; i < 64; i++)
            {
                list1.GetRef(i) = i;
                list2.GetRef(i) = i;
            }

            NAssert.IsTrue(list1 == list2);

            // Change last element
            list2.GetRef(63) = 999;
            NAssert.IsTrue(list1 != list2);
        }

        [Test]
        public void TestEqualityPartiallyFilledList()
        {
            var list1 = new FixedList64<int> { Count = 5 };
            var list2 = new FixedList64<int> { Count = 5 };

            for (int i = 0; i < 5; i++)
            {
                list1.GetRef(i) = i * 2;
                list2.GetRef(i) = i * 2;
            }

            // Fill unused slots with garbage
            for (int i = 5; i < 64; i++)
            {
                list1.Buffer.GetRef(i) = 12345;
                list2.Buffer.GetRef(i) = 67890;
            }

            NAssert.IsTrue(list1 == list2, "Should ignore garbage in unused slots");
        }

        #endregion
    }
}

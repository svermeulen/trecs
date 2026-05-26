using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class NullSerializationTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
        }

        [Test]
        public void TestNullString()
        {
            _cacheHelper.StartWrite(1, true);
            _cacheHelper.WriteString("NullString", null);
            _cacheHelper.WriteString("EmptyString", "");
            _cacheHelper.WriteString("NormalString", "test");
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            TrecsDebugAssert.That(_cacheHelper.ReadString("NullString") == null);
            TrecsDebugAssert.That(_cacheHelper.ReadString("EmptyString") == "");
            TrecsDebugAssert.That(_cacheHelper.ReadString("NormalString") == "test");
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestNullObject()
        {
            _cacheHelper.StartWrite(1, true);
            _cacheHelper.WriteObject("NullObject", null);
            _cacheHelper.WriteObject("StringObject", "test");
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            object nullObj = new object(); // Initialize to non-null
            _cacheHelper.ReadObject("NullObject", ref nullObj);
            TrecsDebugAssert.That(nullObj == null);

            object stringObj = null;
            _cacheHelper.ReadObject("StringObject", ref stringObj);
            TrecsDebugAssert.That(stringObj.Equals("test"));
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestNullReference()
        {
            TestClass nullRef = null;
            TestClass normalRef = new TestClass { Value = 42, Name = "Test" };

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("NullRef", nullRef);
            _cacheHelper.Write("NormalRef", normalRef);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            TestClass readNullRef = new TestClass(); // Initialize to non-null
            _cacheHelper.Read("NullRef", ref readNullRef);
            TrecsDebugAssert.That(readNullRef == null);

            TestClass readNormalRef = null;
            _cacheHelper.Read("NormalRef", ref readNormalRef);
            TrecsDebugAssert.That(readNormalRef != null);
            TrecsDebugAssert.That(readNormalRef.Value == 42);
            TrecsDebugAssert.That(readNormalRef.Name == "Test");
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestListWithNulls()
        {
            var list = new List<string> { "first", null, "third", null, "fifth" };

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("List", list);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            List<string> readList = null;
            _cacheHelper.Read("List", ref readList);
            TrecsDebugAssert.That(readList != null);
            TrecsDebugAssert.That(readList.Count == 5);
            TrecsDebugAssert.That(readList[0] == "first");
            TrecsDebugAssert.That(readList[1] == null);
            TrecsDebugAssert.That(readList[2] == "third");
            TrecsDebugAssert.That(readList[3] == null);
            TrecsDebugAssert.That(readList[4] == "fifth");
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestArrayWithNulls()
        {
            var array = new string[] { "first", null, "third", null, "fifth" };

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("Array", array);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            string[] readArray = null;
            _cacheHelper.Read("Array", ref readArray);
            TrecsDebugAssert.That(readArray != null);
            TrecsDebugAssert.That(readArray.Length == 5);
            TrecsDebugAssert.That(readArray[0] == "first");
            TrecsDebugAssert.That(readArray[1] == null);
            TrecsDebugAssert.That(readArray[2] == "third");
            TrecsDebugAssert.That(readArray[3] == null);
            TrecsDebugAssert.That(readArray[4] == "fifth");
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestNullCompleteCollection()
        {
            List<string> nullList = null;
            string[] nullArray = null;

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("NullList", nullList);
            _cacheHelper.Write("NullArray", nullArray);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            List<string> readList = new List<string>();
            _cacheHelper.Read("NullList", ref readList);
            TrecsDebugAssert.That(readList == null);

            string[] readArray = new string[0];
            _cacheHelper.Read("NullArray", ref readArray);
            TrecsDebugAssert.That(readArray == null);
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestDeltaSerializationWithNullsThrows()
        {
            // Test that delta serialization properly rejects nulls
            _cacheHelper.StartWrite(1, true);

            try
            {
                // These should throw
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteStringDelta("Test", null, "base")
                );
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteStringDelta("Test", "value", null)
                );

                string nullString = null;
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteDelta("Test", nullString, "base")
                );
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteDelta("Test", "value", nullString)
                );
            }
            finally
            {
                // Ensure we end the write to return to idle state
                _cacheHelper.EndWrite();
            }
        }

        [Test]
        public void TestValueTypesWithWrite()
        {
            // Value types should not be affected by null handling
            int intValue = 42;
            float floatValue = 3.14f;
            bool boolValue = true;

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("Int", intValue);
            _cacheHelper.Write("Float", floatValue);
            _cacheHelper.Write("Bool", boolValue);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            int readInt = 0;
            _cacheHelper.Read("Int", ref readInt);
            TrecsDebugAssert.That(readInt == 42);

            float readFloat = 0f;
            _cacheHelper.Read("Float", ref readFloat);
            TrecsDebugAssert.That(Math.Abs(readFloat - 3.14f) < 0.001f);

            bool readBool = false;
            _cacheHelper.Read("Bool", ref readBool);
            TrecsDebugAssert.That(readBool);
            _cacheHelper.StopRead(true);
        }

        public class TestClass
        {
            public int Value { get; set; }
            public string Name { get; set; }
        }
    }
}

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
            TrecsAssert.That(_cacheHelper.ReadString("NullString") == null);
            TrecsAssert.That(_cacheHelper.ReadString("EmptyString") == "");
            TrecsAssert.That(_cacheHelper.ReadString("NormalString") == "test");
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
            TrecsAssert.That(nullObj == null);

            object stringObj = null;
            _cacheHelper.ReadObject("StringObject", ref stringObj);
            TrecsAssert.That(stringObj.Equals("test"));
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
            TrecsAssert.That(readNullRef == null);

            TestClass readNormalRef = null;
            _cacheHelper.Read("NormalRef", ref readNormalRef);
            TrecsAssert.That(readNormalRef != null);
            TrecsAssert.That(readNormalRef.Value == 42);
            TrecsAssert.That(readNormalRef.Name == "Test");
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
            TrecsAssert.That(readList != null);
            TrecsAssert.That(readList.Count == 5);
            TrecsAssert.That(readList[0] == "first");
            TrecsAssert.That(readList[1] == null);
            TrecsAssert.That(readList[2] == "third");
            TrecsAssert.That(readList[3] == null);
            TrecsAssert.That(readList[4] == "fifth");
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
            TrecsAssert.That(readArray != null);
            TrecsAssert.That(readArray.Length == 5);
            TrecsAssert.That(readArray[0] == "first");
            TrecsAssert.That(readArray[1] == null);
            TrecsAssert.That(readArray[2] == "third");
            TrecsAssert.That(readArray[3] == null);
            TrecsAssert.That(readArray[4] == "fifth");
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestDictionaryWithNulls()
        {
            var dict = new Dictionary<string, string>
            {
                { "key1", "value1" },
                { "key2", null },
                { "key3", "value3" },
                { "key4", null },
            };

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("Dict", dict);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            Dictionary<string, string> readDict = null;
            _cacheHelper.Read("Dict", ref readDict);
            TrecsAssert.That(readDict != null);
            TrecsAssert.That(readDict.Count == 4);
            TrecsAssert.That(readDict["key1"] == "value1");
            TrecsAssert.That(readDict["key2"] == null);
            TrecsAssert.That(readDict["key3"] == "value3");
            TrecsAssert.That(readDict["key4"] == null);
            _cacheHelper.StopRead(true);
        }

        [Test]
        public void TestNullCompleteCollection()
        {
            List<string> nullList = null;
            Dictionary<string, string> nullDict = null;
            string[] nullArray = null;

            _cacheHelper.StartWrite(1, true);
            _cacheHelper.Write("NullList", nullList);
            _cacheHelper.Write("NullDict", nullDict);
            _cacheHelper.Write("NullArray", nullArray);
            _cacheHelper.EndWrite();

            _cacheHelper.ResetMemoryPosition();

            _cacheHelper.StartRead();
            List<string> readList = new List<string>();
            _cacheHelper.Read("NullList", ref readList);
            TrecsAssert.That(readList == null);

            Dictionary<string, string> readDict = new Dictionary<string, string>();
            _cacheHelper.Read("NullDict", ref readDict);
            TrecsAssert.That(readDict == null);

            string[] readArray = new string[0];
            _cacheHelper.Read("NullArray", ref readArray);
            TrecsAssert.That(readArray == null);
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
                TrecsAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteStringDelta("Test", null, "base")
                );
                TrecsAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteStringDelta("Test", "value", null)
                );

                string nullString = null;
                TrecsAssert.Throws<TrecsException>(() =>
                    _cacheHelper.WriteDelta("Test", nullString, "base")
                );
                TrecsAssert.Throws<TrecsException>(() =>
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
            TrecsAssert.That(readInt == 42);

            float readFloat = 0f;
            _cacheHelper.Read("Float", ref readFloat);
            TrecsAssert.That(Math.Abs(readFloat - 3.14f) < 0.001f);

            bool readBool = false;
            _cacheHelper.Read("Bool", ref readBool);
            TrecsAssert.That(readBool);
            _cacheHelper.StopRead(true);
        }

        public class TestClass
        {
            public int Value { get; set; }
            public string Name { get; set; }
        }
    }
}

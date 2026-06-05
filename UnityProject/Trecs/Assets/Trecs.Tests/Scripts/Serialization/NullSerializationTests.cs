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
        SerializationHelper _helper;
        SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void TestNullString()
        {
            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.WriteString("NullString", null);
            _helper.Writer.WriteString("EmptyString", "");
            _helper.Writer.WriteString("NormalString", "test");
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            TrecsDebugAssert.That(_helper.Reader.ReadString("NullString") == null);
            TrecsDebugAssert.That(_helper.Reader.ReadString("EmptyString") == "");
            TrecsDebugAssert.That(_helper.Reader.ReadString("NormalString") == "test");
            _helper.Reader.Complete();
        }

        [Test]
        public void TestNullObject()
        {
            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.WriteObject("NullObject", null);
            _helper.Writer.WriteObject("StringObject", "test");
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            object nullObj = new object(); // Initialize to non-null
            _helper.Reader.ReadObject("NullObject", ref nullObj);
            TrecsDebugAssert.That(nullObj == null);

            object stringObj = null;
            _helper.Reader.ReadObject("StringObject", ref stringObj);
            TrecsDebugAssert.That(stringObj.Equals("test"));
            _helper.Reader.Complete();
        }

        [Test]
        public void TestNullReference()
        {
            TestClass nullRef = null;
            TestClass normalRef = new TestClass { Value = 42, Name = "Test" };

            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.Write("NullRef", nullRef);
            _helper.Writer.Write("NormalRef", normalRef);
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            TestClass readNullRef = new TestClass(); // Initialize to non-null
            _helper.Reader.Read("NullRef", ref readNullRef);
            TrecsDebugAssert.That(readNullRef == null);

            TestClass readNormalRef = null;
            _helper.Reader.Read("NormalRef", ref readNormalRef);
            TrecsDebugAssert.That(readNormalRef != null);
            TrecsDebugAssert.That(readNormalRef.Value == 42);
            TrecsDebugAssert.That(readNormalRef.Name == "Test");
            _helper.Reader.Complete();
        }

        [Test]
        public void TestListWithNulls()
        {
            var list = new List<string> { "first", null, "third", null, "fifth" };

            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.Write("List", list);
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            List<string> readList = null;
            _helper.Reader.Read("List", ref readList);
            TrecsDebugAssert.That(readList != null);
            TrecsDebugAssert.That(readList.Count == 5);
            TrecsDebugAssert.That(readList[0] == "first");
            TrecsDebugAssert.That(readList[1] == null);
            TrecsDebugAssert.That(readList[2] == "third");
            TrecsDebugAssert.That(readList[3] == null);
            TrecsDebugAssert.That(readList[4] == "fifth");
            _helper.Reader.Complete();
        }

        [Test]
        public void TestArrayWithNulls()
        {
            var array = new string[] { "first", null, "third", null, "fifth" };

            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.Write("Array", array);
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            string[] readArray = null;
            _helper.Reader.Read("Array", ref readArray);
            TrecsDebugAssert.That(readArray != null);
            TrecsDebugAssert.That(readArray.Length == 5);
            TrecsDebugAssert.That(readArray[0] == "first");
            TrecsDebugAssert.That(readArray[1] == null);
            TrecsDebugAssert.That(readArray[2] == "third");
            TrecsDebugAssert.That(readArray[3] == null);
            TrecsDebugAssert.That(readArray[4] == "fifth");
            _helper.Reader.Complete();
        }

        [Test]
        public void TestNullCompleteCollection()
        {
            List<string> nullList = null;
            string[] nullArray = null;

            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.Write("NullList", nullList);
            _helper.Writer.Write("NullArray", nullArray);
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            List<string> readList = new List<string>();
            _helper.Reader.Read("NullList", ref readList);
            TrecsDebugAssert.That(readList == null);

            string[] readArray = new string[0];
            _helper.Reader.Read("NullArray", ref readArray);
            TrecsDebugAssert.That(readArray == null);
            _helper.Reader.Complete();
        }

        [Test]
        public void TestDeltaSerializationWithNullsThrows()
        {
            // Test that delta serialization properly rejects nulls
            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);

            try
            {
                // These should throw
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _helper.Writer.WriteStringDelta("Test", null, "base")
                );
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _helper.Writer.WriteStringDelta("Test", "value", null)
                );

                string nullString = null;
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _helper.Writer.WriteDelta("Test", nullString, "base")
                );
                TrecsDebugAssert.Throws<TrecsException>(() =>
                    _helper.Writer.WriteDelta("Test", "value", nullString)
                );
            }
            finally
            {
                // Ensure we end the write to return to idle state
                _helper.Writer.Complete();
            }
        }

        [Test]
        public void TestValueTypesWithWrite()
        {
            // Value types should not be affected by null handling
            int intValue = 42;
            float floatValue = 3.14f;
            bool boolValue = true;

            _helper.Writer.Start(_data, version: 1, includeTypeChecks: true);
            _helper.Writer.Write("Int", intValue);
            _helper.Writer.Write("Float", floatValue);
            _helper.Writer.Write("Bool", boolValue);
            _helper.Writer.Complete();

            _helper.Reader.Start(_data);
            int readInt = 0;
            _helper.Reader.Read("Int", ref readInt);
            TrecsDebugAssert.That(readInt == 42);

            float readFloat = 0f;
            _helper.Reader.Read("Float", ref readFloat);
            TrecsDebugAssert.That(Math.Abs(readFloat - 3.14f) < 0.001f);

            bool readBool = false;
            _helper.Reader.Read("Bool", ref readBool);
            TrecsDebugAssert.That(readBool);
            _helper.Reader.Complete();
        }

        public class TestClass
        {
            public int Value { get; set; }
            public string Name { get; set; }
        }
    }
}

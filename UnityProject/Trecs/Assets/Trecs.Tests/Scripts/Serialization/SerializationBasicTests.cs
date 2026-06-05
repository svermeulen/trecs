using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;

namespace Trecs.Tests
{
    [TestFixture]
    public class SerializationBasicTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;
        private SerializationReadBuffer _readBuffer;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
            _readBuffer = new SerializationReadBuffer();
        }

        [Test]
        public void Vector3_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            var originalVector = new Vector3(1.5f, -2.3f, 0.7f);
            var flags = 0L;

            // Act - Serialize and Deserialize using cache helper
            _helper.WriteAll(
                _data,
                originalVector,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedVector = _helper.ReadAll<Vector3>(_data);

            // Assert
            TrecsDebugAssert.That(
                TestUtil.Approximately(originalVector.x, deserializedVector.x, 0.0001f),
                "X component should match"
            );
            TrecsDebugAssert.That(
                TestUtil.Approximately(originalVector.y, deserializedVector.y, 0.0001f),
                "Y component should match"
            );
            TrecsDebugAssert.That(
                TestUtil.Approximately(originalVector.z, deserializedVector.z, 0.0001f),
                "Z component should match"
            );
        }

        [Test]
        public void Int_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            int originalValue = 42;
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedValue = _helper.ReadAll<int>(_data);

            // Assert
            TrecsDebugAssert.That(deserializedValue == originalValue);
        }

        [Test]
        public void String_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            string originalValue = "Hello World";
            var flags = 0L;

            // Act
            _helper.WriteAllObject(
                _data,
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedValue = (string)_helper.ReadAllObject(_data);

            // Assert
            TrecsDebugAssert.That(deserializedValue == originalValue);
        }

        [Test]
        public void EmptyString_SerializeAndDeserialize_ProducesEmptyString()
        {
            // Arrange
            string originalValue = "";
            var flags = 0L;

            // Act
            _helper.WriteAllObject(
                _data,
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedValue = (string)_helper.ReadAllObject(_data);

            // Assert
            TrecsDebugAssert.That(deserializedValue == originalValue);
        }

        [Test]
        public void MultiByteUtf8String_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            string originalValue = "hello éè 世界 🚀";
            var flags = 0L;

            _helper.WriteAllObject(
                _data,
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedValue = (string)_helper.ReadAllObject(_data);

            TrecsDebugAssert.That(deserializedValue == originalValue);
        }

        [Test]
        public void Float_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            float originalValue = 3.14159f;
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var deserializedValue = _helper.ReadAll<float>(_data);

            // Assert
            TrecsDebugAssert.That(
                TestUtil.Approximately(originalValue, deserializedValue, 0.0001f)
            );
        }

        [Test]
        public void Bool_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            var flags = 0L;

            // Test true
            _helper.WriteAll(_data, true, TestConstants.Version, includeTypeChecks: true, flags);
            var deserializedTrue = _helper.ReadAll<bool>(_data);
            TrecsDebugAssert.That(deserializedTrue);

            // Test false
            _helper.WriteAll(_data, false, TestConstants.Version, includeTypeChecks: true, flags);
            var deserializedFalse = _helper.ReadAll<bool>(_data);
            TrecsDebugAssert.That(!deserializedFalse);
        }

        [Test]
        public void MultipleValues_SerializeAndDeserialize_InSequence()
        {
            // Arrange
            var flags = 0L;
            var vec = new Vector3(1, 2, 3);
            var intVal = 100;
            var strVal = "test";

            // Act - Write multiple values
            _helper.Writer.Start(
                _data,
                version: TestConstants.Version,
                includeTypeChecks: true,
                flags: flags
            );
            _helper.Writer.Write("Vec", vec);
            _helper.Writer.Write("Int", intVal);
            _helper.Writer.WriteString("Str", strVal);
            _helper.Writer.Complete();

            // Reset position for reading

            // Read back
            _helper.Reader.Start(_data);
            var readVec = _helper.Reader.Read<Vector3>("Vec");
            var readInt = _helper.Reader.Read<int>("Int");
            var readStr = _helper.Reader.ReadString("Str");
            _helper.Reader.Complete();

            // Assert
            TrecsDebugAssert.That(TestUtil.Approximately(vec.x, readVec.x, 0.0001f));
            TrecsDebugAssert.That(TestUtil.Approximately(vec.y, readVec.y, 0.0001f));
            TrecsDebugAssert.That(TestUtil.Approximately(vec.z, readVec.z, 0.0001f));
            TrecsDebugAssert.That(readInt == intVal);
            TrecsDebugAssert.That(readStr == strVal);
        }

        [Test]
        public void MemoryStream_CanBeSavedAndLoaded()
        {
            // Arrange
            var flags = 0L;
            var originalValue = new Vector3(5, 10, 15);
            var tempPath = Path.GetTempFileName();

            try
            {
                // Write to memory
                _helper.WriteAll(
                    _data,
                    originalValue,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );

                // Save to file
                using (var fileStream = File.Create(tempPath))
                {
                    _data.WriteContiguousTo(fileStream);
                }

                // Load from file and read back
                Vector3 loadedValue;
                using (var fileStream = File.OpenRead(tempPath))
                {
                    loadedValue = _helper.ReadAll<Vector3>(_readBuffer.Load(fileStream));
                }

                // Assert
                TrecsDebugAssert.That(
                    TestUtil.Approximately(originalValue.x, loadedValue.x, 0.0001f)
                );
                TrecsDebugAssert.That(
                    TestUtil.Approximately(originalValue.y, loadedValue.y, 0.0001f)
                );
                TrecsDebugAssert.That(
                    TestUtil.Approximately(originalValue.z, loadedValue.z, 0.0001f)
                );
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }
}

using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization.Internal;
using UnityEngine;
using Assert = Trecs.Internal.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class SerializationBasicTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = SerializationFactory.CreateRegistry();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
        }

        [Test]
        public void Vector3_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            var originalVector = new Vector3(1.5f, -2.3f, 0.7f);
            var flags = 0L;

            // Act - Serialize and Deserialize using cache helper
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalVector,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedVector = _cacheHelper.ReadAll<Vector3>();

            // Assert
            Assert.That(
                MathUtil.Approximately(originalVector.x, deserializedVector.x, 0.0001f),
                "X component should match"
            );
            Assert.That(
                MathUtil.Approximately(originalVector.y, deserializedVector.y, 0.0001f),
                "Y component should match"
            );
            Assert.That(
                MathUtil.Approximately(originalVector.z, deserializedVector.z, 0.0001f),
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedValue = _cacheHelper.ReadAll<int>();

            // Assert
            Assert.That(deserializedValue == originalValue);
        }

        [Test]
        public void String_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            string originalValue = "Hello World";
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedValue = (string)_cacheHelper.ReadAllObject();

            // Assert
            Assert.That(deserializedValue == originalValue);
        }

        [Test]
        public void EmptyString_SerializeAndDeserialize_ProducesEmptyString()
        {
            // Arrange
            string originalValue = "";
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedValue = (string)_cacheHelper.ReadAllObject();

            // Assert
            Assert.That(deserializedValue == originalValue);
        }

        [Test]
        public void Float_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            float originalValue = 3.14159f;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var deserializedValue = _cacheHelper.ReadAll<float>();

            // Assert
            Assert.That(MathUtil.Approximately(originalValue, deserializedValue, 0.0001f));
        }

        [Test]
        public void Bool_SerializeAndDeserialize_ProducesIdenticalValue()
        {
            // Arrange
            var flags = 0L;

            // Test true
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(true, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedTrue = _cacheHelper.ReadAll<bool>();
            Assert.That(deserializedTrue);

            // Test false
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(false, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedFalse = _cacheHelper.ReadAll<bool>();
            Assert.That(!deserializedFalse);
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
            _cacheHelper.StartWrite(TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.Write("vec", vec);
            _cacheHelper.Write("int", intVal);
            _cacheHelper.WriteString("str", strVal);
            _cacheHelper.EndWrite();

            // Reset position for reading
            _cacheHelper.ResetMemoryPosition();

            // Read back
            _cacheHelper.StartRead();
            var readVec = _cacheHelper.Read<Vector3>("vec");
            var readInt = _cacheHelper.Read<int>("int");
            var readStr = _cacheHelper.ReadString("str");
            _cacheHelper.StopRead(verifySentinel: true);

            // Assert
            Assert.That(MathUtil.Approximately(vec.x, readVec.x, 0.0001f));
            Assert.That(MathUtil.Approximately(vec.y, readVec.y, 0.0001f));
            Assert.That(MathUtil.Approximately(vec.z, readVec.z, 0.0001f));
            Assert.That(readInt == intVal);
            Assert.That(readStr == strVal);
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
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    originalValue,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );

                // Save to file
                _cacheHelper.ResetMemoryPosition();
                _cacheHelper.SaveMemoryToFile(tempPath);

                // Clear and load from file
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.LoadMemoryFromFile(tempPath);
                _cacheHelper.ResetMemoryPosition();

                // Read back
                var loadedValue = _cacheHelper.ReadAll<Vector3>();

                // Assert
                Assert.That(MathUtil.Approximately(originalValue.x, loadedValue.x, 0.0001f));
                Assert.That(MathUtil.Approximately(originalValue.y, loadedValue.y, 0.0001f));
                Assert.That(MathUtil.Approximately(originalValue.z, loadedValue.z, 0.0001f));
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

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
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
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
            TrecsAssert.That(
                TestUtil.Approximately(originalVector.x, deserializedVector.x, 0.0001f),
                "X component should match"
            );
            TrecsAssert.That(
                TestUtil.Approximately(originalVector.y, deserializedVector.y, 0.0001f),
                "Y component should match"
            );
            TrecsAssert.That(
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
            TrecsAssert.That(deserializedValue == originalValue);
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
            TrecsAssert.That(deserializedValue == originalValue);
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
            TrecsAssert.That(deserializedValue == originalValue);
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
            TrecsAssert.That(TestUtil.Approximately(originalValue, deserializedValue, 0.0001f));
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
            TrecsAssert.That(deserializedTrue);

            // Test false
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(false, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var deserializedFalse = _cacheHelper.ReadAll<bool>();
            TrecsAssert.That(!deserializedFalse);
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
            _cacheHelper.Write("Vec", vec);
            _cacheHelper.Write("Int", intVal);
            _cacheHelper.WriteString("Str", strVal);
            _cacheHelper.EndWrite();

            // Reset position for reading
            _cacheHelper.ResetMemoryPosition();

            // Read back
            _cacheHelper.StartRead();
            var readVec = _cacheHelper.Read<Vector3>("Vec");
            var readInt = _cacheHelper.Read<int>("Int");
            var readStr = _cacheHelper.ReadString("Str");
            _cacheHelper.StopRead(verifySentinel: true);

            // Assert
            TrecsAssert.That(TestUtil.Approximately(vec.x, readVec.x, 0.0001f));
            TrecsAssert.That(TestUtil.Approximately(vec.y, readVec.y, 0.0001f));
            TrecsAssert.That(TestUtil.Approximately(vec.z, readVec.z, 0.0001f));
            TrecsAssert.That(readInt == intVal);
            TrecsAssert.That(readStr == strVal);
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
                TrecsAssert.That(TestUtil.Approximately(originalValue.x, loadedValue.x, 0.0001f));
                TrecsAssert.That(TestUtil.Approximately(originalValue.y, loadedValue.y, 0.0001f));
                TrecsAssert.That(TestUtil.Approximately(originalValue.z, loadedValue.z, 0.0001f));
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

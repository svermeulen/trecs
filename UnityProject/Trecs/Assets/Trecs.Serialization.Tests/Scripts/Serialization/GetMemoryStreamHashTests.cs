using System;
using NUnit.Framework;
using Assert = Trecs.Internal.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class GetMemoryStreamHashTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            // Register TestData and byte[] serializers
            _serializerRegistry.RegisterSerializer<TestDataSerializer>();
            _serializerRegistry.RegisterSerializerDelta<TestDataSerializer>();
            _serializerRegistry.RegisterSerializer<ArraySerializer<byte[], byte>>();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
        }

        [Test]
        public void GetMemoryStreamHash_WithSameData_ReturnsSameHash()
        {
            // Arrange
            var data = new TestData { Value = 42, Name = "Test" };

            // Act
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true);
            _cacheHelper.ResetMemoryPosition();
            int hash1 = _cacheHelper.GetMemoryStreamHash();

            // Create a new cache helper with same data
            using var cacheHelper2 = new SerializationBuffer(_serializerRegistry);
            cacheHelper2.WriteAll(data, TestConstants.Version, includeTypeChecks: true);
            cacheHelper2.ResetMemoryPosition();
            int hash2 = cacheHelper2.GetMemoryStreamHash();

            // Assert
            Assert.That(hash1 == hash2, "Same data should produce the same hash");
        }

        [Test]
        public void GetMemoryStreamHash_WithDifferentData_ReturnsDifferentHash()
        {
            // Arrange
            var data1 = new TestData { Value = 42, Name = "Test1" };
            var data2 = new TestData { Value = 100, Name = "Test2" };

            // Act
            _cacheHelper.WriteAll(data1, TestConstants.Version, includeTypeChecks: true);
            _cacheHelper.ResetMemoryPosition();
            int hash1 = _cacheHelper.GetMemoryStreamHash();

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data2, TestConstants.Version, includeTypeChecks: true);
            _cacheHelper.ResetMemoryPosition();
            int hash2 = _cacheHelper.GetMemoryStreamHash();

            // Assert
            Assert.That(hash1 != hash2, "Different data should produce different hashes");
        }

        [Test]
        public void GetMemoryStreamHash_DuringWriteState_ThrowsAssertion()
        {
            // Arrange
            _cacheHelper.StartWrite(TestConstants.Version, includeTypeChecks: true);

            // Act & Assert
            Assert.Throws<TrecsException>(() => _cacheHelper.GetMemoryStreamHash());

            // Cleanup
            _cacheHelper.Write("test", 42);
            _cacheHelper.EndWrite();
        }

        [Test]
        public void GetMemoryStreamHash_DuringReadState_ThrowsAssertion()
        {
            // Arrange
            _cacheHelper.WriteAll(42, TestConstants.Version, includeTypeChecks: true);
            _cacheHelper.ResetMemoryPosition();
            _cacheHelper.StartRead();

            // Act & Assert
            Assert.Throws<TrecsException>(() => _cacheHelper.GetMemoryStreamHash());

            // Cleanup
            int value = _cacheHelper.Read<int>("value");
            _cacheHelper.StopRead(verifySentinel: true);
        }

        [Test]
        public void GetMemoryStreamHash_AfterDispose_ThrowsAssertion()
        {
            // Arrange
            _cacheHelper.WriteAll(42, TestConstants.Version, includeTypeChecks: true);
            _cacheHelper.ResetMemoryPosition();
            _cacheHelper.Dispose();

            // Act & Assert
            Assert.Throws<TrecsException>(() => _cacheHelper.GetMemoryStreamHash());

            // Prevent double dispose in TearDown
            _cacheHelper = null;
        }

        [Test]
        public void GetMemoryStreamHash_WithDeltaSerialization_ProducesHash()
        {
            // Arrange
            var baseData = new TestData { Value = 42, Name = "Base" };
            var newData = new TestData { Value = 42, Name = "Modified" };

            // Act
            _cacheHelper.WriteAllDelta(
                newData,
                baseData,
                TestConstants.Version,
                includeTypeChecks: true
            );
            _cacheHelper.ResetMemoryPosition();
            int hash = _cacheHelper.GetMemoryStreamHash();

            // Assert
            Assert.That(hash != 0, "Delta serialization should produce non-zero hash");
            Assert.That(hash != -1, "Delta serialization should produce valid hash");
        }

        private class TestData : IEquatable<TestData>
        {
            public int Value { get; set; }
            public string Name { get; set; }

            public bool Equals(TestData other)
            {
                if (other == null)
                    return false;
                return Value == other.Value && Name == other.Name;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as TestData);
            }

            public override int GetHashCode()
            {
                return (Value, Name).GetHashCode();
            }
        }

        private class TestDataSerializer : ISerializer<TestData>, ISerializerDelta<TestData>
        {
            public TestDataSerializer() { }

            public void Serialize(in TestData value, ISerializationWriter writer)
            {
                writer.Write("Value", value.Value);
                writer.WriteString("Name", value.Name);
            }

            public void Deserialize(ref TestData value, ISerializationReader reader)
            {
                if (value == null)
                    value = new TestData();

                int tempValue = 0;
                reader.Read("Value", ref tempValue);
                value.Value = tempValue;
                value.Name = reader.ReadString("Name");
            }

            public void SerializeDelta(
                in TestData value,
                in TestData baseValue,
                ISerializationWriter writer
            )
            {
                if (!value.Value.Equals(baseValue.Value))
                {
                    writer.WriteBit(true);
                    writer.Write("Value", value.Value);
                }
                else
                {
                    writer.WriteBit(false);
                }

                if (value.Name != baseValue.Name)
                {
                    writer.WriteBit(true);
                    writer.WriteString("Name", value.Name);
                }
                else
                {
                    writer.WriteBit(false);
                }
            }

            public void DeserializeDelta(
                ref TestData value,
                in TestData baseValue,
                ISerializationReader reader
            )
            {
                if (value == null)
                    value = new TestData();

                if (reader.ReadBit())
                {
                    int tempValue = 0;
                    reader.Read("Value", ref tempValue);
                    value.Value = tempValue;
                }
                else
                {
                    value.Value = baseValue.Value;
                }

                if (reader.ReadBit())
                {
                    value.Name = reader.ReadString("Name");
                }
                else
                {
                    value.Name = baseValue.Name;
                }
            }
        }
    }
}

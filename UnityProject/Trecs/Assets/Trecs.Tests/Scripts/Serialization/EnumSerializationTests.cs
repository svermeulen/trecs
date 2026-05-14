using System;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class EnumSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            // Register enum types (includeDelta for delta serialization tests)
            RegisterEnumWithDelta<TestByteEnum>(_serializerRegistry);
            RegisterEnumWithDelta<TestIntEnum>(_serializerRegistry);
            RegisterEnumWithDelta<TestLongEnum>(_serializerRegistry);
            RegisterEnumWithDelta<TestFlagsEnum>(_serializerRegistry);
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
        }

        static void RegisterEnumWithDelta<T>(SerializerRegistry registry)
            where T : unmanaged
        {
            var serializer = new EnumSerializer<T>();
            registry.RegisterSerializer(serializer);
            registry.RegisterSerializerDelta(serializer);
        }

        [Test]
        public void ByteEnum_SerializesAndDeserializes()
        {
            // Arrange
            var original = TestByteEnum.SecondValue;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestByteEnum>();

            // Assert
            Assert.That(result == original);
        }

        [Test]
        public void IntEnum_AllValues_SerializeCorrectly()
        {
            var flags = 0L;
            var testValues = new[]
            {
                TestIntEnum.FirstValue,
                TestIntEnum.SecondValue,
                TestIntEnum.ThirdValue,
                TestIntEnum.NegativeValue,
            };

            foreach (var original in testValues)
            {
                // Act
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<TestIntEnum>();

                // Assert
                Assert.That(
                    result == original,
                    $"Enum value {original} should serialize correctly"
                );
            }
        }

        [Test]
        public void LongEnum_SerializesAndDeserializes()
        {
            // Arrange
            var original = TestLongEnum.LargeValue;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestLongEnum>();

            // Assert
            Assert.That(result == original);
        }

        [Test]
        public void FlagsEnum_CombinedValues_SerializeCorrectly()
        {
            // Arrange
            var original = TestFlagsEnum.Flag1 | TestFlagsEnum.Flag3;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestFlagsEnum>();

            // Assert
            Assert.That(result == original);
            Assert.That(result.HasFlag(TestFlagsEnum.Flag1));
            Assert.That(result.HasFlag(TestFlagsEnum.Flag3));
            Assert.That(!result.HasFlag(TestFlagsEnum.Flag2));
        }

        [Test]
        public void FlagsEnum_AllFlags_SerializeCorrectly()
        {
            // Arrange
            var original = TestFlagsEnum.Flag1 | TestFlagsEnum.Flag2 | TestFlagsEnum.Flag3;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestFlagsEnum>();

            // Assert
            Assert.That(result == original);
            Assert.That(result.HasFlag(TestFlagsEnum.Flag1));
            Assert.That(result.HasFlag(TestFlagsEnum.Flag2));
            Assert.That(result.HasFlag(TestFlagsEnum.Flag3));
        }

        [Test]
        public void FlagsEnum_NoFlags_SerializeCorrectly()
        {
            // Arrange
            var original = TestFlagsEnum.None;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestFlagsEnum>();

            // Assert
            Assert.That(result == original);
            Assert.That(result == TestFlagsEnum.None);
        }

        [Test]
        public void IntEnum_ZeroValue_SerializesCorrectly()
        {
            // Arrange
            var original = TestIntEnum.FirstValue; // Should be 0
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<TestIntEnum>();

            // Assert
            Assert.That(result == original);
            Assert.That((int)result == 0);
        }

        [Test]
        public void ByteEnum_AllValues_SerializeCorrectly()
        {
            var flags = 0L;
            var testValues = Enum.GetValues(typeof(TestByteEnum));

            foreach (TestByteEnum original in testValues)
            {
                // Act
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    original,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<TestByteEnum>();

                // Assert
                Assert.That(
                    result == original,
                    $"Byte enum value {original} should serialize correctly"
                );
            }
        }

        [Test]
        public void IntEnum_DeltaSerialization_RoundTrips()
        {
            var flags = 0L;
            var baseValue = TestIntEnum.FirstValue;
            var value = TestIntEnum.ThirdValue;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta(baseValue);

            Assert.That(result == value);
        }

        [Test]
        public void IntEnum_DeltaSerialization_Unchanged()
        {
            var flags = 0L;
            var baseValue = TestIntEnum.SecondValue;
            var value = TestIntEnum.SecondValue;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta(baseValue);

            Assert.That(result == value);
        }

        [Test]
        public void ByteEnum_DeltaSerialization_RoundTrips()
        {
            var flags = 0L;
            var baseValue = TestByteEnum.FirstValue;
            var value = TestByteEnum.ThirdValue;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta(baseValue);

            Assert.That(result == value);
        }

        [Test]
        public void LongEnum_DeltaSerialization_RoundTrips()
        {
            var flags = 0L;
            var baseValue = TestLongEnum.SmallValue;
            var value = TestLongEnum.LargeValue;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta(baseValue);

            Assert.That(result == value);
        }

        [Test]
        public void FlagsEnum_DeltaSerialization_RoundTrips()
        {
            var flags = 0L;
            var baseValue = TestFlagsEnum.Flag1;
            var value = TestFlagsEnum.Flag2 | TestFlagsEnum.Flag3;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAllDelta(baseValue);

            Assert.That(result == value);
        }

        [Test]
        public void IntEnum_DeltaSerialization_UsesCompactEncoding()
        {
            var flags = 0L;
            var baseValue = TestIntEnum.FirstValue;
            var value = TestIntEnum.ThirdValue;

            // Delta should use byte index (1 bit + 1 byte) instead of full int (1 bit + 4 bytes)
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllDelta(
                value,
                baseValue,
                TestConstants.Version,
                includeTypeChecks: false,
                flags
            );
            var deltaSize = _cacheHelper.MemoryStream.Position;

            // With compact encoding, the data portion should be small
            // Type check header + version + 1 bit change flag + 1 byte index
            Assert.That(
                deltaSize < 32,
                $"Delta size ({deltaSize}) should be compact due to byte index encoding"
            );
        }

        // Test enum definitions
        public enum TestByteEnum : byte
        {
            FirstValue = 0,
            SecondValue = 1,
            ThirdValue = 255,
        }

        public enum TestIntEnum : int
        {
            FirstValue = 0,
            SecondValue = 42,
            ThirdValue = 1000000,
            NegativeValue = -1,
        }

        public enum TestLongEnum : long
        {
            SmallValue = 1,
            LargeValue = 9223372036854775807L, // long.MaxValue
        }

        [Flags]
        public enum TestFlagsEnum : int
        {
            None = 0,
            Flag1 = 1,
            Flag2 = 2,
            Flag3 = 4,
        }
    }
}

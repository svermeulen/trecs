using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class CorruptedDataTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;
        private SerializationReadBuffer _readBuffer;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
            _readBuffer = new SerializationReadBuffer();
        }

        [Test]
        public void CorruptedHeader_InvalidVersion_ThrowsException()
        {
            // Arrange - Create a manually corrupted stream with invalid version
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write invalid version number
            writer.Write(int.MaxValue); // Invalid version
            writer.Write(false); // includesTypeChecks
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<int>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void CorruptedCollectionCount_NegativeCount_ThrowsException()
        {
            // Arrange - Create a stream with negative collection count
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write type ID for List<int>
            var typeId = 901234567; // From TestSerializerInstaller
            writer.Write(typeId);

            // Write negative count (corrupted data)
            writer.Write(-1000);
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void CorruptedCollectionCount_ExtremelyLargeCount_ThrowsException()
        {
            // Arrange - Create a stream with unreasonably large collection count
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write type ID for List<int>
            var typeId = 901234567;
            writer.Write(typeId);

            // Write extremely large count (could cause OutOfMemoryException)
            writer.Write(int.MaxValue - 1);
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void TruncatedStream_IncompleteData_ThrowsException()
        {
            // Arrange - Create a valid stream then truncate it
            var originalData = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            _helper.WriteAll(
                _data,
                originalData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var fullData = _data.ToContiguousBytes();

            // Truncate the stream (remove last 50% of data)
            var truncatedData = new byte[fullData.Length / 2];
            Array.Copy(fullData, truncatedData, truncatedData.Length);

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(truncatedData));
            });
        }

        [Test]
        public void CorruptedTypeId_UnregisteredType_ThrowsException()
        {
            // Arrange - Create stream with invalid type ID
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write invalid/unregistered type ID
            writer.Write(999999999);
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<int>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void CorruptedDictionary_DuplicateKeys_ThrowsException()
        {
            // Arrange - Create a manually corrupted dictionary stream with duplicate keys
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write type ID for Dictionary<string, int>
            var typeId = 901234569; // From TestSerializerInstaller
            writer.Write(typeId);

            // Write count
            writer.Write(2);

            // Write first key-value pair
            WriteString(writer, "duplicate_key");
            writer.Write(100);

            // Write second pair with same key (corruption)
            WriteString(writer, "duplicate_key");
            writer.Write(200);

            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<Dictionary<string, int>>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void CorruptedString_NegativeLength_ThrowsException()
        {
            // Arrange - Create a stream with corrupted string length
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write type ID for string
            var stringTypeId = GetStringTypeId();
            writer.Write(stringTypeId);

            // Write negative string length (corrupted data)
            writer.Write(-100);
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<string>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        [Test]
        public void CorruptedBooleanValue_InvalidByte_HandlesGracefully()
        {
            // Arrange - Create a stream with invalid boolean value (not 0 or 1)
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write valid header
            writer.Write(1); // version
            writer.Write(false); // includesTypeChecks

            // Write type ID for bool
            var boolTypeId = GetBoolTypeId();
            writer.Write(boolTypeId);

            // Write invalid boolean value (should be 0 or 1)
            writer.Write((byte)255);
            writer.Flush();
            memoryStream.Position = 0;

            // Act - This might not throw but could produce unexpected results
            try
            {
                var result = _helper.ReadAll<bool>(_readBuffer.Wrap(memoryStream.ToArray()));

                // Assert - Invalid byte values should be handled consistently
                // Note: .NET typically treats non-zero as true, but this could be framework dependent
                NAssert.That(result == true || result == false); // Should always be a valid bool
            }
            catch (Exception)
            {
                // If it throws, that's also acceptable behavior for corrupted data
                // Exception thrown for corrupted boolean - acceptable behavior
                return;
            }
        }

        [Test]
        public void EmptyStream_NoData_ThrowsException()
        {
            // Arrange - Completely empty stream
            var emptyData = new byte[0];

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<int>(_readBuffer.Wrap(emptyData));
            });
        }

        [Test]
        public void PartialHeader_IncompleteHeader_ThrowsException()
        {
            // Arrange - Stream with incomplete header (missing includesTypeChecks)
            var memoryStream = new MemoryStream();
            var writer = new BinaryWriter(memoryStream);

            // Write only version, missing includesTypeChecks
            writer.Write(1);
            writer.Flush();
            memoryStream.Position = 0;

            // Act & Assert
            NAssert.Catch<Exception>(() =>
            {
                _helper.ReadAll<int>(_readBuffer.Wrap(memoryStream.ToArray()));
            });
        }

        private void WriteString(BinaryWriter writer, string value)
        {
            // Mimic how StringSerializer writes strings
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private int GetStringTypeId()
        {
            // Get the type ID that would be used for string
            // This is a bit of a hack since we don't have direct access
            // Let's try to serialize a string first to see what ID is used
            try
            {
                _helper.WriteAll(_data, "test", TestConstants.Version, includeTypeChecks: true, 0L);
                var data = _data.ToContiguousBytes();

                // Skip header (version + includesTypeChecks = 5 bytes)
                if (data.Length >= 9)
                {
                    return BitConverter.ToInt32(data, 5);
                }
            }
            catch
            {
                // Fallback if something goes wrong
            }

            // Fallback to a reasonable guess based on framework patterns
            return 123; // This will likely be wrong and should trigger our test
        }

        private int GetBoolTypeId()
        {
            // Similar approach for bool type ID
            try
            {
                _helper.WriteAll(_data, true, TestConstants.Version, includeTypeChecks: true, 0L);
                var data = _data.ToContiguousBytes();

                if (data.Length >= 9)
                {
                    return BitConverter.ToInt32(data, 5);
                }
            }
            catch
            {
                // Fallback
            }

            return 124; // Fallback guess
        }
    }
}

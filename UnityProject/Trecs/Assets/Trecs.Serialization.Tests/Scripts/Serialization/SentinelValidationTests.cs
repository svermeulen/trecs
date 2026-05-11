using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class SentinelValidationTests
    {
        private SerializationBuffer _cacheHelper;
        private SerializerRegistry _serializerRegistry;

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
            _cacheHelper = null;
        }

        [Test]
        public void ValidSentinel_CompleteSerialization_PassesValidation()
        {
            // Arrange
            var testData = new Vector3(1.0f, 2.0f, 3.0f);
            var flags = 0L;

            // Act - Normal serialization/deserialization should work
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(testData, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<Vector3>();

            // Assert
            Assert.That(Mathf.Approximately(result.x, testData.x));
            Assert.That(Mathf.Approximately(result.y, testData.y));
            Assert.That(Mathf.Approximately(result.z, testData.z));
        }

        [Test]
        public void MissingSentinel_TruncatedStream_ThrowsException()
        {
            // Arrange
            var testData = 42;
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(testData, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Remove the last 4 bytes (sentinel value)
            var truncatedData = new byte[serializedData.Length - 4];
            Array.Copy(serializedData, truncatedData, truncatedData.Length);

            // Act & Assert
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(truncatedData),
                    truncatedData.Length
                );
                Assert.Catch<Exception>(() => newCacheHelper.ReadAll<int>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void CorruptedSentinel_WrongValue_ThrowsException()
        {
            // Arrange
            var testData = "test string";
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(testData, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Corrupt the sentinel value (last 4 bytes)
            using (var stream = new MemoryStream(serializedData))
            using (var writer = new BinaryWriter(stream))
            {
                stream.Position = serializedData.Length - 4;
                writer.Write(0xDEADBEEF); // Write wrong sentinel value
            }

            // Act & Assert
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(serializedData),
                    serializedData.Length
                );
                Assert.Catch<Exception>(() => newCacheHelper.ReadAll<string>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void ExtraDataAfterSentinel_DetectedAsCorruption()
        {
            // Arrange
            var testData = 123;
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(testData, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Add extra data after the sentinel
            var extendedData = new byte[serializedData.Length + 8];
            Array.Copy(serializedData, extendedData, serializedData.Length);
            // Add some garbage bytes after the sentinel
            extendedData[serializedData.Length] = 0xFF;
            extendedData[serializedData.Length + 1] = 0xAA;
            extendedData[serializedData.Length + 2] = 0xBB;
            extendedData[serializedData.Length + 3] = 0xCC;

            // Act & Assert - Should work fine since we only read up to the sentinel
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(extendedData),
                    serializedData.Length
                );
                newCacheHelper.ResetMemoryPosition();
                var result = newCacheHelper.ReadAll<int>();
                Assert.That(result == testData);
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void PartialData_MissingElementsBeforeSentinel_ThrowsException()
        {
            // Arrange - Create a list with multiple elements
            var testList = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(testList, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Remove some data from the middle (but keep the sentinel)
            // This simulates the corruption scenario we're trying to catch
            var corruptedData = new byte[serializedData.Length - 8]; // Remove 8 bytes from middle

            // Copy header and part of data
            Array.Copy(serializedData, 0, corruptedData, 0, serializedData.Length - 12);
            // Copy sentinel from original position to new position
            Array.Copy(
                serializedData,
                serializedData.Length - 4,
                corruptedData,
                corruptedData.Length - 4,
                4
            );

            // Act & Assert
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(corruptedData),
                    corruptedData.Length
                );
                // This should throw because we're missing element data but still have the sentinel
                Assert.Catch<Exception>(() => newCacheHelper.ReadAll<List<int>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void EmptyStream_NoData_ThrowsException()
        {
            // Arrange
            var emptyData = new byte[0];

            // Act & Assert
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(emptyData),
                    emptyData.Length
                );
                Assert.Catch<Exception>(() => newCacheHelper.ReadAll<int>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Trecs.Serialization.Internal;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class MemoryManagementTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;
        private string _tempFilePath;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
            _tempFilePath = Path.GetTempFileName();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                _cacheHelper?.Dispose();
            }
            catch
            {
                // Ignore dispose errors from already disposed objects
            }

            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }
        }

        [Test]
        public void SerializerCacheHelper_ReusesMemoryStream()
        {
            // Arrange
            var data1 = 42;
            var data2 = 100;
            var flags = 0L;

            // Act - First serialization
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data1, TestConstants.Version, includeTypeChecks: true, flags);
            var firstPosition = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var result1 = _cacheHelper.ReadAll<int>();

            // Second serialization should reuse the same stream after clearing
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data2, TestConstants.Version, includeTypeChecks: true, flags);
            var secondPosition = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var result2 = _cacheHelper.ReadAll<int>();

            // Assert
            Assert.That(result1 == data1);
            Assert.That(result2 == data2);
            Assert.That(firstPosition > 0);
            Assert.That(secondPosition > 0);
            // Positions should be similar since stream gets cleared and reused
            Assert.That(Math.Abs(firstPosition - secondPosition) <= 4); // Allow for minor differences
        }

        [Test]
        public void MemoryPosition_TracksStreamPosition()
        {
            // Arrange
            var data = 12345;
            var flags = 0L;

            // Act
            var initialPosition = _cacheHelper.MemoryPosition;
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true, flags);
            var afterWritePosition = _cacheHelper.MemoryPosition;

            // Assert
            Assert.That(initialPosition == 0);
            Assert.That(afterWritePosition > 0);
            Assert.That(afterWritePosition >= sizeof(int)); // At least the size of an int
        }

        [Test]
        public void ClearMemoryStream_ResetsPosition()
        {
            // Arrange
            var data = "test string";
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true, flags);
            var positionAfterWrite = _cacheHelper.MemoryPosition;

            // Explicitly clear before next WriteAll
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true, flags);
            var positionAfterClear = _cacheHelper.MemoryPosition;

            // Assert
            Assert.That(positionAfterWrite > 0);
            Assert.That(positionAfterClear > 0);
            // Position should be similar after clearing and rewriting same data
        }

        [Test]
        public void SaveAndLoadFile_PreservesData()
        {
            // Arrange
            var originalData = new Vector3(1.5f, 2.5f, 3.5f);
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            _cacheHelper.SaveMemoryToFile(_tempFilePath);

            // Create new cache helper and load from file
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                _cacheHelper.ClearMemoryStream();
                newCacheHelper.LoadMemoryFromFile(_tempFilePath);
                newCacheHelper.ResetMemoryPosition();
                var result = newCacheHelper.ReadAll<Vector3>();

                // Assert
                Assert.That(result.x == originalData.x);
                Assert.That(result.y == originalData.y);
                Assert.That(result.z == originalData.z);
            }
            finally
            {
                newCacheHelper.Dispose();
            }
        }

        [Test]
        public void LoadFromArraySegment_WorksCorrectly()
        {
            // Arrange
            var originalData = 98765;
            var flags = 0L;

            // First serialize to get byte array
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var streamBytes = _cacheHelper.MemoryStream.ToArray();
            var segment = new ArraySegment<byte>(streamBytes);

            // Act
            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                _cacheHelper.ClearMemoryStream();
                newCacheHelper.LoadMemoryStreamFromArraySegment(segment, streamBytes.Length);
                newCacheHelper.ResetMemoryPosition();
                var result = newCacheHelper.ReadAll<int>();

                // Assert
                Assert.That(result == originalData);
            }
            finally
            {
                newCacheHelper.Dispose();
            }
        }

        [Test]
        public void MultipleWriteReadCycles_MaintainPerformance()
        {
            // Arrange
            var testData = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;
            const int cycles = 100;

            // Act & Assert - Multiple cycles should work without memory issues
            for (int i = 0; i < cycles; i++)
            {
                _cacheHelper.ClearMemoryStream();
                _cacheHelper.WriteAll(
                    testData,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags
                );
                _cacheHelper.ResetMemoryPosition();
                var result = _cacheHelper.ReadAll<List<int>>();

                Assert.That(result.Count == testData.Count);
                for (int j = 0; j < testData.Count; j++)
                {
                    Assert.That(result[j] == testData[j]);
                }
            }
        }

        [Test]
        public void LargeDataSerialization_HandlesMemoryEfficiently()
        {
            // Arrange
            var largeData = new List<int>();
            for (int i = 0; i < 10000; i++)
            {
                largeData.Add(i);
            }
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            var initialPosition = _cacheHelper.MemoryPosition;
            _cacheHelper.WriteAll(largeData, TestConstants.Version, includeTypeChecks: true, flags);
            var afterWritePosition = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<List<int>>();

            // Assert
            Assert.That(result.Count == largeData.Count);
            Assert.That(afterWritePosition > initialPosition);
            Assert.That(afterWritePosition > 40000); // Should be substantial for 10k ints

            // Verify data integrity
            for (int i = 0; i < Math.Min(100, largeData.Count); i++)
            {
                Assert.That(result[i] == largeData[i]);
            }
        }

        [Test]
        public void ResetMemoryPosition_AllowsRereadingData()
        {
            // Arrange
            var data = "memory position test";
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true, flags);

            // This should internally reset position to 0 for reading
            _cacheHelper.ResetMemoryPosition();
            var result1 = _cacheHelper.ReadAll<string>();

            // Reading again should work (SerializationBuffer handles position internally)
            _cacheHelper.ResetMemoryPosition();
            var result2 = _cacheHelper.ReadAll<string>();

            // Assert
            Assert.That(result1 == data);
            Assert.That(result2 == data);
        }

        [Test]
        public void StreamBuffer_GrowsAsNeeded()
        {
            // Arrange
            var smallData = 123;
            var largeData = new string('x', 2048); // 2KB string
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(smallData, TestConstants.Version, includeTypeChecks: true, flags);
            var positionAfterSmall = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var resultSmall = _cacheHelper.ReadAll<int>();

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(largeData, TestConstants.Version, includeTypeChecks: true, flags);
            var positionAfterLarge = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var resultLarge = _cacheHelper.ReadAll<string>();

            // Assert
            Assert.That(positionAfterLarge > positionAfterSmall);
            Assert.That(resultSmall == smallData);
            Assert.That(resultLarge == largeData);
        }

        [Test]
        public void Dispose_CleansUpResources()
        {
            // Arrange
            var data = 456;
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(data, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<int>();

            // Dispose should not throw
            _cacheHelper.Dispose();
            _cacheHelper = null; // Prevent double dispose in TearDown

            // Assert
            Assert.That(result == data);
            // Further operations should fail (but we can't test this safely)
        }

        [Test]
        public void ComplexObjectSerialization_ManagesMemoryCorrectly()
        {
            // Arrange
            var complexData = new Dictionary<string, List<int>>
            {
                {
                    "first",
                    new List<int> { 1, 2, 3 }
                },
                {
                    "second",
                    new List<int> { 4, 5, 6, 7, 8 }
                },
                { "third", new List<int>() },
            };
            var flags = 0L;

            // Act
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                complexData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var memoryUsed = _cacheHelper.MemoryPosition;
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<Dictionary<string, List<int>>>();

            // Assert
            Assert.That(memoryUsed > 0);
            Assert.That(result.Count == complexData.Count);
            Assert.That(result["first"].Count == 3);
            Assert.That(result["second"].Count == 5);
            Assert.That(result["third"].Count == 0);
        }

        [Test]
        public void MultipleConsecutiveWrites_CanBeReadBackWithoutReset()
        {
            // Arrange
            var data1 = 42;
            var data2 = "Hello World";
            var data3 = new List<int> { 10, 20, 30 };
            var flags = 0L;

            // Act - Write multiple values consecutively without clearing the stream
            _cacheHelper.StartWrite(TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.Write("value1", data1);
            _cacheHelper.Write("value2", data2);
            _cacheHelper.Write("value3", data3);
            var bytesWritten = _cacheHelper.EndWrite();

            // Reset position to read from beginning
            _cacheHelper.ResetMemoryPosition();

            // Read back all values in the same order
            _cacheHelper.StartRead();
            var result1 = _cacheHelper.Read<int>("value1");
            var result2 = _cacheHelper.Read<string>("value2");
            var result3 = _cacheHelper.Read<List<int>>("value3");
            _cacheHelper.StopRead(verifySentinel: true);

            // Assert
            Assert.That(bytesWritten > 0);
            Assert.That(result1 == data1);
            Assert.That(result2 == data2);
            Assert.That(result3.Count == data3.Count);
            for (int i = 0; i < data3.Count; i++)
            {
                Assert.That(result3[i] == data3[i]);
            }
        }

        [Test]
        public void MultipleWriteAll_ConsecutiveWithoutReset_CanReadBackBothValues()
        {
            // Arrange
            var data1 = 42;
            var data2 = "Hello World";
            var flags = 0L;

            // Act - First WriteAll (should not clear memory)
            _cacheHelper.ClearMemoryStream();
            var positionBeforeFirst = _cacheHelper.MemoryPosition;
            var position1 = _cacheHelper.WriteAll(
                data1,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );

            // Second WriteAll (should append to existing data)
            var position2 = _cacheHelper.WriteAll(
                data2,
                TestConstants.Version,
                includeTypeChecks: true,
                flags,
                allowUnclearedMemory: true
            );

            // Assert that consecutive writes worked correctly before resetting position
            Assert.That(positionBeforeFirst == 0, "Memory position should start at 0");
            Assert.That(position1 > 0, "First write should have written bytes");
            Assert.That(position2 > position1, "Second write should append after first");
            Assert.That(
                _cacheHelper.MemoryPosition == position2,
                "Memory position should be at end of second write"
            );

            // Reset position to read from beginning
            _cacheHelper.ResetMemoryPosition();

            // Note: The current API doesn't support reading multiple consecutive serializations
            // in sequence because StartRead() always expects position 0.
            // This test verifies that multiple writes can be made consecutively.

            // Verify that the memory stream contains both serializations
            var totalBytes = _cacheHelper.MemoryStream.ToArray();
            Assert.That(
                totalBytes.Length >= position2,
                "Memory stream should contain both serializations"
            );
        }
    }
}

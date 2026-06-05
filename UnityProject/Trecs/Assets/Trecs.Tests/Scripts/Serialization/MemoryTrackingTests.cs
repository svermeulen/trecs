#if TRECS_INTERNAL_CHECKS && DEBUG
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Trecs;
using Trecs.Internal;
using Trecs.Collections;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class MemoryTrackingTests
    {
        SerializerRegistry _serializerRegistry;
        BinarySerializationWriter _writer;
        SerializationData _data;
        MemoryStream _memoryStream;

        [SetUp]
        public void Setup()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);

            // Register test-specific serializers. (int/float are unmanaged,
            // so the *Managed variants' class constraint rejects them.)
            _serializerRegistry.RegisterSerializer(new ListSerializerUnmanaged<int>());
            _serializerRegistry.RegisterSerializer(
                new IterableDictionarySerializerUnmanaged<int, float>()
            );

            _writer = new BinarySerializationWriter(_serializerRegistry);
            _data = new SerializationData();
            _memoryStream = new MemoryStream();
        }

        [TearDown]
        public void TearDown()
        {
            _memoryStream?.Dispose();
        }

        [Test]
        public void TestMemoryTrackingReport()
        {
            // Reset the writer
            _writer.Start(
                _data,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some test data
            _writer.Write("PlayerPosition", new Vector3(1, 2, 3));
            _writer.Write("PlayerHealth", 100);
            _writer.WriteString("PlayerName", "TestPlayer");

            // Create a list of items
            var items = new List<int> { 1, 2, 3, 4, 5 };
            _writer.Write("Inventory", items);

            // Create a dictionary
            var stats = new IterableDictionary<int, float>
            {
                { 1, 10.5f },
                { 2, 8.2f },
                { 3, 12.0f },
            };
            _writer.Write("PlayerStats", stats);

            // Finalize the write
            _writer.Complete();
            _data.WriteContiguousTo(_memoryStream);

            // Get the memory report
            var report = _writer.GetMemoryReport();

            // Print the report
            Debug.Log(report);

            // Verify the report contains expected sections
            NAssert.That(report, Does.Contain("=== Serialization Memory Breakdown ==="));
            NAssert.That(report, Does.Contain("Total:"));
            NAssert.That(report, Does.Contain("Header & Metadata:"));
            NAssert.That(report, Does.Contain("Serialized Data:"));

            NAssert.That(report, Does.Contain("Type IDs:"));

            // Verify specific fields are tracked
            NAssert.That(report, Does.Contain("playerPosition"));
            NAssert.That(report, Does.Contain("playerHealth"));
            NAssert.That(report, Does.Contain("playerName"));
            NAssert.That(report, Does.Contain("inventory"));
            NAssert.That(report, Does.Contain("playerStats"));
        }

        [Test]
        public void TestMemoryTrackingDisabled()
        {
            _writer.Start(
                _data,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: false
            );
            _writer.Write("Test", 42);
            _writer.Complete();
            _data.WriteContiguousTo(_memoryStream);

            var report = _writer.GetMemoryReport();
            NAssert.That(report, Is.EqualTo("Memory tracking is disabled"));
        }

        [Test]
        public void TestMemoryTrackingWithDelta()
        {
            // Enable memory tracking
            // Reset the writer
            _writer.Start(
                _data,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some delta data
            var value = new Vector3(1, 2, 3);
            var baseValue = new Vector3(1, 2, 2); // Different Z value

            _writer.WriteDelta("Position", value, baseValue);

            // Write identical values (should be optimized)
            _writer.WriteDelta("Rotation", Vector3.zero, Vector3.zero);

            // Finalize
            _writer.Complete();
            _data.WriteContiguousTo(_memoryStream);

            // Get report
            var report = _writer.GetMemoryReport();
            Debug.Log(report);

            // Verify delta fields are tracked
            NAssert.That(report, Does.Contain("position"));

            // Zero-byte entries (like unchanged rotation) should be filtered out
            NAssert.That(report, Does.Not.Contain("rotation"));
        }

        [Test]
        public void TestMemoryTrackingFiltersZeroByteEntries()
        {
            // Reset the writer
            _writer.Start(
                _data,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some data that will have zero bytes (identical values in delta)
            var value1 = new Vector3(1, 2, 3);
            var value2 = new Vector3(1, 2, 3); // Identical - should result in 0 bytes for delta

            _writer.WriteDelta("UnchangedField", value1, value2);
            _writer.Write("NormalField", 42);

            // Finalize
            _writer.Complete();
            _data.WriteContiguousTo(_memoryStream);

            // Get report
            var report = _writer.GetMemoryReport();
            Debug.Log(report);

            // Zero-byte entries should be filtered out
            NAssert.That(report, Does.Not.Contain("unchangedField"));
            NAssert.That(report, Does.Contain("normalField"));
        }
    }
}
#endif

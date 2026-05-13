#if TRECS_INTERNAL_CHECKS
using Trecs;
using Trecs.Internal;
using Trecs.Collections;
using Trecs.Serialization.Internal;

namespace Trecs.Serialization.Tests
{
    [TestFixture]
    public class MemoryTrackingTests
    {
        SerializerRegistry _serializerRegistry;
        BinarySerializationWriter _writer;
        MemoryStream _memoryStream;
        BinaryWriter _binaryWriter;

        [SetUp]
        public void Setup()
        {
            _serializerRegistry =
                Trecs.Serialization.Internal.SerializationFactory.CreateRegistry();

            // Register test-specific serializers
            _serializerRegistry.RegisterSerializer<ListSerializer<int>>();
            _serializerRegistry.RegisterSerializer<DictionarySerializer<string, float>>();

            _writer = new BinarySerializationWriter(_serializerRegistry);
            _memoryStream = new MemoryStream();
            _binaryWriter = new BinaryWriter(_memoryStream);
        }

        [TearDown]
        public void TearDown()
        {
            _writer?.Dispose();
            _binaryWriter?.Dispose();
            _memoryStream?.Dispose();
        }

        [Test]
        public void TestMemoryTrackingReport()
        {
            // Reset the writer
            _writer.Start(
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some test data
            _writer.Write("playerPosition", new Vector3(1, 2, 3));
            _writer.Write("playerHealth", 100);
            _writer.WriteString("playerName", "TestPlayer");

            // Create a list of items
            var items = new List<int> { 1, 2, 3, 4, 5 };
            _writer.Write("inventory", items);

            // Create a dictionary
            var stats = new Dictionary<string, float>
            {
                { "strength", 10.5f },
                { "agility", 8.2f },
                { "intelligence", 12.0f },
            };
            _writer.Write("playerStats", stats);

            // Finalize the write
            _writer.Complete(_binaryWriter);

            // Get the memory report
            var report = _writer.GetMemoryReport();

            // Print the report
            Debug.Log(report);

            // Verify the report contains expected sections
            NUnit.Framework.Assert.That(
                report,
                Does.Contain("=== Serialization Memory Breakdown ===")
            );
            NUnit.Framework.Assert.That(report, Does.Contain("Total:"));
            NUnit.Framework.Assert.That(report, Does.Contain("Header & Metadata:"));
            NUnit.Framework.Assert.That(report, Does.Contain("Serialized Data:"));

            NUnit.Framework.Assert.That(report, Does.Contain("Type IDs:"));

            // Verify specific fields are tracked
            NUnit.Framework.Assert.That(report, Does.Contain("playerPosition"));
            NUnit.Framework.Assert.That(report, Does.Contain("playerHealth"));
            NUnit.Framework.Assert.That(report, Does.Contain("playerName"));
            NUnit.Framework.Assert.That(report, Does.Contain("inventory"));
            NUnit.Framework.Assert.That(report, Does.Contain("playerStats"));
        }

        [Test]
        public void TestMemoryTrackingDisabled()
        {
            _writer.Start(
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: false
            );
            _writer.Write("test", 42);
            _writer.Complete(_binaryWriter);

            var report = _writer.GetMemoryReport();
            NUnit.Framework.Assert.That(report, Is.EqualTo("Memory tracking is disabled"));
        }

        [Test]
        public void TestMemoryTrackingWithDelta()
        {
            // Enable memory tracking
            // Reset the writer
            _writer.Start(
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some delta data
            var value = new Vector3(1, 2, 3);
            var baseValue = new Vector3(1, 2, 2); // Different Z value

            _writer.WriteDelta("position", value, baseValue);

            // Write identical values (should be optimized)
            _writer.WriteDelta("rotation", Vector3.zero, Vector3.zero);

            // Finalize
            _writer.Complete(_binaryWriter);

            // Get report
            var report = _writer.GetMemoryReport();
            Debug.Log(report);

            // Verify delta fields are tracked
            NUnit.Framework.Assert.That(report, Does.Contain("position"));

            // Zero-byte entries (like unchanged rotation) should be filtered out
            NUnit.Framework.Assert.That(report, Does.Not.Contain("rotation"));
        }

        [Test]
        public void TestMemoryTrackingFiltersZeroByteEntries()
        {
            // Reset the writer
            _writer.Start(
                TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L,
                enableMemoryTracking: true
            );

            // Write some data that will have zero bytes (identical values in delta)
            var value1 = new Vector3(1, 2, 3);
            var value2 = new Vector3(1, 2, 3); // Identical - should result in 0 bytes for delta

            _writer.WriteDelta("unchangedField", value1, value2);
            _writer.Write("normalField", 42);

            // Finalize
            _writer.Complete(_binaryWriter);

            // Get report
            var report = _writer.GetMemoryReport();
            Debug.Log(report);

            // Zero-byte entries should be filtered out
            NUnit.Framework.Assert.That(report, Does.Not.Contain("unchangedField"));
            NUnit.Framework.Assert.That(report, Does.Contain("normalField"));
        }
    }
}
#endif

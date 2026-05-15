#if TRECS_INTERNAL_CHECKS && DEBUG

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Trecs;
using Trecs.Internal;
using Trecs.Collections;
using Svkj;

namespace Trecs.Tests
{
    /// <summary>
    /// Example demonstrating how to use the memory tracking feature to debug serialization size issues.
    /// Memory tracking is only available when TRECS_INTERNAL_CHECKS is defined.
    /// </summary>
    public static class MemoryTrackingExample
    {
        public static void DemonstrateMemoryTracking(SerializerRegistry serializerRegistry)
        {
            // Create a writer with memory tracking enabled
            var writer = new BinarySerializationWriter(serializerRegistry);

            // Create output stream
            using (var memoryStream = new MemoryStream())
            using (var binaryWriter = new BinaryWriter(memoryStream))
            {
                // Reset the writer
                writer.Start(
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags: 0L,
                    enableMemoryTracking: true
                );

                // Write various types of data
                writer.Write("PlayerPosition", new Vector3(10.5f, 20.3f, 30.1f));
                writer.Write("PlayerHealth", 100);
                writer.Write("PlayerLevel", 42);
                writer.WriteString("PlayerName", "JohnDoe123");

                // Write a collection
                var inventory = new List<int> { 1001, 1002, 1003, 1004, 1005 };
                writer.Write("Inventory", inventory);

                // Write a dictionary
                var stats = new Dictionary<string, float>
                {
                    { "strength", 15.5f },
                    { "agility", 12.3f },
                    { "intelligence", 18.7f },
                    { "stamina", 14.2f },
                };
                writer.Write("Stats", stats);

                // Finalize the write - this will output the memory report
                writer.Complete(binaryWriter);

                // You can also get the report manually
                var report = writer.GetMemoryReport();
                Debug.Log($"Serialized data size: {memoryStream.Length} bytes");
                Debug.Log($"Memory breakdown:\n{report}");
            }

            writer.Dispose();
        }

        public static void DemonstrateMemoryTrackingForLargeData(
            SerializerRegistry serializerRegistry
        )
        {
            var writer = new BinarySerializationWriter(serializerRegistry);

            using (var memoryStream = new MemoryStream())
            using (var binaryWriter = new BinaryWriter(memoryStream))
            {
                writer.Start(
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags: 0L,
                    enableMemoryTracking: true
                );

                // Simulate a large game state
                for (int i = 0; i < 100; i++)
                {
                    writer.Write($"entity_{i}_position", new Vector3(i, i * 2, i * 3));
                    writer.Write($"entity_{i}_health", 100 - i);
                }

                // Large collection
                var largeList = new List<int>();
                for (int i = 0; i < 1000; i++)
                {
                    largeList.Add(i * 17);
                }
                writer.Write("LargeDataSet", largeList);

                writer.Complete(binaryWriter);

                // The report will show which fields are consuming the most memory
                var report = writer.GetMemoryReport();
                Debug.Log($"Large data serialization breakdown:\n{report}");
            }

            writer.Dispose();
        }
    }
}

#endif

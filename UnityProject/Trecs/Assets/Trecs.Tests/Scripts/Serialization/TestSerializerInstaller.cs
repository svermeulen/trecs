using System.Collections.Generic;
using Trecs.Internal;
using Trecs.Serialization;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    public static class TestSerializerInstaller
    {
        public static void RegisterTestCollectionSerializers(SerializerRegistry registry)
        {
            // Collection serializers for testing
            registry.RegisterSerializer<ListSerializer<string>>();
            registry.RegisterSerializer<QueueSerializer<int>>();

            // Array serializers for malformed collection tests
            registry.RegisterSerializer<BlitArraySerializer<int>>();
            registry.RegisterSerializer<ArraySerializer<string>>();

            // Nested collection serializers for malformed tests
            registry.RegisterSerializer<ListSerializer<List<int>>>();

            // Test class serializer for null serialization tests.
            registry.RegisterSerializer(new TestClassSerializer());

            // List blit serializers for performance testing
            registry.RegisterSerializer<ListBlitSerializer<int>>();
            registry.RegisterSerializer<ListBlitSerializer<float>>();
            registry.RegisterSerializer<ListBlitSerializer<Vector3>>();
            registry.RegisterSerializer<ListBlitSerializer<int2>>();
            registry.RegisterSerializer<ListBlitSerializer<byte>>();

            // IterableDictionary serializers for blit performance testing
            registry.RegisterSerializer<IterableDictionaryUnmanagedSerializer<int, float>>();
            registry.RegisterSerializer<IterableDictionaryUnmanagedSerializer<int, int>>();
            registry.RegisterSerializer<IterableDictionaryUnmanagedSerializer<int, Vector3>>();
            registry.RegisterSerializer<IterableDictionaryUnmanagedSerializer<int2, float3>>();
            registry.RegisterSerializer<IterableDictionarySerializer<int, string>>();

            // UnsafeList serializers for round-trip tests.
            registry.RegisterSerializer<UnsafeListSerializer<float>>();
            registry.RegisterSerializer<UnsafeListSerializer<Vector3>>();
        }

        public static SerializerRegistry CreateTestRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            RegisterTestCollectionSerializers(registry);
            return registry;
        }

        public static SerializerRegistry CreateJsonTestRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return registry;
        }

        public class TestClassSerializer : ISerializer<NullSerializationTests.TestClass>
        {
            public TestClassSerializer() { }

            public void Serialize(
                in NullSerializationTests.TestClass value,
                ISerializationWriter writer
            )
            {
                writer.Write("Value", value.Value);
                writer.WriteString("Name", value.Name);
            }

            public void Deserialize(
                ref NullSerializationTests.TestClass value,
                ISerializationReader reader
            )
            {
                if (value == null)
                    value = new NullSerializationTests.TestClass();

                int tempValue = 0;
                reader.Read("Value", ref tempValue);
                value.Value = tempValue;
                value.Name = reader.ReadString("Name");
            }
        }
    }
}

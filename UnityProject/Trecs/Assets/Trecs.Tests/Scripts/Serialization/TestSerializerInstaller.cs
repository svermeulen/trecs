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
            registry.RegisterSerializer<ListSerializerManaged<string>>();
            registry.RegisterSerializer<QueueSerializerManaged<string>>();
            registry.RegisterSerializer<QueueSerializerUnmanaged<int>>();

            // Array serializers for malformed collection tests
            registry.RegisterSerializer<ArraySerializerUnmanaged<int>>();
            registry.RegisterSerializer<ArraySerializerManaged<string>>();

            // Nested collection serializers for malformed tests
            registry.RegisterSerializer<ListSerializerManaged<List<int>>>();

            // Test class serializer for null serialization tests.
            registry.RegisterSerializer(new TestClassSerializer());

            // List blit serializers for performance testing
            registry.RegisterSerializer<ListSerializerUnmanaged<int>>();
            registry.RegisterSerializer<ListSerializerUnmanaged<float>>();
            registry.RegisterSerializer<ListSerializerUnmanaged<Vector3>>();
            registry.RegisterSerializer<ListSerializerUnmanaged<int2>>();
            registry.RegisterSerializer<ListSerializerUnmanaged<byte>>();

            // IterableDictionary serializers for blit performance testing
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<int, float>>();
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<int, int>>();
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<int, Vector3>>();
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<int2, float3>>();
            registry.RegisterSerializer<IterableDictionarySerializerManaged<int, string>>();

            // IterableHashSet blit serializers
            registry.RegisterSerializer<IterableHashSetSerializer<int>>();
            registry.RegisterSerializer<IterableHashSetSerializer<int2>>();

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
